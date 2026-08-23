using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentFox.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using TradingAgent.Config;
using TradingAgent.Models;
using TradingAgent.Observability;
using TradingAgent.Risk;
using TradingAgent.Watchlist;

namespace TradingAgent.Broker;

/// <summary>
/// Automates the Arif Habib Kornasif (AHK) trading portal using a single persistent
/// Chromium session (PuppeteerSharp). The session survives across orders so the
/// broker only logs in once per run.
///
/// Confirmed AHK field IDs (from live portal inspection):
///   Login : #ps_userid       #ps_password
///   BUY   : #buysymbol       #buyvolume      #buyprice      #buylimitprice    #buyPIN
///   SELL  : #sellsymbol      #sellvolume     #sellprice     #selllimitprice   #sellPIN
///
/// Submit button: has NO id. Identified by its exact visible text "BUY"/"SELL" (a substring match
/// is unsafe — the toolbar "Buy Order"/"Sell Order" buttons also contain "Buy" and only re-open the
/// dialog). Override via Ahk.BuySubmitSelector / SellSubmitSelector if it ever gains a stable id.
/// </summary>
public sealed class AhkBroker : IAsyncDisposable
{
    // Live view (appsettings + browser-editable runtime overlay). Read at use time so
    // credential/portal changes made in the web UI apply to the next browser session.
    private readonly IRuntimePluginOptions<AhkConfig> _config;
    private readonly ILogger<AhkBroker> _logger;
    private readonly string _workspaceRoot;

    /// <summary>
    /// Live view for the UI's activity panel. Optional so the opt-in browser integration tests can
    /// build a broker with three arguments, as they always have.
    /// </summary>
    private readonly TradingActivityLog? _activity;

    // Serializes every browser interaction. The host can process channel messages concurrently,
    // but there is a single shared _page — concurrent orders would interleave field-fills and
    // submits and corrupt each other. All public entry points take this gate.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Outstanding <see cref="LeaseSession"/> holders. While this is above zero the on-demand
    /// lifecycle does not close the browser between operations — see <see cref="LeaseSession"/>.
    /// </summary>
    private int _sessionLeases;

    private IBrowser? _browser;
    private IPage? _page;
    private bool _initialized;

    public AhkBroker(
        IRuntimePluginOptions<AhkConfig> config,
        IConfiguration configuration,
        ILogger<AhkBroker> logger,
        TradingActivityLog? activity = null)
    {
        _config = config;
        _logger = logger;
        _activity = activity;
        _workspaceRoot = ComputeWorkspaceRoot(configuration);
    }

    /// <summary>
    /// Mirrors the host's WorkspaceManager: the workspace root is the first non-empty entry of the
    /// "Workspaces" config array (or AppContext.BaseDirectory when none is set). Relative SessionDir /
    /// LogDir values resolve against this so the browser profile, logs and screenshots live in the
    /// current workspace rather than the app's bin folder.
    /// </summary>
    private static string ComputeWorkspaceRoot(IConfiguration configuration)
    {
        var first = configuration.GetSection("Workspaces").Get<string[]>()
            ?.FirstOrDefault(w => !string.IsNullOrWhiteSpace(w));

        return string.IsNullOrWhiteSpace(first) ? AppContext.BaseDirectory : Path.GetFullPath(first);
    }

