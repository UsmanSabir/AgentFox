using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuppeteerSharp;
using TradingAgent.Config;
using TradingAgent.Models;

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
/// Submit button: ID not yet confirmed — inspect with:
///   document.querySelectorAll('button')
///     .forEach(b => console.log(b.id, b.className, b.textContent.trim()))
/// Current attempts: #buySubmitBtn, #sellSubmitBtn, then text-content fallback via JS.
/// </summary>
public sealed class AhkBroker : IAsyncDisposable
{
    private readonly IOptions<AhkConfig> _config;
    private readonly ILogger<AhkBroker> _logger;
    private readonly string _workspaceRoot;

    // Serializes every browser interaction. The host can process channel messages concurrently,
    // but there is a single shared _page — concurrent orders would interleave field-fills and
    // submits and corrupt each other. All public entry points take this gate.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IBrowser? _browser;
    private IPage? _page;
    private bool _initialized;

    public AhkBroker(IOptions<AhkConfig> config, IConfiguration configuration, ILogger<AhkBroker> logger)
    {
        _config = config;
        _logger = logger;
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
    /// (Re)launches the browser if needed. ASSUMES the caller holds <see cref="_gate"/> — it must
    /// never take the gate itself (it is also called from the order path which already holds it).
    /// Any stray browser this broker left running on a previous run is killed first, and stale
    /// profile lock files are removed, so "session already running / profile in use" can never
    /// silently block an order.
    /// </summary>
    private async Task EnsureBrowserAsync(bool forceRestart, CancellationToken ct = default)
    {
        if (!forceRestart && IsHealthy()) return;

        var cfg        = _config.Value;
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

        WritePidFile(sessionDir, _browser.Process?.Id);
        _initialized = true;
        _logger.LogInformation(
            "[AhkBroker] Browser session ready. Headless={Headless} Profile={Dir}",
            cfg.Headless, sessionDir);
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

    public async Task<OrderResult> PlaceOrderAsync(TradingSignal signal)
    {
        await _gate.WaitAsync();
        try
        {
            // Readiness — launch + login — is safe to retry, so a dead/locked/stray browser is
            // restarted rather than failing the order. The submit below runs EXACTLY ONCE and is
            // never auto-retried, to avoid double execution if a browser error happens after submit.
            await PrepareSessionWithRetryAsync();

            return signal.Action.ToUpperInvariant() switch
            {
                "BUY"  => await PlaceBuyAsync(signal),
                "SELL" => await PlaceSellAsync(signal),
                _      => new OrderResult
                {
                    Success = false,
                    Message = $"Unsupported action '{signal.Action}'. Only BUY and SELL are supported."
                }
            };
        }
        finally
        {
            _gate.Release();
        }
    }

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
                    await LoginAsync();

                return; // ready
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "[AhkBroker] Session not ready (attempt {Attempt}/{Max}) — restarting browser and retrying.",
                    attempt, maxAttempts);
            }
        }
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    private async Task<bool> IsLoggedInAsync()
    {
        try
        {
            return await _page!.QuerySelectorAsync(_config.Value.LoggedInSelector) is not null;
        }
        catch { return false; }
    }

    private async Task LoginAsync()
    {
        var cfg = _config.Value;
        _logger.LogInformation("[AhkBroker] Logging in to {Url}", cfg.PortalUrl);

        await _page!.GoToAsync(cfg.PortalUrl, WaitUntilNavigation.Networkidle0);
        _logger.LogInformation("[AhkBroker] Login page loaded: {Url}", _page.Url);

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
                new WaitForSelectorOptions { Timeout = 15_000 });
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
            var path = Path.Combine(ResolvePath(_config.Value.LogDir),
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
        var cfg     = _config.Value;
        var qty     = signal.Quantity ?? cfg.DefaultQty;
        var isLimit = !signal.OrderType.Equals("MARKET", StringComparison.OrdinalIgnoreCase);

        _logger.LogInformation("[AhkBroker] BUY {Symbol} x{Qty} @ {Price} ({Type})",
            signal.Symbol, qty, signal.EntryPrice, signal.OrderType);

        await FillFieldAsync("#buysymbol", signal.Symbol);
        await Task.Delay(800); // wait for autocomplete dropdown
        await _page!.Keyboard.PressAsync("Tab");

        await FillFieldAsync("#buyvolume", qty.ToString());

        if (isLimit && signal.EntryPrice.HasValue)
        {
            var price = signal.EntryPrice.Value.ToString("F2");
            await FillFieldAsync("#buyprice",      price);
            await FillFieldAsync("#buylimitprice", price);
        }

        await FillFieldAsync("#buyPIN", cfg.TradingPin);

        var before = await ScreenshotAsync("pre_buy");

        await ClickSubmitAsync("buy");

        var outcome = await ReadOrderOutcomeAsync(cfg.OrderConfirmTimeoutMs);
        var after   = await ScreenshotAsync("post_buy");

        return new OrderResult
        {
            Success          = outcome.Success,
            OrderId          = outcome.OrderId,
            Action           = "BUY",
            Symbol           = signal.Symbol,
            Message          = outcome.Message ?? $"BUY {signal.Symbol} x{qty}: outcome unconfirmed.",
            ScreenshotBefore = before,
            ScreenshotAfter  = after
        };
    }

    // ── SELL ──────────────────────────────────────────────────────────────────

    private async Task<OrderResult> PlaceSellAsync(TradingSignal signal)
    {
        var cfg     = _config.Value;
        var qty     = signal.Quantity ?? cfg.DefaultQty;
        var isLimit = !signal.OrderType.Equals("MARKET", StringComparison.OrdinalIgnoreCase);

        _logger.LogInformation("[AhkBroker] SELL {Symbol} x{Qty} @ {Price} ({Type})",
            signal.Symbol, qty, signal.EntryPrice, signal.OrderType);

        await FillFieldAsync("#sellsymbol", signal.Symbol);
        await Task.Delay(800);
        await _page!.Keyboard.PressAsync("Tab");

        await FillFieldAsync("#sellvolume", qty.ToString());

        if (isLimit && signal.EntryPrice.HasValue)
        {
            var price = signal.EntryPrice.Value.ToString("F2");
            await FillFieldAsync("#sellprice",      price);
            await FillFieldAsync("#selllimitprice", price);
        }

        await FillFieldAsync("#sellPIN", cfg.TradingPin);

        var before = await ScreenshotAsync("pre_sell");

        await ClickSubmitAsync("sell");

        var outcome = await ReadOrderOutcomeAsync(cfg.OrderConfirmTimeoutMs);
        var after   = await ScreenshotAsync("post_sell");

        return new OrderResult
        {
            Success          = outcome.Success,
            OrderId          = outcome.OrderId,
            Action           = "SELL",
            Symbol           = signal.Symbol,
            Message          = outcome.Message ?? $"SELL {signal.Symbol} x{qty}: outcome unconfirmed.",
            ScreenshotBefore = before,
            ScreenshotAfter  = after
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Ctrl+A to select all existing text, then type the new value.
    /// Safer than TypeAsync alone (which appends) or raw JS value injection (which skips events).
    /// </summary>
    private async Task FillFieldAsync(string selector, string value)
    {
        await _page!.FocusAsync(selector);
        await _page.Keyboard.DownAsync("Control");
        await _page.Keyboard.PressAsync("KeyA");
        await _page.Keyboard.UpAsync("Control");
        await _page.TypeAsync(selector, value);
    }

    /// <summary>
    /// Click the submit button. Tries the confirmed ID first, then falls back to a
    /// JS text-content search. Update the confirmed ID after inspecting the live portal.
    /// </summary>
    private async Task ClickSubmitAsync(string side)
    {
        var confirmedId = side == "buy" ? "#buySubmitBtn" : "#sellSubmitBtn";
        var sideText    = side == "buy" ? "Buy"          : "Sell";

        try
        {
            await _page!.ClickAsync(confirmedId);
        }
        catch
        {
            // Fallback: find any button whose visible text matches the side
            await _page!.EvaluateFunctionAsync($@"() => {{
                const buttons = Array.from(
                    document.querySelectorAll('button, input[type=""submit""], input[type=""button""]'));
                const btn = buttons.find(b =>
                    /{sideText}/i.test(b.textContent || b.value || ''));
                if (btn) btn.click();
            }}");
        }
    }

    // Outcome detection. NOTE: these markers are best-effort and should be tuned to the live AHK
    // portal's exact wording. The design is fail-safe: errors take precedence and are matched
    // broadly, success requires a specific phrase, and anything ambiguous is reported as
    // unconfirmed (Success=false) rather than a silent success.
    private static readonly string[] _errorMarkers =
    [
        "error", "invalid", "insufficient", "failed", "incorrect", "rejected",
        "not allowed", "exceeds", "market is closed", "session expired", "try again"
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
    /// </summary>
    private async Task<OrderOutcome> ReadOrderOutcomeAsync(int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1_000, timeoutMs));
        var lastText = "";

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                lastText = await _page!.EvaluateFunctionAsync<string>(
                    "() => (document.body && document.body.innerText) ? document.body.innerText : ''") ?? "";
            }
            catch { /* page mid-navigation — retry */ }

            var lower = lastText.ToLowerInvariant();

            var err = _errorMarkers.FirstOrDefault(m => lower.Contains(m));
            if (err is not null)
                return new OrderOutcome(false, $"Order rejected: {ExtractLine(lastText, err)}", null);

            var ok = _successMarkers.FirstOrDefault(m => lower.Contains(m));
            if (ok is not null)
            {
                var match = _orderIdRegex.Match(lastText);
                var id    = match.Success ? match.Groups[1].Value : null;
                return new OrderOutcome(true, $"Order confirmed: {ExtractLine(lastText, ok)}", id);
            }

            await Task.Delay(400);
        }

        return new OrderOutcome(false,
            "Order submitted but no confirmation or error was detected within the timeout. " +
            "Verify manually (see screenshots).", null);
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
            ResolvePath(_config.Value.LogDir),
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
