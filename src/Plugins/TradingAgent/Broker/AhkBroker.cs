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

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _gate.WaitAsync(ct);
        try
        {
            if (_initialized) return; // double-checked under the gate

            var cfg = _config.Value;

            // Chrome resolves a relative UserDataDir against its OWN working directory (the
            // chrome.exe location), not ours — which it cannot write to, hence "cannot read and
            // write to its data directory: session_ahk". Always hand Chrome an absolute path.
            var sessionDir = ResolvePath(cfg.SessionDir);
            Directory.CreateDirectory(sessionDir);
            Directory.CreateDirectory(ResolvePath(cfg.LogDir));

            var executablePath = await ResolveBrowserPathAsync(cfg, ct);

            var launchOptions = new LaunchOptions
            {
                Headless       = cfg.Headless,
                ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath,
                UserDataDir    = sessionDir,
                Args           = ["--no-sandbox", "--disable-setuid-sandbox"]
            };

            _browser = await Puppeteer.LaunchAsync(launchOptions);

            var pages = await _browser.PagesAsync();
            _page = pages.Length > 0 ? pages[0] : await _browser.NewPageAsync();

            _initialized = true;
            _logger.LogInformation(
                "[AhkBroker] Browser session ready. Headless={Headless} Profile={Dir}",
                cfg.Headless, cfg.SessionDir);
        }
        finally
        {
            _gate.Release();
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
        // Ensure the browser is up before placing the order. InitializeAsync is idempotent and
        // self-gated, so this is a no-op once initialized. Calling it here (BEFORE acquiring _gate,
        // which is non-reentrant) means an order never depends on the fire-and-forget warm-up in
        // OnAgentReadyAsync having finished, and a genuine init failure surfaces its real cause
        // instead of a misleading "not initialized".
        await InitializeAsync();

        await _gate.WaitAsync();
        try
        {
            if (!_initialized || _page is null)
                throw new InvalidOperationException(
                    "AhkBroker initialization did not complete (browser unavailable).");

            if (!await IsLoggedInAsync())
                await LoginAsync();

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

    // ── Login ─────────────────────────────────────────────────────────────────

    private async Task<bool> IsLoggedInAsync()
    {
        try
        {
            return await _page!.EvaluateFunctionAsync<bool>(
                "() => document.querySelector('#buysymbol') !== null");
        }
        catch { return false; }
    }

    private async Task LoginAsync()
    {
        var cfg = _config.Value;
        _logger.LogInformation("[AhkBroker] Logging in to {Url}", cfg.PortalUrl);

        await _page!.GoToAsync(cfg.PortalUrl, WaitUntilNavigation.Networkidle0);
        await FillFieldAsync("#ps_userid",   cfg.Username);
        await FillFieldAsync("#ps_password", cfg.Password);
        await _page.ClickAsync("input[type=submit]");

        await _page.WaitForSelectorAsync("#buysymbol",
            new WaitForSelectorOptions { Timeout = 15_000 });

        _logger.LogInformation("[AhkBroker] Login successful.");
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
        if (_browser is not null)
        {
            try { await _browser.CloseAsync(); }
            catch { /* best effort */ }
            _browser.Dispose();
            _browser = null;
        }
        _gate.Dispose();
    }
}