    // ── Initialization ────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures a live browser session, taking the gate. Pass <paramref name="forceRestart"/> to tear
    /// down and relaunch even if one already appears healthy.
    /// </summary>
    public async Task InitializeAsync(bool forceRestart = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureBrowserAsync(forceRestart, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tears down any live browser and deletes the persisted profile so the next session performs a
    /// fresh login. Called when broker credentials change at runtime — the persisted profile keeps
    /// the OLD authenticated session alive, so it must not survive a credential rotation.
    /// </summary>
    public async Task InvalidateSessionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var sessionDir = ResolvePath(_config.Current.SessionDir);
            await TeardownAsync();
            KillPreviousBrowser(sessionDir);
            try
            {
                if (Directory.Exists(sessionDir))
                    Directory.Delete(sessionDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[AhkBroker] Could not delete session profile {Dir}; the next launch will still re-login if the old session is invalid.",
                    sessionDir);
            }

            _logger.LogInformation(
                "[AhkBroker] Session invalidated — the next broker operation will log in with the current credentials.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Opens a broker session and verifies authentication without opening an order dialog or
    /// submitting an order. Intended for startup diagnostics and opt-in integration tests.
    /// </summary>
    public async Task<AhkLoginVerificationResult> VerifyLoginAsync(
        bool forceRestart = false,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var cfg = _config.Current;
            if (string.IsNullOrWhiteSpace(cfg.PortalUrl))
                throw new InvalidOperationException("AHK PortalUrl is required.");

            await EnsureBrowserAsync(forceRestart, ct);
            if (!IsHealthy())
                throw new InvalidOperationException("Browser unavailable after launch.");

            if (!await IsLoggedInAsync())
            {
                if (string.IsNullOrWhiteSpace(cfg.Username) || string.IsNullOrWhiteSpace(cfg.Password))
                    throw new InvalidOperationException(
                        "AHK Username and Password are required when no authenticated session exists.");
                await LoginAsync();
            }

            if (!await IsLoggedInAsync())
                throw new InvalidOperationException(
                    $"AHK login could not be verified with selector '{cfg.LoggedInSelector}'.");

            return new AhkLoginVerificationResult(
                true,
                _page?.Url ?? cfg.PortalUrl,
                cfg.LoggedInSelector,
                DateTime.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// (Re)launches the browser if needed. ASSUMES the caller holds <see cref="_gate"/> — it must
    /// never take the gate itself (it is also called from the order path which already holds it).
    /// Any stray browser this broker left running on a previous run is killed first, and stale
    /// profile lock files are removed, so "session already running / profile in use" can never
    /// silently block an order.
    /// </summary>
    private async Task EnsureBrowserAsync(bool forceRestart, CancellationToken ct = default)
    {
        if (!forceRestart && IsHealthy()) return;

        var cfg        = _config.Current;
        var sessionDir = ResolvePath(cfg.SessionDir);
        Directory.CreateDirectory(sessionDir);
        Directory.CreateDirectory(ResolvePath(cfg.LogDir));

        await TeardownAsync();            // close/kill our own browser if one is half-alive
        KillPreviousBrowser(sessionDir); // kill a Chrome WE launched on a prior run that outlived us
        CleanProfileLocks(sessionDir);   // remove stale Singleton*/lockfile entries

        var executablePath = await ResolveBrowserPathAsync(cfg, ct);

        var launchOptions = new LaunchOptions
        {
            Headless       = cfg.Headless,
            ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath,
            UserDataDir    = sessionDir,
            Args           = ["--no-sandbox", "--disable-setuid-sandbox"]
        };

        _browser = await LaunchWithRetryAsync(launchOptions, sessionDir);

        var pages = await _browser.PagesAsync();
        _page = pages.Length > 0 ? pages[0] : await _browser.NewPageAsync();
        AttachOrderApiCapture(_page);

        WritePidFile(sessionDir, _browser.Process?.Id);
        _initialized = true;
        _logger.LogInformation(
            "[AhkBroker] Browser session ready. Headless={Headless} Profile={Dir}",
            cfg.Headless, sessionDir);
        // Recorded because a browser window appearing is the most visible thing this system does, and
        // the panel is where the user should be able to see WHY it just appeared.
        _activity?.Info("Broker", "Browser session opened",
            cfg.Headless ? "Headless." : "A visible Chromium window belongs to the broker.");
    }

    private bool IsHealthy() =>
        _initialized && _browser is { IsConnected: true } && _page is { IsClosed: false };

    /// <summary>Launches Chromium; on failure (often a stale profile lock) cleans locks and retries once.</summary>
    private async Task<IBrowser> LaunchWithRetryAsync(LaunchOptions options, string sessionDir)
    {
        try
        {
            return await Puppeteer.LaunchAsync(options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AhkBroker] Browser launch failed — cleaning profile locks and retrying once.");
            KillPreviousBrowser(sessionDir);
            CleanProfileLocks(sessionDir);
            await Task.Delay(500);
            return await Puppeteer.LaunchAsync(options);
        }
    }

    // ── Browser lifecycle / process control ────────────────────────────────────

    private static string PidFilePath(string sessionDir) => Path.Combine(sessionDir, ".broker_chrome.pid");

    private static void WritePidFile(string sessionDir, int? pid)
    {
        if (pid is null) return;
        try { File.WriteAllText(PidFilePath(sessionDir), pid.Value.ToString()); } catch { /* best effort */ }
    }

    /// <summary>
    /// Tears down the browser this broker currently owns and guarantees the OS process is gone so it
    /// cannot keep holding the profile lock.
    /// </summary>
    private async Task TeardownAsync()
    {
        _initialized = false;
        _page = null;

        var browser = _browser;
        _browser = null;
        if (browser is null) return;

        _activity?.Info("Broker", "Browser session closed");

        var process = browser.Process;
        try { await browser.CloseAsync(); } catch { /* best effort */ }
        try { browser.Dispose(); }          catch { /* best effort */ }
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    /// <summary>
    /// Kills a Chrome instance this broker launched on a previous run (tracked by PID file) that is
    /// still alive and holding the profile. Deliberately targeted by recorded PID + process name so a
    /// user's own Chrome is never touched.
    /// </summary>
    private void KillPreviousBrowser(string sessionDir)
    {
        var pidFile = PidFilePath(sessionDir);
        try
        {
            if (!File.Exists(pidFile)) return;

            if (int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    if (p.ProcessName.Contains("chrome", StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(entireProcessTree: true);
                        _logger.LogWarning(
                            "[AhkBroker] Killed leftover browser PID {Pid} from a previous run before relaunch.", pid);
                    }
                }
                catch (ArgumentException) { /* PID no longer running */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AhkBroker] Could not check/kill the previous browser process.");
        }
        finally
        {
            try { File.Delete(pidFile); } catch { /* best effort */ }
        }
    }

    /// <summary>Removes stale single-instance lock files a crashed Chrome leaves behind in the profile.</summary>
    private static void CleanProfileLocks(string sessionDir)
    {
        foreach (var name in new[] { "SingletonLock", "SingletonCookie", "SingletonSocket", "lockfile" })
        {
            try
            {
                var path = Path.Combine(sessionDir, name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best effort — a live lock can't be deleted, but the kill above handles that */ }
        }
    }

    /// <summary>
    /// Returns the configured Chrome path, or downloads a matching Chromium via BrowserFetcher
    /// when none is set. Without this, Puppeteer.LaunchAsync fails with "chrome.exe ... not found".
    /// </summary>
    private async Task<string?> ResolveBrowserPathAsync(AhkConfig cfg, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(cfg.ExecutablePath))
        {
            if (File.Exists(cfg.ExecutablePath))
                return cfg.ExecutablePath;

            _logger.LogWarning(
                "[AhkBroker] Configured ExecutablePath '{Path}' not found — falling back to BrowserFetcher.",
                cfg.ExecutablePath);
        }

        // Download into a cache dir that is a SIBLING of plugins/, never inside it. PuppeteerSharp's
        // default fetcher path is the location of PuppeteerSharp.dll (under plugins/), which would
        // drop Chromium into plugins/Chrome where the host's plugin scanner then tries to load
        // chrome.dll as a managed assembly and throws BadImageFormatException.
        var cacheDir = Path.Combine(AppContext.BaseDirectory, "browser-cache");
        Directory.CreateDirectory(cacheDir);

        _logger.LogInformation("[AhkBroker] Ensuring a local Chromium is installed (first run may download)...");
        var fetcher   = new BrowserFetcher(new BrowserFetcherOptions { Path = cacheDir });
        var installed = await fetcher.DownloadAsync();
        var path      = installed.GetExecutablePath();
        _logger.LogInformation("[AhkBroker] Using Chromium at {Path}", path);
        return path;
    }

    // ── Order placement ───────────────────────────────────────────────────────

    public async Task<OrderResult> PlaceOrderAsync(TradingSignal signal) =>
        (await PlaceOrdersAsync(new[] { signal })).Single();

    /// <summary>
    /// Places a flat list of orders within a SINGLE browser session. With <paramref name="stopOnFailure"/>
    /// (default true) the whole list is one dependent sequence that halts at the first failure — so a
    /// follow-up take-profit SELL is never placed if its BUY failed. With it false each order is
    /// independent. Returns one <see cref="OrderResult"/> per order actually attempted.
    /// </summary>
    public async Task<IReadOnlyList<OrderResult>> PlaceOrdersAsync(
        IReadOnlyList<TradingSignal> signals, bool stopOnFailure = true)
    {
        // Model the flag as grouping: stop-on-failure ⇒ one dependent group; otherwise ⇒ each order
        // its own independent group. Then flatten the per-group results back to a flat list.
        var groups = stopOnFailure
            ? new[] { signals }
            : signals.Select(s => (IReadOnlyList<TradingSignal>)new[] { s }).ToArray();

        var grouped = await PlaceOrderGroupsAsync(groups);
        return grouped.SelectMany(g => g).ToList();
    }

    /// <summary>
    /// Places independent GROUPS of orders in one browser session: launched once, session prepared
    /// once, torn down once at the end (when CloseBrowserAfterOrder is set). WITHIN a group orders run
    /// in sequence and stop at the first failure (a buy→sell pair: the sell is skipped if the buy
    /// fails). ACROSS groups execution always continues (independent positions). Each submit runs
    /// EXACTLY ONCE and is never auto-retried. Returns the results for each group, aligned to the input.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyList<OrderResult>>> PlaceOrderGroupsAsync(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups)
    {
        var output = new List<IReadOnlyList<OrderResult>>(groups.Count);

        await _gate.WaitAsync();
        using var screen = EnterTradingScreen();
        try
        {
            // Readiness — launch + login — is safe to retry, so a dead/locked/stray browser is restarted
            // rather than failing the batch.
            await PrepareSessionWithRetryAsync();

            foreach (var group in groups)
            {
                var groupResults = new List<OrderResult>(group.Count);
                foreach (var signal in group)
                {
                    _activity?.Info("Orders",
                        $"Submitting {signal.Action} {signal.Quantity?.ToString() ?? "default qty"} "
                        + $"{signal.Symbol} ({signal.OrderType})",
                        signal.EntryPrice is { } entry ? $"At {entry:0.##}." : null);

                    var result = await DispatchOrderAsync(signal);
                    groupResults.Add(result);

                    _activity?.Record(
                        result.Success ? ActivityLevel.Info : ActivityLevel.Error,
                        "Orders",
                        result.Success
                            ? $"{signal.Action} {signal.Symbol} accepted by the broker"
                            : $"{signal.Action} {signal.Symbol} was NOT placed",
                        result.Message);

                    if (!result.Success) break; // dependent within a group
                }
                output.Add(groupResults);
            }

            return output;
        }
        finally
        {
            // Close the browser once the whole batch is done (on-demand lifecycle) — unless a caller
            // holds a session lease, in which case the teardown belongs to them. The next call
            // relaunches and the persisted profile usually keeps us logged in. Disable via
            // CloseBrowserAfterOrder.
            await CloseAfterOperationAsync();

            _gate.Release();
        }
    }

    // ── Order API capture ─────────────────────────────────────────────────────

    /// <summary>
    /// The portal endpoints whose raw traffic is worth recording. Deliberately just the two that place
    /// and pull orders: the trading screen also polls <c>GetFeed</c> every second or two, and recording
    /// that would bury the two events a year of debugging actually needs in a megabyte an hour.
    /// </summary>
    private static readonly string[] _capturedEndpoints = ["/Home/PlaceOrder", "/Home/CancelOrder"];

    /// <summary>0 until the order form's option lists have been recorded for this browser session.</summary>
    private int _loggedOrderFormOptions;

    /// <summary>
    /// Records what the portal's own <c>PlaceOrder</c> / <c>CancelOrder</c> calls send and what they
    /// answer, for every order the browser path places. See
    /// <see cref="AhkConfig.CaptureOrderApiTraffic"/> for why this exists — in short, the response body
    /// is the one piece of the direct-API migration that cannot be learned by reading anything, because
    /// the portal's own UI discards it, and this is how it gets learned without placing a test order.
    ///
    /// <para>
    /// Attached per page, so it re-attaches on every relaunch of the on-demand lifecycle. Both handlers
    /// swallow everything: a diagnostic able to fault the page's event loop would be able to break an
    /// order it was only supposed to watch.
    /// </para>
    /// </summary>
    private void AttachOrderApiCapture(IPage page)
    {
        if (!_config.Current.CaptureOrderApiTraffic) return;

        _loggedOrderFormOptions = 0;

        page.Request += (_, e) =>
        {
            try
            {
                if (!IsCapturedEndpoint(e.Request.Url)) return;
                WriteOrderApiCapture(
                    $"REQUEST  {e.Request.Method} {e.Request.Url}\n"
                  + $"  payload: {RedactPin(e.Request.PostData)}");
            }
            catch { /* a capture must never disturb the page */ }
        };

        page.Response += async (_, e) =>
        {
            try
            {
                if (!IsCapturedEndpoint(e.Response.Url)) return;

                // Read verbatim, and record the LENGTH separately. The known failure mode here is an
                // empty 200 — an off-hours submission places nothing and says nothing — and "" is
                // indistinguishable from "we could not read it" in a log line without the length.
                string body;
                try { body = await e.Response.TextAsync() ?? ""; }
                catch (Exception ex) { body = $"<could not be read: {ex.Message}>"; }

                var contentType = e.Response.Headers.TryGetValue("content-type", out var ct) ? ct : "(none)";

                WriteOrderApiCapture(
                    $"RESPONSE {(int)e.Response.Status} {e.Response.Url}\n"
                  + $"  request payload: {RedactPin(e.Response.Request?.PostData)}\n"
                  + $"  content-type: {contentType}\n"
                  + $"  body length: {body.Length}\n"
                  + $"  body: {Truncate(body, 4_000)}");
            }
            catch { /* see above */ }
        };
    }

    private static bool IsCapturedEndpoint(string? url) =>
        url is not null && _capturedEndpoints.Any(e => url.Contains(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Removes the trading PIN from a captured form payload. Everything else in it is the order the user
    /// asked for and is already in the logs; the PIN is the one field that must never be.
    /// </summary>
    private static string RedactPin(string? postData)
    {
        if (string.IsNullOrEmpty(postData)) return "(none)";

        // Form-urlencoded, so the PIN is one &-delimited pair. Matched case-insensitively because the
        // field is "PIN" on this endpoint and could be "pin" on the next, and a redaction that depends
        // on the portal's capitalisation is a redaction that silently stops working.
        return Regex.Replace(postData, @"(?i)\b(pin)=[^&]*", "$1=***");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + $"… (+{value.Length - max} more)";

    /// <summary>
    /// Appends one capture entry to <c>{LogDir}/order_api_capture.log</c> and mirrors it to the log. The
    /// file exists because this evidence gets read weeks later, by someone diffing what the browser sent
    /// against what a direct call sends — which is not a thing to go hunting for in a rolled-over
    /// application log.
    /// </summary>
    private void WriteOrderApiCapture(string entry)
    {
        _logger.LogInformation("[AhkBroker] Order API capture:\n{Entry}", entry);

        try
        {
            var dir = ResolvePath(_config.Current.LogDir);
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "order_api_capture.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {entry}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AhkBroker] Could not append to the order API capture file.");
        }
    }

    /// <summary>
    /// Records the option lists of the order dialog's selects, once per browser session.
    ///
    /// <para>
    /// This closes the one gap in the captured <c>PlaceOrder</c> payload that stays invisible until an
    /// order is actually placed: <c>site.js</c> sends the selected order type's <c>value</c> on the BUY
    /// side but its <c>text</c> on the SELL side, and the two are not necessarily the same string. A
    /// direct caller that sends the text where the server wants the value gets what this portal always
    /// gives — HTTP 200 and no order. Both are recorded so the mapping is written from evidence.
    /// </para>
    /// </summary>
    private async Task LogOrderFormOptionsAsync(string side)
    {
        if (!_config.Current.CaptureOrderApiTraffic) return;
        if (Interlocked.Exchange(ref _loggedOrderFormOptions, 1) != 0) return;

        try
        {
            var json = await _page!.EvaluateFunctionAsync<string>(
                @"() => {
                    const ids = ['buyordertype','sellordertype','buytradetype','selltradetype',
                                 'buymarket','sellmarket','buyaccount','sellaccount'];
                    const out = {};
                    for (const id of ids) {
                        const el = document.getElementById(id);
                        if (!el) { out[id] = null; continue; }
                        const opts = el.options ? [...el.options] : [...el.querySelectorAll('option')];
                        out[id] = opts.map(o => ({ value: o.value, text: (o.text || '').trim(),
                                                   selected: !!o.selected }));
                    }
                    return JSON.stringify(out);
                }");

            WriteOrderApiCapture($"ORDER FORM OPTIONS (read from the {side.ToUpperInvariant()} dialog)\n  {json}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AhkBroker] Could not read the order form's select options.");
        }
    }

    // ── Session lease ─────────────────────────────────────────────────────────

    /// <summary>
    /// Holds one browser session open across SEVERAL broker calls.
    ///
    /// <para>
    /// <b>Why.</b> With <see cref="AhkConfig.CloseBrowserAfterOrder"/> (the default) every public
    /// operation tears the browser down when it returns, so a caller that needs two readings — the
    /// protective-stop pass reads holdings and then the outstanding book; a backstop checks the book
    /// and then submits — paid for two Chromium launches, two logins and two window flashes to do
    /// one job. That is what "the browser opens twice per order" was.
    /// </para>
    ///
    /// <para>
    /// This is NOT a lock and grants no exclusivity: each operation still takes the gate on its own,
    /// so an order arriving mid-lease is serialised exactly as before. All the lease does is defer
    /// the teardown until the last holder releases it.
    /// </para>
    /// </summary>
    public BrokerSessionLease LeaseSession() => new(this);

    /// <summary>See <see cref="LeaseSession"/>. Disposing the last lease closes the browser.</summary>
    public sealed class BrokerSessionLease : IAsyncDisposable
    {
        private readonly AhkBroker _broker;
        private int _disposed;

        internal BrokerSessionLease(AhkBroker broker)
        {
            _broker = broker;
            Interlocked.Increment(ref broker._sessionLeases);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Interlocked.Decrement(ref _broker._sessionLeases) == 0)
                await _broker.CloseIfIdleAsync();
        }
    }

    /// <summary>
    /// Applies the on-demand lifecycle at the end of an operation. ASSUMES the gate is held. A live
    /// lease defers the teardown to whoever releases it last, so the session survives the gap
    /// between two calls that belong to the same piece of work.
    /// </summary>
    private async Task CloseAfterOperationAsync()
    {
        if (!_config.Current.CloseBrowserAfterOrder) return;
        if (Volatile.Read(ref _sessionLeases) > 0) return;
        await TeardownAsync();
    }

    /// <summary>Closes the browser once the last lease is released, taking the gate to do it.</summary>
    private async Task CloseIfIdleAsync()
    {
        if (!_config.Current.CloseBrowserAfterOrder) return;

        await _gate.WaitAsync();
        try
        {
            // Re-checked under the gate: a new lease may have been taken while we waited, and closing
            // the browser out from under it would defeat the whole point.
            if (Volatile.Read(ref _sessionLeases) == 0)
                await TeardownAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AhkBroker] Could not close the browser after the last session lease.");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Session sharing (direct JSON API) ─────────────────────────────────────

    /// <summary>
    /// Number of in-flight operations actually driving the portal's trading screen. Incremented only
    /// by the operations that navigate the UI, never merely by having a browser open.
    /// </summary>
    private int _tradingScreenHolders;

    /// <summary>
    /// True while an operation is actively driving the portal's trading screen, where the portal's
    /// own <c>site.js</c> is polling <c>/Home/GetFeed</c> on a 1–2s timer.
    ///
    /// <para>
    /// This exists for one reason: <c>GetFeed</c> is plausibly a drain-once queue (see
    /// <c>docs/ahk-feed-api.md</c> — it could not be confirmed either way with the market closed).
    /// If it is, then two pollers on the SAME session split the stream between them and each sees
    /// roughly half the ticks, with no error anywhere to say so. The direct feed poller reads this
    /// and yields while the browser holds the screen, so the two never compete.
    /// </para>
    ///
    /// <para>
    /// It is a COUNTER of active operations, not "is a browser alive". That distinction was learned
    /// by running it: defined as <c>_initialized &amp;&amp; _page is not null</c>, the flag latched
    /// true the moment any browser existed — including one left alive by a failed login or by
    /// <c>CloseBrowserAfterOrder = false</c> — and the feed worker then yielded to a browser that was
    /// doing nothing, forever, polling not once more for the rest of the session. The only symptom
    /// was silence at Debug level.
    /// </para>
    /// </summary>
    public bool BrowserHoldsTradingScreen => Volatile.Read(ref _tradingScreenHolders) > 0;

    /// <summary>
    /// Marks the trading screen as in use for the lifetime of the returned scope. Callers that
    /// navigate the portal UI wrap their work in this; <see cref="GetSessionCookiesAsync"/>
    /// deliberately does not, because harvesting cookies never leaves a page on the trading screen.
    /// </summary>
    private TradingScreenScope EnterTradingScreen() => new(this);

    private readonly struct TradingScreenScope : IDisposable
    {
        private readonly AhkBroker _broker;

        public TradingScreenScope(AhkBroker broker)
        {
            _broker = broker;
            Interlocked.Increment(ref broker._tradingScreenHolders);
        }

        public void Dispose() => Interlocked.Decrement(ref _broker._tradingScreenHolders);
    }

    /// <summary>
    /// Hands out the authenticated session cookies from the live browser session, for callers that
    /// talk to the portal's JSON API directly instead of driving the DOM.
    ///
    /// <para>
    /// Reusing the browser's login rather than reimplementing it over HTTP is deliberate. The portal
    /// authenticates with twelve positional single-character password boxes of which only a random
    /// subset is enabled per attempt (see <see cref="LoginAsync"/>); that logic is already written,
    /// already handles the portal's slow async render, and already persists its profile. A second
    /// implementation of it would be a second thing to keep correct against the same fragile page.
    /// </para>
    ///
    /// <para>
    /// Returns an empty list rather than throwing when no session can be established, because every
    /// caller of this is a fail-soft data path that must degrade to its fallback source, not break.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<(string Name, string Value, string Domain)>> GetSessionCookiesAsync(
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await PrepareSessionWithRetryAsync();

            var cookies = await _page!.GetCookiesAsync();
            var harvested = cookies
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => (c.Name, c.Value ?? "", c.Domain ?? ""))
                .ToList();

            _logger.LogInformation(
                "[AhkBroker] Handed {Count} session cookie(s) to the direct portal API client.",
                harvested.Count);

            return harvested;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[AhkBroker] Could not harvest session cookies for the direct portal API.");
            return [];
        }
        finally
        {
            // Deliberately NOT tearing the browser down on CloseBrowserAfterOrder here. The cookies
            // just handed out are only valid while the portal considers the session alive, and the
            // caller is about to start using them; closing the browser is the caller's cue that the
            // session is theirs to keep warm via /Home/Relogin.
            //
            // The page it is left ON, however, matters — see ParkPageAsync.
            await ParkPageAsync();

            _gate.Release();
        }
    }

    /// <summary>
    /// Navigates the browser off the portal's trading screen, to <c>about:blank</c>, while leaving the
    /// browser and its authenticated profile alone. ASSUMES the gate is held.
    ///
    /// <para>
    /// <b>Why an idle page is not harmless.</b> The trading screen's own <c>site.js</c> polls
    /// <c>/Home/GetFeed</c> on a 1–2s timer for as long as it is loaded, and re-subscribes
    /// <c>Page1</c> from its own (almost always empty) market-watch table on every load. Both of those
    /// fight <see cref="Feed.AhkFeedWorker"/> for the same server-side session, and
    /// <see cref="BrowserHoldsTradingScreen"/> cannot see it happening: that flag counts our own
    /// in-flight operations, by design, so a window merely sitting on the screen registers as idle
    /// while its JavaScript keeps draining the feed. The observed symptom is a feed that re-subscribes
    /// every thirty silent polls, forever, with the market open and nothing wrong upstream.
    /// </para>
    ///
    /// <para>
    /// This is the fix for the specific case the harvest creates: <see cref="GetSessionCookiesAsync"/>
    /// deliberately leaves the browser alive, so with the feed enabled the login lands on the trading
    /// screen and stays there for the life of the process. Parking the page keeps the warm session —
    /// no relaunch, no second login — and takes the competing poller out of it. The next operation
    /// finds no login form on <c>about:blank</c>, navigates back to the portal, and skips credentials
    /// because the profile is still authenticated.
    /// </para>
    /// </summary>
    private async Task ParkPageAsync()
    {
        if (!_config.Current.ParkPageAfterCookieHarvest) return;
        if (_page is null || _page.IsClosed) return;

        try
        {
            await _page.GoToAsync("about:blank");
            _logger.LogInformation(
                "[AhkBroker] Parked the browser on about:blank so the portal's own feed poller stops "
                + "competing with the direct quote feed.");
        }
        catch (Exception ex)
        {
            // Failing to park costs feed contention, not correctness — never an order.
            _logger.LogDebug(ex, "[AhkBroker] Could not park the browser page.");
        }
    }

    // ── Live price lookup ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads the live last-trade price for each symbol in ONE browser session, by opening the BUY
    /// dialog and letting the portal auto-fill <c>#buyprice</c> when the symbol resolves. Nothing is
    /// submitted — this only inspects the form. Used to size/price a BUY tip that gave no entry price
    /// ("accumulate on dips"). A symbol whose price could not be read maps to null.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, decimal?>> GetMarketPricesAsync(
        IReadOnlyList<string> symbols)
    {
        var prices = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        if (symbols.Count == 0) return prices;

        await _gate.WaitAsync();
        using var screen = EnterTradingScreen();
        try
        {
            _activity?.Info("Broker", $"Reading live prices for {symbols.Count} symbol(s)");
            await PrepareSessionWithRetryAsync();
            await OpenOrderDialogAsync("buy");

            foreach (var symbol in symbols
                         .Select(s => s?.Trim().ToUpperInvariant() ?? "")
                         .Where(s => s.Length > 0)
                         .Distinct())
            {
                prices[symbol] = await ReadLastTradePriceAsync(symbol);
            }

            return prices;
        }
        finally
        {
            await CloseAfterOperationAsync();
            _gate.Release();
        }
    }

    /// <summary>
    /// Types <paramref name="symbol"/> into the (already open) BUY dialog, waits for the portal to
    /// resolve it and auto-fill <c>#buyprice</c> with the last-trade price, and parses that value.
    /// ASSUMES the gate is held and the dialog is open. Returns null if no positive price appears.
    /// </summary>
    private async Task<decimal?> ReadLastTradePriceAsync(string symbol)
    {
        await FillFieldAsync("#buysymbol", symbol);

        // Bounded wait for the portal to resolve the symbol and populate #buyprice — a fixed sleep
        // reads an empty field on a slow machine and reports the symbol as unpriceable.
        var price = await ResolveSymbolAsync("buy");
        if (price is > 0m)
        {
            _logger.LogInformation("[AhkBroker] Live price for {Symbol}: {Price}.", symbol, price);
            return price;
        }

        _logger.LogWarning("[AhkBroker] Could not read a live price for {Symbol}.", symbol);
        return null;
    }

    // ── Portfolio / balance ───────────────────────────────────────────────────

    // Header-synonym map used to recognize the holdings grid and its columns without hard-coding
    // the portal's exact wording. Order matters: more specific kinds (market value) are matched
    // before generic ones (price) so "Market Value" is never consumed as a price column.
    private static readonly (string Kind, string[] Synonyms)[] _holdingsColumns =
    [
        ("symbol",       ["symbol", "scrip", "stock", "company", "code"]),
        ("quantity",     ["qty", "quantity", "volume", "shares", "holding", "position"]),
        ("investment",   ["investment", "cost value", "buy value", "buy amount", "total cost", "cost amount"]),
        ("currentValue", ["market value", "current value", "mkt value", "value"]),
        ("avgPrice",     ["avg", "average", "cost price", "buy rate", "purchase", "cost"]),
        ("currentPrice", ["market rate", "current rate", "market price", "current price", "last price", "last rate", "close", "rate", "price"]),
        ("profitLoss",   ["p/l", "p&l", "profit", "gain", "unrealized"]),
    ];

    private static readonly string[] _balanceKeywords =
    [
        "available amount", "available cash", "available limit", "available balance",
        "avail amount", "buying power", "cash balance", "available"
    ];

    /// <summary>
    /// Reads the account's available cash and current holdings (symbol, shares, cost, market value)
    /// from the portal in one browser session. Read-only — nothing is clicked except an optional
    /// configured portfolio nav element. Column mapping is heuristic (see <see cref="_holdingsColumns"/>);
    /// when the holdings grid or the balance cannot be found, the page HTML + a screenshot are dumped
    /// to LogDir so the real selectors can be configured (Ahk.HoldingsTableSelector etc.), and the
    /// snapshot carries a warning instead of invented numbers.
    /// </summary>
    public async Task<PortfolioSnapshot> GetPortfolioAsync()
    {
        await _gate.WaitAsync();
        using var screen = EnterTradingScreen();
        try
        {
            _activity?.Info("Broker", "Reading the portfolio (holdings and available cash)");
            await PrepareSessionWithRetryAsync();
            await NavigateToPortfolioViewAsync();
            var snapshot = await ExtractPortfolioAsync();
            _activity?.Record(
                snapshot.Warnings.Count == 0 ? ActivityLevel.Info : ActivityLevel.Warn,
                "Broker",
                $"Portfolio read: {snapshot.Holdings.Count} holding(s)",
                snapshot.Warnings.Count == 0 ? null : string.Join(" ", snapshot.Warnings));
            return snapshot;
        }
        finally
        {
            await CloseAfterOperationAsync();
            _gate.Release();
        }
    }


    /// <summary>
    /// Brings the portfolio data on screen. AHK flow (defaults): click the #exposure menu item to
    /// open the Exposure dialog, select the account in #expaccount (its change event triggers the
    /// data load), flip to Open Position and back to Collaterals (the grid only renders after that),
    /// then wait — bounded — for grid rows to appear. A configured PortfolioUrl replaces the menu
    /// click with a navigation; the account/tab steps still run when their selectors exist.
    /// ASSUMES the gate is held.
    /// </summary>
    private async Task NavigateToPortfolioViewAsync()
    {
        var cfg = _config.Current;
        var timeout = Math.Max(1_000, cfg.PortfolioLoadTimeoutMs);

        if (!string.IsNullOrWhiteSpace(cfg.PortfolioUrl))
        {
            var url = Uri.TryCreate(cfg.PortfolioUrl, UriKind.Absolute, out var abs)
                ? abs.ToString()
                : new Uri(new Uri(cfg.PortalUrl), cfg.PortfolioUrl).ToString();
            await _page!.GoToAsync(url, new NavigationOptions
            {
                Timeout = timeout,
                WaitUntil = [WaitUntilNavigation.Networkidle2]
            });
        }
        else if (!string.IsNullOrWhiteSpace(cfg.PortfolioNavSelector))
        {
            // The "Exposure" item lives in a slide-out sidebar. Open it first if a toggle is
            // configured, so the menu item is rendered/visible before we click it.
            if (!string.IsNullOrWhiteSpace(cfg.PortfolioMenuToggleSelector))
            {
                if (await ClickViaDomAsync(cfg.PortfolioMenuToggleSelector))
                {
                    // Wait for the menu the toggle reveals rather than guessing the animation length:
                    // the nav item appearing IS the thing the fixed 600ms was hoping for.
                    if (!await WaitForExistsAsync(cfg.PortfolioNavSelector, timeout))
                        await WaitForDomSettledAsync(300, Math.Min(timeout, 2_000));
                }
                else
                {
                    _logger.LogWarning(
                        "[AhkBroker] Menu toggle '{Selector}' not found — trying the nav item directly.",
                        cfg.PortfolioMenuToggleSelector);
                }
            }

            // The nav item may be lazily rendered; give it the configured budget rather than a fixed 5s.
            // A miss is not fatal here — the open attempt below reports if it is truly absent.
            await WaitForExistsAsync(cfg.PortfolioNavSelector, timeout);

            if (!await OpenPortfolioDialogAsync(cfg, timeout))
            {
                await DumpPortfolioPageAsync("no_dialog");
            }
        }
        else
        {
            // No explicit target: let the current screen finish rendering.
            await WaitForDomSettledAsync(quietMs: 500, timeoutMs: Math.Min(timeout, 5_000));
        }

        await SelectPortfolioAccountAsync(cfg);
        await RunPortfolioTabSequenceAsync(cfg);
        await WaitForHoldingsRowsAsync(cfg, timeout);
    }

    /// <summary>
    /// Opens the AHK Exposure dialog and returns true once its scaffold exists. The "Exposure" menu
    /// item (#exposure) has an addEventListener('click') that calls the site's OpenExposureModalPopUp():
    /// it shows the modal AND builds the #exposuredynamic scaffold (tabs + #collateralstable) the data
    /// AJAX later writes into. Confirmed against the live portal: only a dispatched, bubbling
    /// MouseEvent fires that handler — a bare element.click() does not, and Puppeteer's ClickAsync
    /// can't reach the element because the sidebar is parked off-screen (left:-200). The success
    /// signal is the holdings table EXISTING (not visible: DataTables keeps the scroll header
    /// zero-height). Retries until PortfolioLoadTimeoutMs is exhausted to absorb the handler binding
    /// slightly after page load — a fixed attempt count silently became a much shorter wall-clock
    /// budget on a slow machine, which is exactly when the handler binds latest.
    /// </summary>
    private async Task<bool> OpenPortfolioDialogAsync(AhkConfig cfg, int timeout)
    {
        var scaffoldSelector = !string.IsNullOrWhiteSpace(cfg.HoldingsTableSelector)
            ? cfg.HoldingsTableSelector
            : cfg.PortfolioAccountSelectSelector;

        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        for (var attempt = 1; ; attempt++)
        {
            var fired = await ClickViaDomAsync(cfg.PortfolioNavSelector);
            if (!fired)
            {
                _logger.LogWarning(
                    "[AhkBroker] PortfolioNavSelector '{Selector}' not found on the page (attempt {Attempt}).",
                    cfg.PortfolioNavSelector, attempt);
            }
            else
            {
                var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (await WaitForExistsAsync(scaffoldSelector, Math.Clamp(remaining, 500, 3_000)))
                {
                    if (attempt > 1)
                        _logger.LogInformation(
                            "[AhkBroker] Exposure dialog opened on attempt {Attempt} — the portal bound its handler late.",
                            attempt);
                    return true;
                }
            }

            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(400);
        }

        _logger.LogWarning(
            "[AhkBroker] Exposure dialog scaffold '{Selector}' never appeared within {Timeout}ms after triggering '{Nav}'. " +
            "Raise Ahk.PortfolioLoadTimeoutMs if this machine is slow.",
            scaffoldSelector, timeout, cfg.PortfolioNavSelector);
        return false;
    }

    /// <summary>
    /// Waits until the page has stopped mutating for <paramref name="quietMs"/>, bounded by
    /// <paramref name="timeoutMs"/>. Returns true if it settled, false on timeout (the caller
    /// continues either way — this replaces a guess, it is not a gate).
    ///
    /// The portfolio flow is a chain of AJAX loads and tab renders with no single selector that means
    /// "done", which is why it used fixed sleeps. A MutationObserver gives the real signal: the sleep
    /// was always either too long (wasting seconds every read) or too short (scraping a half-rendered
    /// grid and reporting an empty portfolio). The observer is installed once per document and
    /// re-installed automatically after a navigation clears it.
    /// </summary>
    private async Task<bool> WaitForDomSettledAsync(int quietMs, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(quietMs, timeoutMs));
        while (true)
        {
            long idleMs;
            try
            {
                idleMs = await _page!.EvaluateFunctionAsync<long>(
                    """
                    () => {
                        if (!window.__afSettle) {
                            window.__afSettle = { last: Date.now() };
                            try {
                                new MutationObserver(() => { window.__afSettle.last = Date.now(); })
                                    .observe(document.documentElement, {
                                        subtree: true, childList: true, characterData: true, attributes: true
                                    });
                            } catch (e) { /* no document yet — treated as still busy below */ }
                        }
                        return Date.now() - window.__afSettle.last;
                    }
                    """);
            }
            catch
            {
                return false; // page mid-navigation; the caller's own waits take over
            }

            if (idleMs >= quietMs) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(100);
        }
    }

    /// <summary>Waits until the selector matches an element in the DOM (visible or not). Empty/timeout → false.</summary>
    private async Task<bool> WaitForExistsAsync(string selector, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(selector)) return false;
        try
        {
            await _page!.WaitForSelectorAsync(selector,
                new WaitForSelectorOptions { Timeout = Math.Max(500, timeoutMs) });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Fires the site's click handler on the first match by dispatching a full bubbling MouseEvent
    /// (plus a jQuery-triggered click for delegated handlers). This runs the handler regardless of
    /// the element's visibility/viewport position — needed for the off-screen sidebar menu item and
    /// Bootstrap tab triggers that Puppeteer's ClickAsync can't reach. A bare element.click() is
    /// deliberately NOT used: on the AHK portal it does not invoke the Exposure open handler.
    /// Returns false only when the selector matches nothing.
    /// </summary>
    private async Task<bool> ClickViaDomAsync(string selector) =>
        await _page!.EvaluateFunctionAsync<bool>(
            """
            (sel) => {
                const el = document.querySelector(sel);
                if (!el) return false;
                try {
                    el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                } catch (e) {}
                for (const jq of [window.jQuery, window.$].filter(Boolean)) {
                    try { jq(el).trigger('click'); } catch (e) {}
                }
                return true;
            }
            """, selector);

    /// <summary>
    /// Picks the first real account in the dialog's account dropdown — option value "0" is the
    /// "Select Account" placeholder. SelectAsync fires the change event the portal listens on to
    /// load the exposure panels and collaterals. No-op when the dropdown is absent/unconfigured.
    /// </summary>
    private async Task SelectPortfolioAccountAsync(AhkConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.PortfolioAccountSelectSelector)) return;

        var timeout = Math.Max(1_000, cfg.PortfolioLoadTimeoutMs);

        // The account list itself arrives by AJAX: right after the dialog opens the dropdown exists but
        // holds only the "Select Account" placeholder (value "0"). Reading it once at that moment used
        // to abort the whole portfolio read — no account selected means no data loads, and the caller
        // reports a perfectly healthy account as an empty portfolio. Poll until a real option appears.
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        string[] values;
        string? account;
        while (true)
        {
            values = await _page!.EvaluateFunctionAsync<string[]>(
                """
                (sel) => {
                    const e = document.querySelector(sel);
                    return e && e.options ? Array.from(e.options).map(o => o.value) : [];
                }
                """, cfg.PortfolioAccountSelectSelector) ?? [];

            account = values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && v != "0");
            if (account is not null || DateTime.UtcNow >= deadline) break;
            await Task.Delay(250);
        }

        if (account is null)
        {
            _logger.LogWarning(
                "[AhkBroker] Account dropdown '{Selector}' still had no selectable account after {Timeout}ms (options: {Options}).",
                cfg.PortfolioAccountSelectSelector, timeout,
                values.Length == 0 ? "<none>" : string.Join(",", values));
            return;
        }

        await _page.SelectAsync(cfg.PortfolioAccountSelectSelector, account);

        // SelectAsync dispatches a native change event; also nudge the jQuery .change() handler the
        // portal binds, in case it listens only on its own jQuery instance. Either one triggers the
        // AJAX that fills the exposure panels and collaterals grid.
        await _page.EvaluateFunctionAsync(
            """
            (sel) => {
                for (const jq of [window.jQuery, window.$].filter(Boolean)) {
                    try { jq(sel).trigger('change'); } catch (e) {}
                }
            }
            """, cfg.PortfolioAccountSelectSelector);

        // Exposure panels + collaterals load via AJAX after the change event. Wait for the page to stop
        // mutating instead of sleeping 1.5s: on a fast machine that sleep was wasted, and on a slow one
        // the tab sequence below ran against a grid the portal was still building.
        await WaitForDomSettledAsync(quietMs: 600, timeoutMs: timeout);
    }

    /// <summary>
    /// Replays the tab flips the portal needs before it renders the collaterals grid (observed on
    /// the live portal: Open Position, then back to Collaterals). Missing elements are skipped with
    /// a log line so a portal change degrades gracefully instead of failing the read.
    /// </summary>
    private async Task RunPortfolioTabSequenceAsync(AhkConfig cfg)
    {
        var timeout = Math.Max(1_000, cfg.PortfolioLoadTimeoutMs);

        foreach (var selector in cfg.PortfolioTabSequence.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            if (!await ClickViaDomAsync(selector))
            {
                _logger.LogWarning("[AhkBroker] Portfolio tab '{Selector}' not found — skipping.", selector);
                continue;
            }

            // Each flip re-renders a pane (and can refetch its data). Wait for that to finish rather
            // than a fixed 700ms — flipping away before the previous pane rendered is what makes the
            // final Collaterals grid come back empty.
            await WaitForDomSettledAsync(quietMs: 400, timeoutMs: Math.Min(timeout, 5_000));
        }
    }

    /// <summary>
    /// Bounded poll for data rows in the holdings table body. A grid that has explicitly rendered its
    /// "no data available" placeholder returns immediately — that is the portal SAYING the portfolio is
    /// empty, which is different from not having answered yet, and waiting out the timeout for it made
    /// every read on an empty account cost the full PortfolioLoadTimeoutMs.
    /// </summary>
    private async Task WaitForHoldingsRowsAsync(AhkConfig cfg, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(cfg.HoldingsTableSelector)) return;

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            // -1 = the grid rendered an explicit empty-state row; 0 = nothing yet; >0 = data rows.
            var rows = await _page!.EvaluateFunctionAsync<int>(
                """
                (sel) => {
                    const t = document.querySelector(sel);
                    if (!t || !t.tBodies || !t.tBodies.length) return 0;
                    const body = t.tBodies[0];
                    const count = body.rows.length;
                    if (count === 0) return 0;
                    const text = (body.innerText || body.textContent || '').trim().toLowerCase();
                    // DataTables' empty state is a single full-width row ("No data available in table",
                    // "No matching records found", "no record found" on some skins).
                    if (count === 1 && /no (data|matching|record)/.test(text)) return -1;
                    return count;
                }
                """, cfg.HoldingsTableSelector);

            if (rows > 0) return;
            if (rows < 0)
            {
                _logger.LogInformation(
                    "[AhkBroker] Holdings grid '{Selector}' reported an empty portfolio.", cfg.HoldingsTableSelector);
                return;
            }
            await Task.Delay(400);
        }

        _logger.LogWarning(
            "[AhkBroker] No rows appeared in '{Selector}' within {Timeout}ms — portfolio may be empty.",
            cfg.HoldingsTableSelector, timeoutMs);
    }

    /// <summary>Scrapes balance + holdings from the current page. ASSUMES the gate is held.</summary>
    private async Task<PortfolioSnapshot> ExtractPortfolioAsync()
    {
        var cfg = _config.Current;
        var warnings = new List<string>();

        // Every table on the page as rows of cell text (first row = header). Plain string arrays keep
        // the Puppeteer→.NET deserialization trivial and version-proof.
        // Cell text prefers innerText but falls back to textContent: DataTables clones the header
        // into a zero-height "sizing" row inside the scroll body's table, and innerText of those
        // hidden cells is empty — textContent still carries the column names.
        var tables = await _page!.EvaluateFunctionAsync<string[][][]>(
            """
            (tableSelector) => {
                const norm = t => (t || '').trim().replace(/\s+/g, ' ');
                const cellText = c => norm(c.innerText) || norm(c.textContent);
                const grab = t => Array.from(t.rows)
                    .filter(r => r.cells.length >= 2)
                    .map(r => Array.from(r.cells).map(cellText));
                if (tableSelector) {
                    const el = document.querySelector(tableSelector);
                    return el ? [grab(el)] : [];
                }
                return Array.from(document.querySelectorAll('table')).map(grab);
            }
            """,
            string.IsNullOrWhiteSpace(cfg.HoldingsTableSelector) ? null : cfg.HoldingsTableSelector) ?? [];

        var holdings = new List<HoldingPosition>();
        var best = PickHoldingsTable(tables, cfg.HoldingsColumnMap);
        if (best is null)
        {
            warnings.Add(string.IsNullOrWhiteSpace(cfg.HoldingsTableSelector)
                ? "No table on the page looked like a holdings grid (need at least symbol + quantity columns). " +
                  "Inspect the dumped portfolio_*.html in LogDir and set Ahk.HoldingsTableSelector / Ahk.PortfolioNavSelector."
                : $"Ahk.HoldingsTableSelector '{cfg.HoldingsTableSelector}' matched no usable table on the page.");
            await DumpPortfolioPageAsync("no_holdings_table");
        }
        else
        {
            holdings.AddRange(ParseHoldings(best.Value.Table, best.Value.ColumnMap));
            if (holdings.Count == 0)
                warnings.Add("A holdings grid was found but contained no parseable position rows (empty portfolio?).");
        }

        var (balance, balanceSource) = await ReadAvailableBalanceAsync(cfg);
        if (balance is null)
        {
            warnings.Add("Available balance could not be read. Inspect the dumped portfolio_*.html in LogDir " +
                         "and set Ahk.AvailableBalanceSelector.");
            await DumpPortfolioPageAsync("no_balance");
        }

        var totalInvestment  = SumIfAny(holdings, h => h.InvestmentValue);
        var totalValue       = SumIfAny(holdings, h => h.CurrentValue);

        _logger.LogInformation(
            "[AhkBroker] Portfolio read: balance={Balance} holdings={Count} warnings={Warnings}",
            balance, holdings.Count, warnings.Count);

        return new PortfolioSnapshot
        {
            AvailableBalancePkr = balance,
            BalanceSource       = balanceSource,
            HoldingsAvailable   = best is not null,
            Holdings            = holdings,
            TotalInvestment     = totalInvestment,
            TotalCurrentValue   = totalValue,
            RetrievedAtUtc      = DateTime.UtcNow,
            Warnings            = warnings
        };
    }

    /// <summary>
    /// Scores every scraped table's header row against <see cref="_holdingsColumns"/> and returns the
    /// best one with its column→kind map. A table qualifies only when both a symbol and a quantity
    /// column are recognized — that pair is what distinguishes a holdings grid from the market-watch
    /// and order-book tables that share the same screen.
    /// </summary>
    private static (string[][] Table, Dictionary<int, string> ColumnMap)? PickHoldingsTable(
        string[][][] tables, IReadOnlyDictionary<string, string> explicitMap)
    {
        (string[][] Table, Dictionary<int, string> Map)? best = null;
        var bestScore = 0;

        foreach (var table in tables)
        {
            if (table.Length < 1) continue;
            var headers = table[0];
            var map = MapColumns(headers, explicitMap);
            if (!map.ContainsValue("symbol") || !map.ContainsValue("quantity")) continue;

            // Prefer the table that resolves the most distinct financial columns, then the taller one.
            var score = map.Count * 1_000 + table.Length;
            if (score > bestScore)
            {
                bestScore = score;
                best = (table, map);
            }
        }

        return best is null ? null : (best.Value.Table, best.Value.Map);
    }

    /// <summary>
    /// Maps header-cell index → column kind. The configured exact-name map is applied first — it is
    /// authoritative for the known AHK grid, where synonym matching would misfire (e.g. the generic
    /// "rate" synonym would bind currentPrice to "Ave_Rate_Buy" instead of "MTM_Price"). Header
    /// synonyms then fill only the kinds the explicit map left unresolved. Each kind and each
    /// column is assigned at most once.
    /// </summary>
    private static Dictionary<int, string> MapColumns(
        string[] headers, IReadOnlyDictionary<string, string> explicitMap)
    {
        var map = new Dictionary<int, string>();
        var taken = new HashSet<string>();

        foreach (var (kind, headerName) in explicitMap)
        {
            if (string.IsNullOrWhiteSpace(headerName)) continue;
            for (var i = 0; i < headers.Length; i++)
            {
                if (map.ContainsKey(i)) continue;
                if (headers[i].Equals(headerName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    map[i] = kind;
                    taken.Add(kind);
                    break;
                }
            }
        }

        foreach (var (kind, synonyms) in _holdingsColumns)
        {
            if (taken.Contains(kind)) continue;
            for (var i = 0; i < headers.Length; i++)
            {
                if (map.ContainsKey(i)) continue;
                var header = headers[i].ToLowerInvariant();
                if (header.Length == 0) continue;
                if (synonyms.Any(header.Contains))
                {
                    map[i] = kind;
                    taken.Add(kind);
                    break;
                }
            }
        }

        return map;
    }

    private static List<HoldingPosition> ParseHoldings(string[][] table, Dictionary<int, string> map)
    {
        var holdings = new List<HoldingPosition>();

        foreach (var row in table.Skip(1))
        {
            string? Cell(string kind)
            {
                var idx = map.FirstOrDefault(kv => kv.Value == kind, new(-1, "")).Key;
                return idx >= 0 && idx < row.Length ? row[idx] : null;
            }

            var symbol = (Cell("symbol") ?? "").Trim().ToUpperInvariant();
            // Skip repeated header rows, totals/footer rows and anything that isn't a ticker.
            if (symbol.Length is 0 or > 12) continue;
            if (!Regex.IsMatch(symbol, @"^[A-Z][A-Z0-9.\-]*$")) continue;
            if (symbol is "TOTAL" or "SYMBOL" or "SCRIP") continue;

            var qty        = ParseAmount(Cell("quantity"));
            if (qty is null or <= 0) continue;

            var avgPrice   = ParseAmount(Cell("avgPrice"));
            var investment = ParseAmount(Cell("investment")) ?? (avgPrice is not null ? avgPrice * qty : null);
            var price      = ParseAmount(Cell("currentPrice"));
            var value      = ParseAmount(Cell("currentValue")) ?? (price is not null ? price * qty : null);
            var pl         = ParseAmount(Cell("profitLoss")) ??
                             (value is not null && investment is not null ? value - investment : null);

            holdings.Add(new HoldingPosition
            {
                Symbol            = symbol,
                Quantity          = qty,
                AverageBuyPrice   = avgPrice ?? (investment is not null && qty > 0 ? Math.Round(investment.Value / qty.Value, 4) : null),
                InvestmentValue   = investment,
                CurrentPrice      = price ?? (value is not null && qty > 0 ? Math.Round(value.Value / qty.Value, 4) : null),
                CurrentValue      = value,
                ProfitLoss        = pl,
                ProfitLossPercent = pl is not null && investment is > 0
                    ? Math.Round(pl.Value / investment.Value * 100m, 2)
                    : null
            });
        }

        return holdings;
    }

    /// <summary>
    /// Reads the cash amount by finding the configured label's line inside the configured scope
    /// element (AHK: the "Net Cash" row of the #exposuretable1 summary panel — innerText renders
    /// each table row as "Net Cash\t255.00"). Falls back to generic balance keywords, and to the
    /// whole page text when the scope selector matches nothing. Returns the value plus the line it
    /// was read from (audit trail).
    /// </summary>
    private async Task<(decimal? Balance, string? Source)> ReadAvailableBalanceAsync(AhkConfig cfg)
    {
        var scope = "";
        if (!string.IsNullOrWhiteSpace(cfg.AvailableBalanceSelector))
        {
            scope = await _page!.EvaluateFunctionAsync<string>(
                "(sel) => { const e = document.querySelector(sel); return e ? (e.innerText || e.textContent || '') : ''; }",
                cfg.AvailableBalanceSelector) ?? "";
        }
        if (string.IsNullOrWhiteSpace(scope))
        {
            scope = await _page!.EvaluateFunctionAsync<string>(
                "() => (document.body && document.body.innerText) ? document.body.innerText : ''") ?? "";
        }

        var labels = new List<string>();
        if (!string.IsNullOrWhiteSpace(cfg.AvailableBalanceLabel))
            labels.Add(cfg.AvailableBalanceLabel.Trim());
        labels.AddRange(_balanceKeywords);

        var lines = scope.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        foreach (var label in labels)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(label, StringComparison.OrdinalIgnoreCase)) continue;

                // The amount usually sits on the same line (table cells collapse to one innerText
                // line); some layouts put it on the following line.
                var value = FirstAmountIn(lines[i]) ??
                            (i + 1 < lines.Length ? FirstAmountIn(lines[i + 1]) : null);
                if (value is not null)
                {
                    var source = lines[i].Length > 160 ? lines[i][..160] : lines[i];
                    return (value, source);
                }
            }
        }

