namespace TradingAgent.Config;

public class AhkConfig
{
    public const string SectionName = "Ahk";

    public string PortalUrl { get; set; } = "https://www.ahktrading.com";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string TradingPin { get; set; } = "";
    public int DefaultQty { get; set; } = 100;
    public decimal MaxOrderValuePkr { get; set; } = 50_000m;

    /// <summary>
    /// Target spend per stock (PKR) used to size an order when the signal carries no explicit share
    /// count. Shares = floor((PerStockBudgetPkr × (1 − BudgetBufferPercent/100)) ÷ limit price). The
    /// resulting order value is still checked against <see cref="MaxOrderValuePkr"/>, so keep the cap
    /// at or above the budget or every auto-sized order will be blocked.
    /// </summary>
    public decimal PerStockBudgetPkr { get; set; } = 50_000m;

    /// <summary>
    /// Headroom kept aside when sizing from <see cref="PerStockBudgetPkr"/> (percent). Leaves room for
    /// fees and price drift so the actual fill stays under budget. Default 2%.
    /// </summary>
    public decimal BudgetBufferPercent { get; set; } = 2m;
    public string SessionDir { get; set; } = "session_ahk";
    public string LogDir { get; set; } = "logs/trading";

    /// <summary>Run Chromium without a visible window. Default true so the agent can run as a service.</summary>
    public bool Headless { get; set; } = true;

    /// <summary>
    /// Path to a Chrome/Chromium executable. When empty, the broker downloads a matching
    /// Chromium via PuppeteerSharp's BrowserFetcher on first launch.
    /// </summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>
    /// Allow MARKET orders (no limit price). Default false: market orders cannot be value-capped,
    /// so they are blocked unless this is explicitly enabled.
    /// </summary>
    public bool AllowMarketOrders { get; set; } = false;

    /// <summary>How long to wait for the portal to show an order confirmation/error after submit.</summary>
    public int OrderConfirmTimeoutMs { get; set; } = 8_000;

    /// <summary>
    /// Launch the browser only when an order is placed and close it once the order finishes (default
    /// true). The persisted profile keeps the session authenticated, so the next order re-launches and
    /// usually skips the full login. Set false to keep one long-lived browser session across orders.
    /// </summary>
    public bool CloseBrowserAfterOrder { get; set; } = true;

    // ── Order form selectors ────────────────────────────────────────────────────
    // The buy/sell field ids (#buysymbol, #buyvolume, …) are stable on the AHK portal and used
    // directly. The submit button, however, has NO id — its only distinguishing feature is its
    // exact visible text ("BUY"/"SELL"). Leave these empty to use that exact-text matching; set a
    // CSS selector only if the portal later gives the submit button a stable id/class.

    /// <summary>CSS selector for the BUY submit button. Empty → match the button whose text is exactly "BUY".</summary>
    public string BuySubmitSelector { get; set; } = "";

    /// <summary>CSS selector for the SELL submit button. Empty → match the button whose text is exactly "SELL".</summary>
    public string SellSubmitSelector { get; set; } = "";

    // ── Login form selectors ───────────────────────────────────────────────────
    // Leave empty to use the built-in heuristics. Override only if the heuristics pick the wrong
    // element — inspect the dumped login_*.html (written to LogDir on a login failure) to find IDs.

    /// <summary>CSS selector for the username input. Empty → heuristic discovery.</summary>
    public string UsernameSelector { get; set; } = "";

    /// <summary>
    /// CSS selector for the single-character positional password boxes. The default matches the
    /// AHK "Web Trade Cast" character grid (maxlength=1 inputs). Only the enabled boxes are filled.
    /// </summary>
    public string PasswordBoxSelector { get; set; } = "input[maxlength='1']";

    /// <summary>CSS selector for the Login button. Empty → heuristic (text/value matching "login").</summary>
    public string LoginButtonSelector { get; set; } = "";

    /// <summary>
    /// Selector that exists only once logged in (used to confirm a successful login). Defaults to the
    /// toolbar "Buy Order" button (#buyorder), which is always present on the trading screen — unlike
    /// the modal field #buysymbol, which only exists while the buy dialog is open.
    /// </summary>
    public string LoggedInSelector { get; set; } = "#buyorder";
}
