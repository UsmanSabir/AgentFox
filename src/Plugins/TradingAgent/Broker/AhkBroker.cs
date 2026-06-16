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

    private IBrowser? _browser;
    private IPage? _page;
    private bool _initialized;

    public AhkBroker(IOptions<AhkConfig> config, ILogger<AhkBroker> logger)
    {
        _config = config;
        _logger = logger;
    }

    // ── Initialization ────────────────────────────────────────────────────────

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        var cfg = _config.Value;
        Directory.CreateDirectory(cfg.SessionDir);
        Directory.CreateDirectory(cfg.LogDir);

        var launchOptions = new LaunchOptions
        {
            Headless    = false,
            UserDataDir = cfg.SessionDir,
            Args        = ["--no-sandbox", "--disable-setuid-sandbox"]
        };

        _browser = await Puppeteer.LaunchAsync(launchOptions);

        var pages = await _browser.PagesAsync();
        _page = pages.Length > 0 ? pages[0] : await _browser.NewPageAsync();

        _initialized = true;
        _logger.LogInformation("[AhkBroker] Browser session ready. Profile: {Dir}", cfg.SessionDir);
    }

    // ── Order placement ───────────────────────────────────────────────────────

    public async Task<OrderResult> PlaceOrderAsync(TradingSignal signal)
    {
        if (!_initialized || _page is null)
            throw new InvalidOperationException(
                "AhkBroker is not initialized. Call InitializeAsync before placing orders.");

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
        var cfg = _config.Value;
        var qty = signal.Quantity ?? cfg.DefaultQty;

        _logger.LogInformation("[AhkBroker] BUY {Symbol} x{Qty} @ {Price}",
            signal.Symbol, qty, signal.EntryPrice);

        await FillFieldAsync("#buysymbol", signal.Symbol);
        await Task.Delay(800); // wait for autocomplete dropdown
        await _page!.Keyboard.PressAsync("Tab");

        await FillFieldAsync("#buyvolume", qty.ToString());

        if (signal.EntryPrice.HasValue)
        {
            var price = signal.EntryPrice.Value.ToString("F2");
            await FillFieldAsync("#buyprice",      price);
            await FillFieldAsync("#buylimitprice", price);
        }

        await FillFieldAsync("#buyPIN", cfg.TradingPin);

        var before = await ScreenshotAsync("pre_buy");

        await ClickSubmitAsync("buy");

        await Task.Delay(2_000); // brief settle before after-screenshot
        var after = await ScreenshotAsync("post_buy");

        return new OrderResult
        {
            Success          = true,
            Action           = "BUY",
            Symbol           = signal.Symbol,
            Message          = $"BUY order submitted for {signal.Symbol} x{qty}",
            ScreenshotBefore = before,
            ScreenshotAfter  = after
        };
    }

    // ── SELL ──────────────────────────────────────────────────────────────────

    private async Task<OrderResult> PlaceSellAsync(TradingSignal signal)
    {
        var cfg = _config.Value;
        var qty = signal.Quantity ?? cfg.DefaultQty;

        _logger.LogInformation("[AhkBroker] SELL {Symbol} x{Qty} @ {Price}",
            signal.Symbol, qty, signal.EntryPrice);

        await FillFieldAsync("#sellsymbol", signal.Symbol);
        await Task.Delay(800);
        await _page!.Keyboard.PressAsync("Tab");

        await FillFieldAsync("#sellvolume", qty.ToString());

        if (signal.EntryPrice.HasValue)
        {
            var price = signal.EntryPrice.Value.ToString("F2");
            await FillFieldAsync("#sellprice",      price);
            await FillFieldAsync("#selllimitprice", price);
        }

        await FillFieldAsync("#sellPIN", cfg.TradingPin);

        var before = await ScreenshotAsync("pre_sell");

        await ClickSubmitAsync("sell");

        await Task.Delay(2_000);
        var after = await ScreenshotAsync("post_sell");

        return new OrderResult
        {
            Success          = true,
            Action           = "SELL",
            Symbol           = signal.Symbol,
            Message          = $"SELL order submitted for {signal.Symbol} x{qty}",
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

    private async Task<string> ScreenshotAsync(string prefix)
    {
        var path = Path.Combine(
            _config.Value.LogDir,
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
    }
}