        return (null, null);
    }

    private static decimal? FirstAmountIn(string line)
    {
        var match = Regex.Match(line, @"-?[0-9][0-9,]*(?:\.[0-9]+)?");
        return match.Success ? ParseAmount(match.Value) : null;
    }

    /// <summary>Parses "12,345.67", "Rs. 12,345.67" or "(1,234)" (negative) into a decimal; null when not numeric.</summary>
    private static decimal? ParseAmount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var negative = raw.Contains('(') && raw.Contains(')') || raw.Contains('-');
        var cleaned = Regex.Replace(raw, @"[^0-9.]", "");
        if (cleaned.Length == 0) return null;

        // Guard against multi-number strings collapsing into nonsense ("12.34.56").
        if (cleaned.Count(c => c == '.') > 1) return null;

        if (!decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return null;

        return negative ? -value : value;
    }

    private static decimal? SumIfAny<T>(IReadOnlyList<T> items, Func<T, decimal?> selector)
    {
        var values = items.Select(selector).Where(v => v is not null).ToList();
        return values.Count == 0 ? null : values.Sum();
    }

    /// <summary>
    /// Saves the page behind an unrecognised order popup, so its markup can be read later and the
    /// classifier taught to recognise it. Best-effort: a failed dump must never change an order's
    /// verdict.
    /// </summary>
    private async Task DumpOrderPopupAsync()
    {
        try
        {
            var tag = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            await ScreenshotAsync($"orderpopup_{tag}");
            var html = await _page!.EvaluateFunctionAsync<string>(
                "() => (document.querySelector('.swal-modal') || document.body).outerHTML");
            var path = Path.Combine(ResolvePath(_config.Current.LogDir), $"orderpopup_{tag}.html");
            await File.WriteAllTextAsync(path, html);
            _logger.LogWarning("[AhkBroker] Dumped the unrecognised order popup to {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AhkBroker] Could not dump the unrecognised order popup.");
        }
    }

    private async Task DumpPortfolioPageAsync(string tag)
    {
        try
        {
            await ScreenshotAsync($"portfolio_{tag}");
            var html = await _page!.EvaluateFunctionAsync<string>("() => document.body.innerHTML");
            var path = Path.Combine(ResolvePath(_config.Current.LogDir),
                $"portfolio_{tag}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.html");
            await File.WriteAllTextAsync(path, html);
            _logger.LogWarning("[AhkBroker] Dumped portfolio page HTML to {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AhkBroker] Could not dump portfolio page.");
        }
    }

    // ── Price-band clamp ──────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the limit price to submit, clamping it into the day's price band (Lower Lock / Upper Cap)
    /// when <see cref="AhkConfig.ClampPriceToBand"/> is on and the band can be read from the open dialog.
    /// A price above the Upper Cap is lowered to the cap; a price below the Lower Lock is raised to the
    /// lock (PSX rejects anything outside the band). Returns the (possibly unchanged) price and a note
    /// describing any clamp, or null. ASSUMES the dialog is open and the symbol resolved.
    /// </summary>
    private async Task<(decimal price, string? note)> ResolveLimitPriceAsync(decimal requested, string side)
    {
        if (!_config.Current.ClampPriceToBand)
            return (requested, null);

        var (lowerLock, upperCap) = await ReadPriceBandAsync(side);

        if (upperCap is > 0 && requested > upperCap.Value)
        {
            var note = $"Limit clamped down from {requested:F2} to the day's Upper Cap {upperCap.Value:F2}.";
            _logger.LogWarning("[AhkBroker] {Note}", note);
            return (upperCap.Value, note);
        }

        if (lowerLock is > 0 && requested < lowerLock.Value)
        {
            var note = $"Limit clamped up from {requested:F2} to the day's Lower Lock {lowerLock.Value:F2}.";
            _logger.LogWarning("[AhkBroker] {Note}", note);
            return (lowerLock.Value, note);
        }

        return (requested, null);
    }

    /// <summary>
    /// Reads the day's Lower Lock and Upper Cap for the resolved symbol from the order dialog. Reads the
    /// confirmed stable element ids first — SELL: <c>#sf-selluppercap</c> / <c>#sf-selllowerlock</c>,
    /// BUY: <c>#bf-uppercap</c> / <c>#bf-lowerlock</c> — and falls back to matching the
    /// "Lower Lock"/"Upper Cap" columns in the quote table if an id is missing. Returns (null, null) when
    /// the band can't be found, so the caller leaves the price unchanged. <paramref name="side"/> is
    /// "buy" or "sell".
    /// </summary>
    private async Task<(decimal? lowerLock, decimal? upperCap)> ReadPriceBandAsync(string side)
    {
        var s = string.Equals(side, "sell", StringComparison.OrdinalIgnoreCase) ? "sell" : "buy";
        string raw;
        try
        {
            raw = await _page!.EvaluateFunctionAsync<string>(@"(side) => {
                const txt   = el => el ? (el.innerText || el.value || '').trim() : '';
                const byId  = id => txt(document.getElementById(id));

                // 1) Confirmed stable element ids — note the buy/sell ids do NOT share a pattern.
                const ids = side === 'sell'
                    ? { up: 'sf-selluppercap', lo: 'sf-selllowerlock' }
                    : { up: 'bf-uppercap',     lo: 'bf-lowerlock' };
                let up = byId(ids.up);
                let lo = byId(ids.lo);

                // 2) Fallback: match the quote-table header columns if an id is missing/renamed.
                if (!up || !lo) {
                    const norm = x => (x || '').replace(/\s+/g, ' ').trim().toLowerCase();
                    for (const t of Array.from(document.querySelectorAll('table'))) {
                        const rows = Array.from(t.rows || []);
                        if (rows.length < 2) continue;
                        const headers = Array.from(rows[0].cells || []).map(c => norm(c.innerText));
                        const upIdx = headers.findIndex(h => h.includes('upper') && (h.includes('cap') || h.includes('lock')));
                        const loIdx = headers.findIndex(h => h.includes('lower') && h.includes('lock'));
                        if (upIdx < 0 && loIdx < 0) continue;
                        for (let r = 1; r < rows.length; r++) {
                            const cells = rows[r].cells;
                            if (!cells || cells.length <= Math.max(upIdx, loIdx)) continue;
                            if (!up && upIdx >= 0) up = txt(cells[upIdx]);
                            if (!lo && loIdx >= 0) lo = txt(cells[loIdx]);
                            if (up && lo) break;
                        }
                        if (up || lo) break;
                    }
                }
                return (lo || '') + '|' + (up || '');
            }", s) ?? "";
        }
        catch { return (null, null); }

        if (string.IsNullOrWhiteSpace(raw)) return (null, null);

        var parts = raw.Split('|');
        var lower = parts.Length > 0 ? ParseBandValue(parts[0]) : null;
        var upper = parts.Length > 1 ? ParseBandValue(parts[1]) : null;

        if (lower is not null || upper is not null)
            _logger.LogInformation("[AhkBroker] Price band: LowerLock={Lower} UpperCap={Upper}.", lower, upper);

        return (lower, upper);
    }

    /// <summary>Parses a band cell value, stripping thousands separators and stray characters.</summary>
    private static decimal? ParseBandValue(string raw)
    {
        var cleaned = Regex.Replace(raw ?? "", @"[^0-9.]", "");
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && v > 0m
            ? v : (decimal?)null;
    }

    /// <summary>Routes a single signal to the BUY/SELL placement. ASSUMES the gate is held and session ready.</summary>
    private async Task<OrderResult> DispatchOrderAsync(TradingSignal signal) =>
        signal.Action.ToUpperInvariant() switch
        {
            "BUY"  => await PlaceBuyAsync(signal),
            "SELL" => await PlaceSellAsync(signal),
            _      => new OrderResult
            {
                Success = false,
                Message = $"Unsupported action '{signal.Action}'. Only BUY and SELL are supported."
            }
        };

    /// <summary>
    /// Brings the session to a logged-in, order-ready state. ASSUMES the gate is held. On any
    /// browser/infra failure it forces a full session restart (kill stray browser, relaunch,
    /// re-login) and retries once, so a controllable failure does not cause a missed order.
    /// </summary>
    private async Task PrepareSessionWithRetryAsync()
    {
        const int maxAttempts = 2;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await EnsureBrowserAsync(forceRestart: attempt > 1);

                if (!IsHealthy())
                    throw new InvalidOperationException("Browser unavailable after launch.");

                if (!await IsLoggedInAsync())
                {
                    _activity?.Info("Broker", "Logging in to the broker portal");
                    await LoginAsync();
                }

                return; // ready
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "[AhkBroker] Session not ready (attempt {Attempt}/{Max}) — restarting browser and retrying.",
                    attempt, maxAttempts);
                _activity?.Warn("Broker",
                    $"Broker session not ready (attempt {attempt}/{maxAttempts}) — restarting the browser",
                    ex.Message);
            }
        }
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    private async Task<bool> IsLoggedInAsync()
    {
        try
        {
            return await _page!.QuerySelectorAsync(_config.Current.LoggedInSelector) is not null;
        }
        catch { return false; }
    }

    private async Task LoginAsync()
    {
        var cfg = _config.Current;
        _logger.LogInformation("[AhkBroker] Logging in to {Url}", cfg.PortalUrl);

        await _page!.GoToAsync(cfg.PortalUrl, WaitUntilNavigation.Networkidle0);
        _logger.LogInformation("[AhkBroker] Login page loaded: {Url}", _page.Url);

        // A persisted profile may still be authenticated (cookies survive a browser close). In that
        // case the portal lands directly on the trading screen with no login form, so trying to fill
        // credentials would fail with "username field not found". Detect that and skip login.
        // The decision is deliberately delayed until the page has actually rendered one of the two:
        // on a slow machine networkidle0 fires while the screen is still blank, and reading it that
        // early misclassifies an authenticated session as "login page with no username field".
        await WaitForPageReadyAsync(cfg);

        if (await IsLoggedInAsync())
        {
            _logger.LogInformation("[AhkBroker] Already authenticated via persisted session — skipping credential entry.");
            return;
        }

        // 1. Username — discovered robustly (a wrong/redirected PortalUrl shows up here as "not found").
        var userHandle = await FindUsernameFieldAsync(cfg);
        if (userHandle is null)
        {
            await DumpLoginFormAsync("no_username");
            throw new InvalidOperationException(
                $"Username field not found on '{_page.Url}'. If that is not the AHK login page, fix " +
                "Ahk.PortalUrl; otherwise set Ahk.UsernameSelector (see the dumped login_no_username.html).");
        }
        await TypeIntoAsync(userHandle, cfg.Username);

        // 2. Positional password — read which character positions the portal is asking for THIS login.
        await FillPositionalPasswordAsync(cfg);

        // 3. Submit.
        await ClickLoginButtonAsync(cfg);

        // 4. Verify, surfacing any on-page error instead of a blind timeout.
        await VerifyLoggedInAsync(cfg);

        _logger.LogInformation("[AhkBroker] Login successful.");
    }

    /// <summary>
    /// Waits — bounded by PageReadyTimeoutMs — until the loaded portal shows EITHER the trading screen
    /// (LoggedInSelector, i.e. a still-valid persisted session) OR a login form. Returns when one of
    /// them appears; on timeout it simply returns and the caller reports the concrete failure with a
    /// page dump, so this can never turn a real problem into an indefinite hang.
    /// </summary>
    private async Task WaitForPageReadyAsync(AhkConfig cfg)
    {
        var timeout  = Math.Max(1_000, cfg.PageReadyTimeoutMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);

        while (DateTime.UtcNow < deadline)
        {
            if (await IsLoggedInAsync()) return;
            if (await FindUsernameFieldAsync(cfg) is not null) return;
            await Task.Delay(250);
        }

        _logger.LogWarning(
            "[AhkBroker] Neither the trading screen ('{LoggedIn}') nor a login form appeared within {Timeout}ms on '{Url}'.",
            cfg.LoggedInSelector, timeout, _page!.Url);
    }

    /// <summary>
    /// Fills the AHK positional-password grid. The portal asks for a changing subset of characters
    /// ("enter 2nd,3rd,5th,6th character of your password"); we parse that instruction, pair each
    /// requested position with the corresponding enabled box (left-to-right), and type the matching
    /// character from the configured full password.
    /// </summary>
    private async Task FillPositionalPasswordAsync(AhkConfig cfg)
    {
        var password = cfg.Password ?? "";

        var pageText    = await _page!.EvaluateFunctionAsync<string>("() => document.body?.innerText || ''");
        var positions   = ParsePasswordPositions(pageText);

        // Editable single-character boxes, in DOM (left-to-right) order.
        var allBoxes = await _page.QuerySelectorAllAsync(cfg.PasswordBoxSelector);
        var editable = new List<IElementHandle>();
        foreach (var box in allBoxes)
        {
            var usable = await box.EvaluateFunctionAsync<bool>(
                "e => !e.disabled && !e.readOnly && e.offsetParent !== null");
            if (usable) editable.Add(box);
        }

        // Fallback: not a positional grid (single full-password field).
        if (positions.Count == 0 && editable.Count <= 1)
        {
            var pwd = editable.Count == 1 ? editable[0] : await FindFullPasswordFieldAsync();
            if (pwd is null)
            {
                await DumpLoginFormAsync("no_password");
                throw new InvalidOperationException("Password field not found. See dumped login_no_password.html.");
            }
            await TypeIntoAsync(pwd, password);
            return;
        }

        if (positions.Count == 0)
        {
            await DumpLoginFormAsync("no_password_instruction");
            throw new InvalidOperationException(
                $"Found {editable.Count} password boxes but could not read which character positions to enter. " +
                "See dumped login_no_password_instruction.html.");
        }

        if (editable.Count != positions.Count)
        {
            await DumpLoginFormAsync("password_mismatch");
            throw new InvalidOperationException(
                $"Positional-password mismatch: portal asked for {positions.Count} position(s) " +
                $"[{string.Join(",", positions)}] but found {editable.Count} editable box(es). " +
                "Adjust Ahk.PasswordBoxSelector (see login_password_mismatch.html).");
        }

        for (var k = 0; k < positions.Count; k++)
        {
            var pos = positions[k];
            if (pos < 1 || pos > password.Length)
                throw new InvalidOperationException(
                    $"Portal requested character #{pos} but the configured password has only {password.Length} character(s).");

            await TypeIntoAsync(editable[k], password[pos - 1].ToString());
        }

        _logger.LogInformation(
            "[AhkBroker] Entered {Count} positional password character(s) at positions [{Positions}].",
            positions.Count, string.Join(",", positions));
    }

    /// <summary>
    /// Extracts the requested 1-based character positions from an instruction such as
    /// "Please enter 2nd,3rd,5th,6th character of your password". Order is preserved.
    /// </summary>
    private static List<int> ParsePasswordPositions(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();

        // Scope to the phrase between "enter" and "character" so we don't pick up unrelated numbers.
        var scope = Regex.Match(text, @"enter(.*?)character",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var window = scope.Success ? scope.Groups[1].Value : text;

        var seen = new HashSet<int>();
        var result = new List<int>();
        foreach (Match m in Regex.Matches(window, @"\d+"))
        {
            if (int.TryParse(m.Value, out var n) && n is >= 1 and <= 64 && seen.Add(n))
                result.Add(n);
        }
        return result;
    }

    private async Task<IElementHandle?> FindUsernameFieldAsync(AhkConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.UsernameSelector))
            return await _page!.QuerySelectorAsync(cfg.UsernameSelector);

        foreach (var selector in new[]
                 {
                     "#ps_userid",
                     "input[name*='user' i]",
                     "input[id*='user' i]",
                     "input[placeholder*='user' i]",
                     "input[type='text']:not([maxlength='1'])",
                     "input:not([type]):not([maxlength='1'])"
                 })
        {
            var handle = await _page!.QuerySelectorAsync(selector);
            if (handle is not null) return handle;
        }
        return null;
    }

    private async Task<IElementHandle?> FindFullPasswordFieldAsync()
    {
        foreach (var selector in new[] { "#ps_password", "input[type='password']", "input[name*='pass' i]" })
        {
            var handle = await _page!.QuerySelectorAsync(selector);
            if (handle is not null) return handle;
        }
        return null;
    }

    private async Task ClickLoginButtonAsync(AhkConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.LoginButtonSelector))
        {
            await _page!.ClickAsync(cfg.LoginButtonSelector);
            return;
        }

        var clicked = await _page!.EvaluateFunctionAsync<bool>(@"() => {
            const els = Array.from(document.querySelectorAll(
                ""button, input[type='submit'], input[type='button'], a[role='button']""));
            const el = els.find(e => /log\s*in|sign\s*in/i.test((e.textContent || '') + ' ' + (e.value || '')));
            if (el) { el.click(); return true; }
            // Fall back to a lone submit button if no text matched.
            const submit = document.querySelector(""button[type='submit'], input[type='submit']"");
            if (submit) { submit.click(); return true; }
            return false;
        }");

        if (!clicked)
        {
            await DumpLoginFormAsync("no_login_button");
            throw new InvalidOperationException("Login button not found. See dumped login_no_login_button.html.");
        }
    }

    private async Task VerifyLoggedInAsync(AhkConfig cfg)
    {
        try
        {
            await _page!.WaitForSelectorAsync(cfg.LoggedInSelector,
                new WaitForSelectorOptions { Timeout = Math.Max(15_000, cfg.LoginVerifyTimeoutMs) });
        }
        catch (Exception)
        {
            var text  = await _page!.EvaluateFunctionAsync<string>("() => document.body?.innerText || ''");
            var lower = text.ToLowerInvariant();
            var err   = _errorMarkers.FirstOrDefault(m => lower.Contains(m));
            await DumpLoginFormAsync("login_unverified");

            throw new InvalidOperationException(err is not null
                ? $"Login appears to have failed: {ExtractLine(text, err)}"
                : $"Login could not be confirmed — selector '{cfg.LoggedInSelector}' not found on '{_page.Url}'. " +
                  "Set Ahk.LoggedInSelector to an element unique to the post-login page (see login_login_unverified.html).");
        }
    }

    /// <summary>Focus, clear, and type — using real key events so the portal's input handlers fire.</summary>
    private async Task TypeIntoAsync(IElementHandle element, string text)
    {
        await element.ClickAsync();
        await _page!.Keyboard.DownAsync("Control");
        await _page.Keyboard.PressAsync("KeyA");
        await _page.Keyboard.UpAsync("Control");
        await _page.Keyboard.PressAsync("Delete");
        await element.TypeAsync(text);
    }

    /// <summary>Saves a screenshot and the login form HTML to LogDir to make selector debugging concrete.</summary>
    private async Task DumpLoginFormAsync(string tag)
    {
        try
        {
            await ScreenshotAsync($"login_{tag}");
            var html = await _page!.EvaluateFunctionAsync<string>(
                "() => { const f = document.querySelector('form'); return f ? f.outerHTML : document.body.innerHTML; }");
            var path = Path.Combine(ResolvePath(_config.Current.LogDir),
                $"login_{tag}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.html");
            await File.WriteAllTextAsync(path, html);
            _logger.LogWarning("[AhkBroker] Dumped login form HTML to {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AhkBroker] Could not dump login form.");
        }
    }

    // ── BUY ───────────────────────────────────────────────────────────────────

    private async Task<OrderResult> PlaceBuyAsync(TradingSignal signal)
    {
        var cfg     = _config.Current;
        var qty     = signal.Quantity ?? cfg.DefaultQty;
        var isStop  = signal.OrderType.Equals("STOPLOSS", StringComparison.OrdinalIgnoreCase);
        var isLimit = !signal.OrderType.Equals("MARKET", StringComparison.OrdinalIgnoreCase);

        _logger.LogInformation("[AhkBroker] BUY {Symbol} x{Qty} @ {Price} ({Type})",
            signal.Symbol, qty, signal.EntryPrice, signal.OrderType);

        // When the session is already logged in the dialog is closed and its fields are hidden in the
        // DOM — open it first so the fields and the BUY button are actually interactable.
        await OpenOrderDialogAsync("buy");

        // Set order type explicitly so a stale "Market"/"Limit" from a prior order can't misroute this one.
        await SelectByVisibleTextAsync("#buyordertype", isStop ? "Stop Loss" : isLimit ? "Limit" : "Market");

        await FillFieldAsync("#buysymbol", signal.Symbol);
        await ResolveSymbolAsync("buy");

        await FillFieldAsync("#buyvolume", qty.ToString());

        decimal? requestedPrice = null, submittedPrice = null;
        decimal? submittedLimitPrice = null;
        string?  priceAdjustment = null;
        if (isLimit && signal.EntryPrice.HasValue)
        {
            requestedPrice = signal.EntryPrice.Value;
            (var finalPrice, priceAdjustment) = await ResolveLimitPriceAsync(requestedPrice.Value, "buy");
            submittedPrice = finalPrice;

            if (isStop)
            {
                await WaitForLimitPriceEnabledAsync("buy");
                var limit = signal.LimitPrice
                    ?? decimal.Round(finalPrice * (1m + Math.Clamp(
                        cfg.StopLimitSlippagePercent, 0m, 20m) / 100m), 2);
                (limit, _) = await ResolveLimitPriceAsync(limit, "buy");
                submittedLimitPrice = limit;
            }

            if (PriceIntentRule.Validate(signal, finalPrice, submittedLimitPrice) is { } priceProblem)
            {
                return new OrderResult
                {
                    Success = false,
                    Action = "BUY",
                    Symbol = signal.Symbol,
                    Quantity = qty,
                    Message = priceProblem,
                    RequestedPrice = requestedPrice
                };
            }

            // #buylimitprice is DISABLED for every order type except Stop Loss (the portal's
            // order-type handler owns that), so writing to it on an ordinary limit order targets a
            // disabled input: the value never sticks and FillFieldAsync spends its verify-and-refill
            // retry for nothing. Only #buyprice matters here.
            await FillFieldAsync("#buyprice", finalPrice.ToString("F2"));
            if (submittedLimitPrice is { } buyLimit)
                await FillFieldAsync("#buylimitprice", buyLimit.ToString("F2"));
        }

        await FillFieldAsync("#buyPIN", cfg.TradingPin);

        var before = await ScreenshotAsync("pre_buy");

        await ClickSubmitAsync("buy");
        var confirmed = await ConfirmOrderAsync("Buy");

        var popup   = await ReadOrderOutcomeAsync(cfg.OrderConfirmTimeoutMs, confirmed);
        // The popup is a hint; the account's own order book is the evidence.
        var outcome = await ConfirmAgainstBookAsync(signal, popup);
        var after   = await ScreenshotAsync("post_buy");

        return new OrderResult
        {
            Success          = outcome.Success,
            OrderId          = outcome.OrderId,
            Action           = "BUY",
            Symbol           = signal.Symbol,
            Quantity         = qty,
            Message          = outcome.Message ?? $"BUY {signal.Symbol} x{qty}: outcome unconfirmed.",
            ScreenshotBefore = before,
            ScreenshotAfter  = after,
            RequestedPrice   = requestedPrice,
            SubmittedPrice   = submittedPrice,
            PriceAdjustment  = priceAdjustment
        };
    }

    // ── SELL ──────────────────────────────────────────────────────────────────

    private async Task<OrderResult> PlaceSellAsync(TradingSignal signal)
    {
        var cfg      = _config.Current;
        var qty      = signal.Quantity ?? cfg.DefaultQty;
        var isStop   = signal.OrderType.Equals("STOPLOSS", StringComparison.OrdinalIgnoreCase);
        var isMarket = signal.OrderType.Equals("MARKET", StringComparison.OrdinalIgnoreCase);
        var isLimit  = !isMarket;

        _logger.LogInformation("[AhkBroker] SELL {Symbol} x{Qty} @ {Price} ({Type})",
            signal.Symbol, qty, signal.EntryPrice, signal.OrderType);

        // When the session is already logged in the dialog is closed and its fields are hidden in the
        // DOM — open it first so the fields and the SELL button are actually interactable.
        await OpenOrderDialogAsync("sell");

        // Set order type explicitly so a stale type from a prior order can't misroute this one. The
        // portal's option LABELS are what SelectByVisibleText matches ("Stop Loss" carries a space,
        // while the underlying option value is "StopLoss"); selecting it is also what ENABLES
        // #selllimitprice — see the readiness wait below.
        await SelectByVisibleTextAsync("#sellordertype", isStop ? "Stop Loss" : isMarket ? "Market" : "Limit");

        // The trade type is NOT reset when the dialog is opened from the toolbar (only the portal's own
        // SellOrder() resets it), so a stale "Short Sell" from a previous dialog would otherwise ride
        // along and turn a protective exit into a short.
        await SelectByVisibleTextAsync("#selltradetype", "SEL");

        await FillFieldAsync("#sellsymbol", signal.Symbol);
        await ResolveSymbolAsync("sell");

        await FillFieldAsync("#sellvolume", qty.ToString());

        decimal? requestedPrice = null, submittedPrice = null;
        decimal? submittedLimitPrice = null;
        string?  priceAdjustment = null;
        if (isLimit && signal.EntryPrice.HasValue)
        {
            requestedPrice = signal.EntryPrice.Value;
            (var finalPrice, priceAdjustment) = await ResolveLimitPriceAsync(requestedPrice.Value, "sell");
            submittedPrice = finalPrice;

            // #sellprice is the TRIGGER for a stop order and the limit for an ordinary one; the portal
            // reads it as whichever the selected type implies.
            await FillFieldAsync("#sellprice", finalPrice.ToString("F2"));

            if (isStop)
            {
                // Only a Stop Loss order enables #selllimitprice — writing to it for any other type
                // targets a DISABLED input, where the value silently fails to stick and FillFieldAsync
                // then burns its verify-and-refill retry. Wait for the enable rather than assuming the
                // change handler has run.
                await WaitForLimitPriceEnabledAsync("sell");

                // A stop limit placed exactly AT the trigger frequently misses the fast move that
                // triggered it, so the limit sits a slippage allowance BELOW it for a sell.
                var limit = signal.LimitPrice
                    ?? decimal.Round(finalPrice * (1m - Math.Clamp(cfg.StopLimitSlippagePercent, 0m, 20m) / 100m), 2);
                (limit, _) = await ResolveLimitPriceAsync(limit, "sell");
                submittedLimitPrice = limit;

                if (PriceIntentRule.Validate(signal, finalPrice, submittedLimitPrice) is { } priceProblem)
                {
                    return new OrderResult
                    {
                        Success = false,
                        Action = "SELL",
                        Symbol = signal.Symbol,
                        Quantity = qty,
                        Message = priceProblem,
                        RequestedPrice = requestedPrice
                    };
                }

                await FillFieldAsync("#selllimitprice", limit.ToString("F2"));
                _logger.LogInformation(
                    "[AhkBroker] SELL {Symbol} STOP trigger {Trigger} → limit {Limit}.",
                    signal.Symbol, finalPrice, limit);
            }
            else if (PriceIntentRule.Validate(signal, finalPrice, null) is { } priceProblem)
            {
                return new OrderResult
                {
                    Success = false,
                    Action = "SELL",
                    Symbol = signal.Symbol,
                    Quantity = qty,
                    Message = priceProblem,
                    RequestedPrice = requestedPrice
                };
            }
        }

        await FillFieldAsync("#sellPIN", cfg.TradingPin);

        var before = await ScreenshotAsync("pre_sell");

        await ClickSubmitAsync("sell");
        var confirmed = await ConfirmOrderAsync("Sell");

        var popup   = await ReadOrderOutcomeAsync(cfg.OrderConfirmTimeoutMs, confirmed);
        var outcome = await ConfirmAgainstBookAsync(signal, popup);
        var after   = await ScreenshotAsync("post_sell");

        return new OrderResult
        {
            Success          = outcome.Success,
            OrderId          = outcome.OrderId,
            Action           = "SELL",
            Symbol           = signal.Symbol,
            Quantity         = qty,
            Message          = outcome.Message ?? $"SELL {signal.Symbol} x{qty}: outcome unconfirmed.",
            ScreenshotBefore = before,
            ScreenshotAfter  = after,
            RequestedPrice   = requestedPrice,
            SubmittedPrice   = submittedPrice,
            PriceAdjustment  = priceAdjustment
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Clears the field, then types the new value with real key events (so the portal's
    /// input/autocomplete handlers fire), and verifies the value stuck — refilling once if not.
    ///
    /// Keyboard Ctrl+A select-all is unreliable on the portal's autocomplete (#buysymbol) and number
    /// (#buyprice/#buylimitprice) fields: when the selection doesn't take, TypeAsync APPENDS to the
    /// existing text. That produced "LUCKLUCK" symbols and doubled prices, and also clobbers the
    /// last-trade price the portal auto-fills into #buyprice on symbol resolution. We instead select
    /// all via triple-click + el.select() and press Backspace to guarantee an empty field first.
    /// </summary>
    private async Task FillFieldAsync(string selector, string value)
    {
        // The modal's fields are rendered asynchronously — on a slow machine we arrive before they
        // exist, so wait for the field rather than failing the order on the first lookup.
        var timeout = Math.Max(1_000, _config.Current.DialogOpenTimeoutMs);
        await WaitForVisibleAsync(selector, timeout);

        var el = await _page!.QuerySelectorAsync(selector)
                 ?? throw new InvalidOperationException(
                     $"Order field '{selector}' not found on the form (waited {timeout}ms).");

        await ClearAndTypeAsync(el, selector, value);

        // Read back; if the field holds something other than our value (stale text not cleared, or an
        // async portal re-populate), clear and type once more. One retry — never an infinite fight.
        var actual = await el.EvaluateFunctionAsync<string>("e => (e.value ?? '').toString()");
        if (!string.Equals(actual?.Trim(), value.Trim(), StringComparison.Ordinal))
        {
            await Task.Delay(150);
            await ClearAndTypeAsync(el, selector, value);
        }
    }

    /// <summary>
    /// Completes symbol entry in the open order dialog: lets the autocomplete dropdown render, accepts
    /// it with Tab, then waits — bounded by SymbolResolveTimeoutMs — for the portal to auto-fill the
    /// price field from the last trade. Returns that price, or null if it never appeared.
    ///
    /// Waiting for the auto-fill rather than sleeping a fixed interval matters twice over on a slow
    /// machine: the price band we clamp against is only rendered once the symbol resolves, and a
    /// populate that lands after we have typed our own limit price silently overwrites it.
    /// </summary>
    private async Task<decimal?> ResolveSymbolAsync(string side)
    {
        var priceField = side == "sell" ? "#sellprice" : "#buyprice";
        var timeout    = Math.Max(1_000, _config.Current.SymbolResolveTimeoutMs);

        await Task.Delay(800); // autocomplete dropdown must be rendered before Tab can accept it
        await _page!.Keyboard.PressAsync("Tab");

        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (true)
        {
            // The authoritative signal that the portal accepted the symbol is the PRICE BAND, not the
            // price field: entering a symbol populates the upperCap/lowerCap globals (from the
            // preloaded objUpperLower table) even when no price is auto-filled. On the SELL path the
            // price field is never populated at all — only the portal's own SellOrder() sets it, and
            // that runs when opening from a market-watch row rather than the toolbar we click — so
            // waiting on the price alone burned this entire timeout on every sell.
            var band = await ReadPriceBandAsync();
            if (band is { Upper: > 0m })
            {
                var raw = await _page.EvaluateFunctionAsync<string>(
                    "(sel) => { const e = document.querySelector(sel); return e ? (e.value || '') : ''; }",
                    priceField) ?? "";
                var cleaned = Regex.Replace(raw, @"[^0-9.]", "");
                return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var price)
                    && price > 0m ? price : null;
            }

            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(150);
        }

        // Not fatal on its own, but it is the signature of an unresolved symbol — and the portal's own
        // submit handler refuses silently when the band is missing, so this is worth shouting about.
        _logger.LogWarning(
            "[AhkBroker] No price band appeared within {Timeout}ms after entering the symbol. The portal "
            + "may not have resolved it; submission would be rejected client-side without an error.",
            timeout);
        return null;
    }

    /// <summary>
    /// Reconciles the popup's verdict against the account's order book and returns the outcome we are
    /// actually willing to record.
    ///
    /// <para>
    /// The book wins in both directions. Found in the book ⇒ the order exists, whatever the popup said,
    /// and we adopt the exchange's order number. Absent from the book after a successful-looking
    /// submission ⇒ NOT success: the portal is known to answer HTTP 200 with an empty body and a green
    /// alert while placing nothing. A book we could not read at all leaves the popup's verdict alone
    /// but downgrades a claimed success to unconfirmed, because "we could not check" and "it is there"
    /// are not the same statement.
    /// </para>
    /// </summary>
    private async Task<OrderOutcome> ConfirmAgainstBookAsync(TradingSignal signal, OrderOutcome popup)
    {
        if (!_config.Current.VerifyOrderInBook) return popup;

        var match = await VerifyOrderInBookAsync(signal);
        if (match is not null)
        {
            return new OrderOutcome(
                true,
                $"Order verified in the {match.Book} log"
                + (match.OrderNo is not null ? $" (order no {match.OrderNo})" : "")
                + $". Portal said: {popup.Message}",
                match.OrderNo ?? popup.OrderId);
        }

        return new OrderOutcome(
            false,
            popup.Success
                ? "The portal reported success, but the order does NOT appear in the outstanding or "
                + "activity log. Treating it as NOT placed: this portal returns an empty 200 with a "
                + $"'success' alert while placing nothing (e.g. outside market hours). Portal said: {popup.Message}"
                : $"{popup.Message} The order does not appear in the order book either.",
            popup.OrderId);
    }

    /// <summary>
    /// Confirms a submitted order actually exists, by reading the account's own order book rather than
    /// believing the portal's result popup.
    ///
    /// <para>
    /// <b>Why this exists.</b> Measured against the live portal: an off-hours submission returns
    /// HTTP 200 with an empty response body and displays a green "success" alert, while placing
    /// nothing — the order appears in neither the outstanding nor the activity log. The happy path
    /// returns no order number either. So the popup cannot distinguish "placed" from "silently
    /// discarded", and the book is the only ground truth.
    /// </para>
    ///
    /// <para>
    /// Both logs are checked: a resting order shows in the outstanding book, but an order that filled
    /// immediately never rests, and treating its absence there as "never placed" would be exactly
    /// backwards. Returns the exchange's order number when found — which is the only place we can get
    /// one at all.
    /// </para>
    /// </summary>
    private async Task<OrderBookMatch?> VerifyOrderInBookAsync(TradingSignal signal)
    {
        var cfg = _config.Current;
        if (!cfg.VerifyOrderInBook) return null;

        var symbol   = signal.Symbol.Trim().ToUpperInvariant();
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1_000, cfg.OrderBookVerifyTimeoutMs));

        while (DateTime.UtcNow < deadline)
        {
            foreach (var (tab, panel, book) in new[]
                     {
                         (cfg.OutstandingLogTabSelector, cfg.OutstandingLogPanelSelector, "outstanding"),
                         (cfg.ActivityLogTabSelector,    cfg.ActivityLogPanelSelector,    "activity")
                     })
            {
                try
                {
                    var match = await ReadOrderBookAsync(tab, panel, book, symbol);
                    if (match is not null) return match;
                }
                catch (Exception ex)
                {
                    // A failure to READ the book must not be reported as a failure to place the order —
                    // those are opposite conclusions, and guessing between them is how a real position
                    // becomes invisible.
                    _logger.LogWarning(ex, "[AhkBroker] Could not read the {Book} log while verifying.", book);
                }
            }

            await Task.Delay(500);
        }

        return null;
    }

    /// <summary>
    /// Opens a log tab, refreshes it, and looks for a row for <paramref name="symbol"/>. Row shape was
    /// taken from the live portal: the outstanding table's columns are
    /// Trader, Market, Scrip, Price, Remaining, Account, Order No, … and the activity table's are
    /// Trader, Market, Scrip, Account, Price, Order No, … so the symbol and order number are located
    /// by HEADER NAME rather than by index, which survives the portal reordering its columns.
    /// </summary>
    private async Task<OrderBookMatch?> ReadOrderBookAsync(
        string tabSelector, string panelSelector, string book, string symbol)
    {
        // Open the tab and press its own Refresh, so the grid is current rather than whatever was
        // rendered when the page loaded.
        await _page!.EvaluateFunctionAsync(
            @"(tabSel, panelSel) => {
                document.querySelector(tabSel)?.click();
                const panel = document.querySelector(panelSel);
                const refresh = panel && [...panel.querySelectorAll('button,input[type=button],a')]
                    .find(b => ((b.textContent || b.value || '').trim().toLowerCase() === 'refresh'));
                refresh?.click();
            }", tabSelector, panelSelector);

        // The grids load over AJAX, so wait for the DOM to settle rather than a fixed delay.
        await WaitForDomSettledAsync(quietMs: 500, timeoutMs: 3_000);

        var json = await _page.EvaluateFunctionAsync<string>(
            @"(panelSel, symbol) => {
                const panel = document.querySelector(panelSel);
                const table = panel && panel.querySelector('table');
                if (!table) return '';
                const rows = [...table.querySelectorAll('tr')]
                    .map(tr => [...tr.querySelectorAll('th,td')].map(c => (c.textContent || '').trim()))
                    .filter(r => r.length);
                if (rows.length < 2) return '';

                const header = rows[0].map(h => h.toLowerCase());
                const col = (...names) => {
                    for (const n of names) {
                        const i = header.findIndex(h => h === n);
                        if (i >= 0) return i;
                    }
                    return -1;
                };
                const iScrip = col('scrip', 'symbol');
                const iOrder = col('order no', 'order no.', 'orderno');
                const iPrice = col('price');
                if (iScrip < 0) return '';

                for (const r of rows.slice(1)) {
                    if ((r[iScrip] || '').trim().toUpperCase() !== symbol.toUpperCase()) continue;
                    return JSON.stringify({
                        orderNo: iOrder >= 0 ? (r[iOrder] || '') : '',
                        price:   iPrice >= 0 ? (r[iPrice] || '') : '',
                        row:     r.join(' | ')
                    });
                }
                return '';
            }", panelSelector, symbol);

        if (string.IsNullOrWhiteSpace(json)) return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var orderNo = root.TryGetProperty("orderNo", out var o) ? o.GetString() : null;

        _logger.LogInformation(
            "[AhkBroker] Verified {Symbol} in the {Book} log (order no {OrderNo}).",
            symbol, book, string.IsNullOrWhiteSpace(orderNo) ? "n/a" : orderNo);

        return new OrderBookMatch(
            book,
            string.IsNullOrWhiteSpace(orderNo) ? null : orderNo,
            root.TryGetProperty("row", out var r) ? r.GetString() ?? "" : "");
    }

    /// <summary>An order found in the account's own book, and where it was found.</summary>
    private sealed record OrderBookMatch(string Book, string? OrderNo, string Row);

    // ── Outstanding book as data ────────────────────────────────────────────────

    /// <summary>
    /// Every order currently RESTING in the outstanding book, optionally narrowed to one symbol.
    ///
    /// <para>
    /// <see cref="VerifyOrderInBookAsync"/> answers "did my order arrive?" and stops at the first
    /// match; this answers "what is resting right now?", which is a different question and the one a
    /// protective stop has to ask before placing another. The distinction matters: only the
    /// <b>outstanding</b> book is read here, never the activity log, because an order in the activity
    /// log has already been dealt with and treating it as live protection would leave a position
    /// uncovered.
    /// </para>
    ///
    /// <para>
    /// Columns are located by header name and every one of them is optional — a column the portal does
    /// not expose comes back null, meaning <b>unknown</b>. Callers must not read null as zero or as
    /// "not mine"; <see cref="ProtectiveStopDecisions"/> treats an unreadable field as ambiguity and
    /// declines to act on it.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<RestingOrder>> GetOutstandingOrdersAsync(string? symbol = null)
    {
        await _gate.WaitAsync();
        using var screen = EnterTradingScreen();
        try
        {
            _activity?.Info("Broker",
                symbol is { Length: > 0 }
                    ? $"Reading the outstanding order book for {symbol.Trim().ToUpperInvariant()}"
                    : "Reading the outstanding order book");
            await PrepareSessionWithRetryAsync();
            var resting = await ReadOutstandingOrdersAsync(symbol?.Trim().ToUpperInvariant());
            _activity?.Info("Broker", $"Order book read: {resting.Count} order(s) resting");
            return resting;
        }
        finally
        {
            await CloseAfterOperationAsync();
            _gate.Release();
        }
    }

    /// <summary>ASSUMES the gate is held and the session is prepared.</summary>
    private async Task<IReadOnlyList<RestingOrder>> ReadOutstandingOrdersAsync(string? symbol)
    {
        var cfg = _config.Current;

        await _page!.EvaluateFunctionAsync(
            @"(tabSel, panelSel) => {
                document.querySelector(tabSel)?.click();
                const panel = document.querySelector(panelSel);
                const refresh = panel && [...panel.querySelectorAll('button,input[type=button],a')]
                    .find(b => ((b.textContent || b.value || '').trim().toLowerCase() === 'refresh'));
                refresh?.click();
            }", cfg.OutstandingLogTabSelector, cfg.OutstandingLogPanelSelector);

        await WaitForDomSettledAsync(quietMs: 500, timeoutMs: 3_000);

        var json = await _page.EvaluateFunctionAsync<string>(
            @"(panelSel) => {
                const panel = document.querySelector(panelSel);
                const table = panel && panel.querySelector('table');
                if (!table) return '[]';
                const rows = [...table.querySelectorAll('tr')]
                    .map(tr => [...tr.querySelectorAll('th,td')].map(c => (c.textContent || '').trim()))
                    .filter(r => r.length);
                if (rows.length < 2) return '[]';

                const header = rows[0].map(h => h.toLowerCase());
                const col = (...names) => {
                    for (const n of names) {
                        const i = header.findIndex(h => h === n);
                        if (i >= 0) return i;
                    }
                    return -1;
                };
                const iScrip = col('scrip', 'symbol');
                const iSide  = col('side', 'type', 'buy/sell', 'order side', 'trade type');
                const iKind  = col('order type', 'ordertype');
                const iQty   = col('remaining', 'quantity', 'qty', 'volume');
                const iPrice = col('price', 'rate');
                const iOrder = col('order no', 'order no.', 'orderno');
                if (iScrip < 0) return '[]';

                const at = (r, i) => (i >= 0 ? (r[i] || '').trim() : '');
                return JSON.stringify(rows.slice(1)
                    .filter(r => at(r, iScrip).length > 0)
                    .map(r => ({
                        symbol:  at(r, iScrip).toUpperCase(),
                        side:    at(r, iSide),
                        kind:    at(r, iKind),
                        qty:     at(r, iQty),
                        price:   at(r, iPrice),
                        orderNo: at(r, iOrder),
                        row:     r.join(' | ')
                    })));
            }", cfg.OutstandingLogPanelSelector);

        var orders = new List<RestingOrder>();
        if (string.IsNullOrWhiteSpace(json)) return orders;

        using var doc = JsonDocument.Parse(json);
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var rowSymbol = Text(element, "symbol");
            if (rowSymbol is null) continue;
            if (symbol is not null && !rowSymbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                continue;

            orders.Add(new RestingOrder(
                rowSymbol,
                Text(element, "side"),
                Text(element, "kind"),
                ParseQuantity(Text(element, "qty")),
                ParsePrice(Text(element, "price")),
                Text(element, "orderNo"),
                Text(element, "row") ?? ""));
        }

        _logger.LogInformation(
            "[AhkBroker] Outstanding book: {Count} resting order(s){Filter}.",
            orders.Count, symbol is null ? "" : $" for {symbol}");
        return orders;

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value)
            && value.GetString() is { Length: > 0 } text ? text : null;

        static int? ParseQuantity(string? raw) =>
            int.TryParse((raw ?? "").Replace(",", "").Trim(),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;

        static decimal? ParsePrice(string? raw) =>
            decimal.TryParse((raw ?? "").Replace(",", "").Trim(),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
    }

    /// <summary>
    /// Reads the portal's tradable price band for the symbol currently entered in the order dialog.
    ///
    /// <para>
    /// This is the same pair the portal's own submit handler gates on: it refuses to send when the
    /// price falls outside <c>lowerCap..upperCap</c>, and it does so with a modal alert rather than an
    /// error — a silent no-op that looks like success unless we check first.
    /// </para>
    /// </summary>
    private async Task<(decimal Lower, decimal Upper)?> ReadPriceBandAsync()
    {
        try
        {
            var raw = await _page!.EvaluateFunctionAsync<string>(
                "() => `${window.lowerCap ?? 0}|${window.upperCap ?? 0}`");
            var parts = (raw ?? "").Split('|');
            if (parts.Length == 2
                && decimal.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var lower)
                && decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var upper))
                return (lower, upper);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[AhkBroker] Price band could not be read.");
        }
        return null;
    }

    /// <summary>
    /// Waits for the portal's order-type change handler to enable the stop-limit field. Selecting
    /// "Stop Loss" is what enables it, so this is a deterministic readiness signal rather than a
    /// guess at how long the handler takes — which is what makes the flow behave the same on a slow
    /// machine as on a fast one.
    /// </summary>
    private async Task WaitForLimitPriceEnabledAsync(string side)
    {
        var selector = side == "sell" ? "#selllimitprice" : "#buylimitprice";
        var timeout  = Math.Max(1_000, _config.Current.DialogOpenTimeoutMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);

        while (DateTime.UtcNow < deadline)
        {
            var enabled = await _page!.EvaluateFunctionAsync<bool>(
                "(sel) => { const e = document.querySelector(sel); return !!e && !e.disabled; }", selector);
            if (enabled) return;
            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"{selector} never became editable after selecting the Stop Loss order type. The portal "
            + "enables it from its order-type change handler, so this means the type did not actually "
            + "change — submitting now would send an ordinary order with no stop.");
    }

    /// <summary>
    /// Clears a single-line field with real key events, then types the value. End→Shift+Home selects
    /// the whole current value and Backspace deletes it; doing this via key events (rather than
    /// keyboard Ctrl+A, which the portal's autocomplete/number fields often ignore) reliably empties
    /// the field so TypeAsync replaces rather than appends.
    /// </summary>
    private async Task ClearAndTypeAsync(IElementHandle el, string selector, string value)
    {
        // Focus via the DOM first: a coordinate click can land on the wrong element while the modal is
        // still fading in, which silently sends the keystrokes somewhere else. Click only as a fallback
        // for a field that ignores programmatic focus.
        try { await el.FocusAsync(); } catch { /* fall back to the click below */ }
        if (!await el.EvaluateFunctionAsync<bool>("e => document.activeElement === e"))
            await el.ClickAsync();

        await _page!.Keyboard.PressAsync("End");      // caret to end
        await _page.Keyboard.DownAsync("Shift");
        await _page.Keyboard.PressAsync("Home");      // select the entire value
        await _page.Keyboard.UpAsync("Shift");
        await _page.Keyboard.PressAsync("Backspace"); // delete the selection (no-op if already empty)
        await el.TypeAsync(value);
    }

    /// <summary>
    /// Opens the BUY/SELL order dialog by clicking its toolbar button (#buyorder / #sellorder) and
    /// waits for the symbol field to become visible. No-ops if the dialog is already open. Required
    /// when the session is restored already-logged-in: nothing else opens the modal, so its fields
    /// stay hidden and uninteractable.
    ///
    /// Slow machines break this step in two distinct ways, so both are handled:
    ///   • the toolbar button itself is not rendered yet — clicking blindly throws "No node found";
    ///   • the click lands before the portal binds its click handler — the click is simply swallowed
    ///     and nothing ever opens, which used to fail the order after a flat 5s wait.
    /// The open click is therefore RETRIED until the dialog is visible or DialogOpenTimeoutMs expires.
    /// Retrying is safe here: opening a dialog places nothing (unlike submit, which runs exactly once).
    /// </summary>
    private async Task OpenOrderDialogAsync(string side)
    {
        var cfg     = _config.Current;
        var openBtn = side == "buy" ? "#buyorder"  : "#sellorder";
        var field   = side == "buy" ? "#buysymbol" : "#sellsymbol";
        var timeout = Math.Max(2_000, cfg.DialogOpenTimeoutMs);

        if (await IsVisibleAsync(field)) return; // already open

        if (!await WaitForVisibleAsync(openBtn, timeout))
        {
            await DumpOrderFormAsync($"no_{side}_toolbar");
            throw new InvalidOperationException(
                $"The {side.ToUpperInvariant()} toolbar button '{openBtn}' never became visible within " +
                $"{timeout}ms — the trading screen did not finish loading. See dumped order_no_{side}_toolbar.html, " +
                "or raise Ahk.DialogOpenTimeoutMs.");
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        for (var attempt = 1; ; attempt++)
        {
            try { await _page!.ClickAsync(openBtn); }
            catch (Exception ex)
            {
                // The button can be re-rendered between the visibility check and the click.
                _logger.LogDebug(ex, "[AhkBroker] Click on '{Selector}' failed (attempt {Attempt}) — retrying.",
                    openBtn, attempt);
            }

            var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (await WaitForVisibleAsync(field, Math.Clamp(remaining, 500, 3_000)))
            {
                if (attempt > 1)
                    _logger.LogWarning(
                        "[AhkBroker] {Side} dialog opened only on attempt {Attempt} — the portal was slow to bind its handler.",
                        side.ToUpperInvariant(), attempt);

                // The modal fades in; give it a beat to reach its final position so the field clicks
                // below land on the field rather than on whatever was under the moving element.
                await Task.Delay(250);

                // Once per session, record the dialog's select options — the BUY/SELL order-type
                // value-vs-text asymmetry documented in LogOrderFormOptionsAsync is invisible anywhere
                // else, and this is the only moment the selects exist in the DOM.
                await LogOrderFormOptionsAsync(side);
                return;
            }

            if (DateTime.UtcNow >= deadline) break;
        }

        await DumpOrderFormAsync($"no_{side}_dialog");
        throw new InvalidOperationException(
            $"Could not open the {side.ToUpperInvariant()} order dialog ({openBtn} → {field} not visible " +
            $"within {timeout}ms). See dumped order_no_{side}_dialog.html, or raise Ahk.DialogOpenTimeoutMs.");
    }

    /// <summary>True if the element exists and is rendered (offsetParent set, i.e. not display:none / hidden modal).</summary>
    private async Task<bool> IsVisibleAsync(string selector)
    {
        try
        {
            return await _page!.EvaluateFunctionAsync<bool>(
                "(sel) => { const el = document.querySelector(sel); return !!el && el.offsetParent !== null; }",
                selector);
        }
        catch { return false; }
    }

    /// <summary>
    /// Bounded wait until the selector matches a RENDERED element. Unlike
    /// <see cref="WaitForExistsAsync"/> this requires visibility, which is what makes an element
    /// clickable/typeable — the distinction that matters while a modal is still opening.
    /// </summary>
    private async Task<bool> WaitForVisibleAsync(string selector, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(selector)) return false;
        try
        {
            await _page!.WaitForSelectorAsync(selector,
                new WaitForSelectorOptions { Visible = true, Timeout = Math.Max(250, timeoutMs) });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Click the order submit button. The AHK portal's submit button has NO id — its only reliable
    /// marker is its exact visible text ("BUY"/"SELL"). A substring match is unsafe: the toolbar
    /// buttons "Buy Order" / "LB Buy Order" also contain "Buy" and merely re-open the dialog, so a
    /// loose match clicks the wrong button and the order never submits. We therefore match the text
    /// EXACTLY. A configured selector (Ahk.BuySubmitSelector / SellSubmitSelector) overrides this.
    ///
    /// The button is polled for up to SubmitButtonTimeoutMs: while the modal is still rendering it is
    /// either absent or disabled, and a single-shot lookup on a slow machine failed the order with
    /// "submit button not found". This polls the LOOKUP only — the moment a click lands we return, so
    /// the exactly-once submit guarantee is unchanged.
    /// </summary>
    private async Task ClickSubmitAsync(string side)
    {
        var cfg        = _config.Current;
        var configured = side == "buy" ? cfg.BuySubmitSelector : cfg.SellSubmitSelector;
        var timeout    = Math.Max(1_000, cfg.SubmitButtonTimeoutMs);
        var word       = side == "buy" ? "BUY" : "SELL";

        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!await WaitForVisibleAsync(configured, timeout))
            {
                await DumpOrderFormAsync($"no_{side}_submit");
                throw new InvalidOperationException(
                    $"Configured {word} submit selector '{configured}' never became visible within {timeout}ms. " +
                    $"See dumped order_no_{side}_submit.html, or raise Ahk.SubmitButtonTimeoutMs.");
            }
            await _page!.ClickAsync(configured);
            return;
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (true)
        {
            var clicked = await _page!.EvaluateFunctionAsync<bool>(@"(word) => {
                const btns = Array.from(document.querySelectorAll(
                    ""button, input[type='submit'], input[type='button']""));
                const visible = e => e.offsetParent !== null && !e.disabled;
                const btn = btns.find(b => visible(b) &&
                    (b.textContent || b.value || '').trim().toUpperCase() === word);
                if (btn) { btn.click(); return true; }
                return false;
            }", word);

            if (clicked) return;
            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(150);
        }

        await DumpOrderFormAsync($"no_{side}_submit");
        throw new InvalidOperationException(
            $"{word} submit button not found within {timeout}ms (no visible, enabled button with exact text '{word}'). " +
            $"See dumped order_no_{side}_submit.html, set Ahk.{(side == "buy" ? "Buy" : "Sell")}SubmitSelector, " +
            "or raise Ahk.SubmitButtonTimeoutMs.");
    }

    /// <summary>
    /// After the submit click the portal shows a SweetAlert2 confirmation prompt
    /// ("Are you sure? You want to execute Buy/Sell order!") with Cancel / OK buttons. The order does
    /// NOT execute until OK is pressed, so we wait for that prompt and click OK exactly once. A
    /// confirmation is told apart from a result popup by having a VISIBLE Cancel button. Returns whether
    /// the prompt was confirmed; false if none appeared (so the EXACTLY-ONCE submit guarantee is
    /// preserved — we never re-click submit). A prompt that is merely LATE is picked up afterwards by
    /// <see cref="ReadOrderOutcomeAsync"/>, which keeps watching for it while polling for the verdict.
    /// </summary>
    private async Task<bool> ConfirmOrderAsync(string side)
    {
        var timeout  = Math.Max(1_000, _config.Current.ConfirmPromptTimeoutMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (true)
        {
            if (await TryClickConfirmPromptAsync())
            {
                _logger.LogInformation("[AhkBroker] Confirmed {Side} execution (clicked OK on the 'Are you sure?' prompt).", side);
                return true;
            }

            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(200);
        }

        _logger.LogInformation(
            "[AhkBroker] No {Side} confirmation prompt appeared within {Timeout}ms — still watching for a late one while reading the outcome.",
            side, timeout);
        return false;
    }

    /// <summary>
    /// Clicks the confirm button of a visible "Are you sure?" prompt, returning whether one was found.
    /// The portal uses the legacy "sweetalert" library (swal-* classes); a confirmation prompt has a
    /// visible cancel button alongside the confirm button, which is what distinguishes it from a result
    /// popup. A text-based match (OK/Yes next to Cancel) is kept as a fallback for any other dialog.
    /// </summary>
    private async Task<bool> TryClickConfirmPromptAsync()
    {
        try
        {
            return await _page!.EvaluateFunctionAsync<bool>(@"() => {
                const visible = e => e && e.offsetParent !== null;
                const swalConfirm = document.querySelector('.swal-button--confirm');
                const swalCancel  = document.querySelector('.swal-button--cancel');
                if (visible(swalConfirm) && visible(swalCancel)) { swalConfirm.click(); return true; }

                const label = e => (e.textContent || e.value || '').trim().toLowerCase();
                const btns = Array.from(document.querySelectorAll(
                    ""button, input[type='button'], input[type='submit'], a[role='button']"")).filter(visible);
                const hasCancel = btns.some(b => label(b) === 'cancel');
                const ok = btns.find(b => label(b) === 'ok' || label(b) === 'yes');
                if (hasCancel && ok) { ok.click(); return true; }
                return false;
            }");
        }
        catch { return false; }
    }

    /// <summary>
    /// Sets a &lt;select&gt; to the option whose visible text matches (case-insensitive) and fires a
    /// change event so the portal's handlers run. Logs a warning rather than throwing if the option
    /// or element is missing, so a dropdown the portal already defaults correctly never blocks an order.
    /// </summary>
    private async Task SelectByVisibleTextAsync(string selector, string visibleText)
    {
        var ok = await _page!.EvaluateFunctionAsync<bool>(@"(sel, text) => {
            const el = document.querySelector(sel);
            if (!el || !el.options) return false;
            const opt = Array.from(el.options).find(o =>
                (o.textContent || '').trim().toLowerCase() === text.toLowerCase());
            if (!opt) return false;
            el.value = opt.value;
            el.dispatchEvent(new Event('change', { bubbles: true }));
            return true;
        }", selector, visibleText);

        if (!ok)
            _logger.LogWarning("[AhkBroker] Could not set dropdown {Selector} to '{Text}' (leaving portal default).",
                selector, visibleText);
    }

    /// <summary>Saves a screenshot and the order dialog HTML to LogDir to make selector debugging concrete.</summary>
    private async Task DumpOrderFormAsync(string tag)
    {
        try
        {
            await ScreenshotAsync($"order_{tag}");
            var html = await _page!.EvaluateFunctionAsync<string>("() => document.body.innerHTML");
            var path = Path.Combine(ResolvePath(_config.Current.LogDir),
                $"order_{tag}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.html");
            await File.WriteAllTextAsync(path, html);
            _logger.LogWarning("[AhkBroker] Dumped order form HTML to {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AhkBroker] Could not dump order form.");
        }
    }

    // Outcome detection. NOTE: these markers are best-effort and should be tuned to the live AHK
    // portal's exact wording. The design is fail-safe: errors take precedence and are matched
    // broadly, success requires a specific phrase, and anything ambiguous is reported as
    // unconfirmed (Success=false) rather than a silent success.
    // NOTE: these must be DISTINCTIVE phrases, never words that are permanent page furniture. The AHK
    // trading grid always shows column headers "Lower Lock" / "Upper Cap", so those words can't be
    // markers — they'd flag every order as an error. Validation popups use specific wording instead
    // ("Price should be between Upper and Lower lock", "Volume should be…", etc.).
    private static readonly string[] _errorMarkers =
    [
        "error", "invalid", "insufficient", "failed", "incorrect", "rejected",
        "not allowed", "exceeds", "market is closed", "session expired", "try again",
        "should be between", "price should", "volume should", "quantity should",
        "out of range", "not in range", "not enough", "cannot be"
    ];

    private static readonly string[] _successMarkers =
    [
        "order placed", "order submitted successfully", "successfully submitted",
        "order confirmation", "order accepted", "order booked", "order number", "order no."
    ];

    private static readonly Regex _orderIdRegex = new(
        @"order\s*(?:id|no\.?|number)\s*[:#]?\s*([A-Za-z0-9\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record OrderOutcome(bool Success, string? Message, string? OrderId);

    /// <summary>
    /// Polls the page text after submit for an explicit confirmation or error. Does NOT assume
    /// success — if neither appears within the timeout the order is reported unconfirmed so a
    /// human verifies via the screenshots instead of trusting a fake success.
    ///
    /// <paramref name="confirmed"/> says whether the "Are you sure?" prompt was already answered. When
    /// it was not, this loop keeps watching for it: on a slow machine the prompt can render after
    /// ConfirmPromptTimeoutMs, and an unanswered prompt means the order NEVER executes and its modal
    /// blocks the next one. It is clicked at most once here — submit itself is never re-clicked.
    /// </summary>
    private async Task<OrderOutcome> ReadOrderOutcomeAsync(int timeoutMs, bool confirmed)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1_000, timeoutMs));
        var lastText = "";

        while (DateTime.UtcNow < deadline)
        {
            // 0. A confirmation prompt that arrived after ConfirmOrderAsync gave up. Answer it (once)
            //    and keep polling for the verdict it produces.
            if (!confirmed && await TryClickConfirmPromptAsync())
            {
                confirmed = true;
                _logger.LogWarning(
                    "[AhkBroker] The confirmation prompt appeared late — confirmed while reading the outcome. " +
                    "Consider raising Ahk.ConfirmPromptTimeoutMs on this machine.");
                await Task.Delay(200);
                continue;
            }

            // 1. A result popup (SweetAlert2-style: red ✗ / green ✓ with a message and an OK button)
            //    is the portal's explicit verdict. Read and classify it, then dismiss it so a leftover
            //    modal can't block the next order.
            var popup = await ReadResultPopupAsync();
            if (popup is not null)
            {
                await DismissPopupAsync();
                return popup;
            }

            // 2. Fall back to scanning the page text for a distinctive confirmation/rejection phrase.
            try
            {
                lastText = await _page!.EvaluateFunctionAsync<string>(
                    "() => (document.body && document.body.innerText) ? document.body.innerText : ''") ?? "";
            }
            catch { /* page mid-navigation — retry */ }

            var lower = lastText.ToLowerInvariant();

            var err = _errorMarkers.FirstOrDefault(m => lower.Contains(m));
            if (err is not null)
            {
                await DismissPopupAsync();
                return new OrderOutcome(false, $"Order rejected: {ExtractLine(lastText, err)}", null);
            }

            var ok = _successMarkers.FirstOrDefault(m => lower.Contains(m));
            if (ok is not null)
            {
                var match = _orderIdRegex.Match(lastText);
                var id    = match.Success ? match.Groups[1].Value : null;
                await DismissPopupAsync();
                return new OrderOutcome(true, $"Order confirmed: {ExtractLine(lastText, ok)}", id);
            }

            await Task.Delay(400);
        }

        return new OrderOutcome(false,
            confirmed
                ? "Order submitted but no confirmation or error was detected within the timeout. " +
                  "Verify manually (see screenshots)."
                : "Order submitted but the portal's 'Are you sure?' prompt never appeared, so the order was " +
                  "most likely NOT executed. Verify manually (see screenshots) and raise Ahk.ConfirmPromptTimeoutMs " +
                  "/ Ahk.OrderConfirmTimeoutMs if this machine is slow.", null);
    }

    /// <summary>
    /// Reads a legacy-sweetalert (swal-*) result popup if one is visible, classifying it by its icon
    /// (error/warning/success) and returning its title+message. Returns null when no popup is shown,
    /// so the caller falls back to scanning the page text. If the portal swaps dialog libraries this
    /// no-ops harmlessly (no false positive) and the page-text keyword scan still catches the verdict.
    /// </summary>
    private async Task<OrderOutcome?> ReadResultPopupAsync()
    {
        string raw;
        try
        {
            raw = await _page!.EvaluateFunctionAsync<string>(@"() => {
                const pop = document.querySelector('.swal-modal');
                if (!pop || pop.offsetParent === null) return '';
                // Skip a confirmation prompt (it has a visible Cancel button) — that is handled by
                // ConfirmOrderAsync, not an order verdict. Only single-OK popups are results.
                const cancel = pop.querySelector('.swal-button--cancel');
                if (cancel && cancel.offsetParent !== null) return '';
                const title = (pop.querySelector('.swal-title') || {}).innerText || '';
                const body  = (pop.querySelector('.swal-text')  || {}).innerText || '';
                const msg = (title + ' ' + body).replace(/\s+/g, ' ').trim();
                let kind = 'POPUP';
                if (pop.querySelector('.swal-icon--error, .swal-icon--warning')) kind = 'ERROR';
                else if (pop.querySelector('.swal-icon--success'))              kind = 'OK';
                // Every class on every icon element, so an unrecognised popup can be identified
                // from the log instead of needing to be reproduced against a live market.
                const icons = Array.from(pop.querySelectorAll('[class*=swal-icon]'))
                    .map(e => e.className).join(' ') || '(none)';
                return kind + '::' + icons + '::' + msg;
            }") ?? "";
        }
        catch { return null; }

        var parts = raw.Split("::", 3);
        if (parts.Length < 3) return null;

        var kind  = parts[0];
        var icons = parts[1].Trim();
        var msg   = parts[2].Trim();
        if (msg.Length > 200) msg = msg[..200];

        if (kind == "OK")
        {
            return new OrderOutcome(true, $"Order confirmed: {msg}",
                _orderIdRegex.Match(msg) is { Success: true } m ? m.Groups[1].Value : null);
        }

        if (kind == "ERROR")
            return new OrderOutcome(false, $"Order rejected: {msg}", null);

        // Unclassified: a popup appeared but carried no icon this code recognises.
        //
        // The verdict stays NOT-PLACED, and deliberately so even when the text says "success".
        // VerifyOrderInBook documents the portal returning a green success alert while placing
        // nothing at all, so believing the words would be exactly the false positive that costs
        // money; the outstanding book remains the only evidence. What was missing was any way to
        // find out WHAT the popup was — the message alone ("success") does not say why it went
        // unrecognised. Dumping the icon classes and the page makes the next occurrence diagnosable
        // instead of requiring it to be reproduced against a live market.
        _logger.LogWarning(
            "[AhkBroker] Unclassified order popup. icons=[{Icons}] text=[{Message}]. Treating the "
            + "order as NOT placed; the outstanding book is the deciding evidence.", icons, msg);
        _activity?.Warn("Orders", "Unrecognised order popup", $"icons=[{icons}] text=[{msg}]");
        await DumpOrderPopupAsync();

        return new OrderOutcome(false,
            $"Order returned an unclassified popup (icons: {icons}): {msg}", null);
    }

    /// <summary>
    /// Clicks the OK button of a single-OK result popup so it can't block the next order. A popup with
    /// a visible Cancel button is a confirmation prompt and is left for ConfirmOrderAsync. Best-effort.
    /// </summary>
    private async Task DismissPopupAsync()
    {
        try
        {
            await _page!.EvaluateFunctionAsync(@"() => {
                const visible = e => e && e.offsetParent !== null;
                // Legacy sweetalert: leave confirmation prompts (with a cancel button) for ConfirmOrderAsync.
                const swalCancel  = document.querySelector('.swal-button--cancel');
                if (visible(swalCancel)) return;
                const swalConfirm = document.querySelector('.swal-button--confirm');
                if (visible(swalConfirm)) { swalConfirm.click(); return; }

                const label = e => (e.textContent || e.value || '').trim().toLowerCase();
                const btns = Array.from(document.querySelectorAll(
                    ""button, input[type='button'], input[type='submit'], a[role='button']"")).filter(visible);
                if (btns.some(b => label(b) === 'cancel')) return; // confirmation prompt — not ours to dismiss
                const ok = btns.find(b => label(b) === 'ok');
                if (ok) ok.click();
            }");
        }
        catch { /* best effort */ }
    }

    /// <summary>Returns the first text line containing <paramref name="marker"/>, trimmed and length-capped.</summary>
    private static string ExtractLine(string text, string marker)
    {
        var line = text
            .Split('\n')
            .FirstOrDefault(l => l.Contains(marker, StringComparison.OrdinalIgnoreCase))
            ?.Trim() ?? marker;

        return line.Length > 200 ? line[..200] : line;
    }

    /// <summary>Resolves a possibly-relative configured path to an absolute one under the workspace root.</summary>
    private string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(_workspaceRoot, path);

    private async Task<string> ScreenshotAsync(string prefix)
    {
        var path = Path.Combine(
            ResolvePath(_config.Current.LogDir),
            $"{prefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
        try
        {
            var bytes = await _page!.ScreenshotDataAsync(new ScreenshotOptions { Type = ScreenshotType.Png });
            await File.WriteAllBytesAsync(path, bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AhkBroker] Screenshot failed: {Path}", path);
        }
        return path;
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        // TeardownAsync also kills the OS process tree, so we don't leave a Chrome holding the
        // profile lock after shutdown. The PID file is cleared on the next KillPreviousBrowser pass.
        await TeardownAsync();
        _gate.Dispose();
    }
}

public sealed record AhkLoginVerificationResult(
    bool Authenticated,
    string CurrentUrl,
    string VerifiedSelector,
    DateTime CheckedUtc);
