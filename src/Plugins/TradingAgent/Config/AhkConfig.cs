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

    /// <summary>
    /// When a BUY tip names a stock and a clear buy intent but gives NO entry price ("accumulate on
    /// dips"), the limit is set to the live last-trade price read from the portal, LESS this percentage,
    /// so the order rests just below market to catch a dip. Default 1%. Set 0 to buy at the live price.
    /// Only used when <c>TradingAgent.AutoBuyWithoutEntryPrice</c> is enabled.
    /// </summary>
    public decimal DipDiscountPercent { get; set; } = 1m;

    /// <summary>
    /// Clamp a limit price into the day's price band before submitting: a SELL above the Upper Cap is
    /// lowered to the cap, a BUY below the Lower Lock is raised to the lock. PSX rejects any order outside
    /// the band (so e.g. a take-profit above today's cap would fail). Default true. When the band can't be
    /// read from the dialog, the price is left as-is. Set false to always submit the exact requested price.
    /// </summary>
    public bool ClampPriceToBand { get; set; } = true;
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

    /// <summary>
    /// How far below the trigger a stop-loss SELL's limit price is placed, in percent (default 1.0).
    /// A stop limit set exactly AT the trigger frequently fails to fill in the fast move that
    /// triggered it — the market is already through the level by the time the order is working. Set 0
    /// to place the limit at the trigger itself, accepting that risk.
    /// </summary>
    public decimal StopLimitSlippagePercent { get; set; } = 1.0m;

    /// <summary>
    /// How long to wait for the portal to show an order confirmation/error after submit. Also covers a
    /// LATE "Are you sure?" prompt (see <see cref="ConfirmPromptTimeoutMs"/>), so on a slow machine this
    /// must be comfortably larger than the prompt timeout or a confirmed order reads back as unconfirmed.
    /// </summary>
    public int OrderConfirmTimeoutMs { get; set; } = 15_000;

    // ── Slow-machine readiness timeouts ────────────────────────────────────────
    // The portal renders its toolbar, modal and confirmation prompt asynchronously. On a slow or loaded
    // machine each of those can arrive seconds after the step that triggered it, so every one of these
    // waits is a bounded poll — never a fixed sleep. Raise them if orders fail with "could not open the
    // … dialog" or "submit button not found"; the cost of a larger value is only spent when the portal
    // really is that slow.

    /// <summary>
    /// How long to wait for the order dialog to actually open (toolbar button to render + the modal's
    /// symbol field to become visible). The open click is retried within this window, because a click
    /// that lands before the portal binds its handler opens nothing at all.
    /// </summary>
    public int DialogOpenTimeoutMs { get; set; } = 15_000;

    /// <summary>
    /// How long to wait for the order submit button to become visible and enabled. The button is only
    /// ever clicked ONCE — this timeout governs waiting for it to appear, never a retry of the submit.
    /// </summary>
    public int SubmitButtonTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// How long to wait for the portal's "Are you sure? You want to execute Buy/Sell order!" prompt
    /// after the submit click. The order does NOT execute until it is confirmed, so a too-small value
    /// on a slow machine means the submit silently never becomes an order.
    /// </summary>
    public int ConfirmPromptTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// How long to wait for the portal to resolve a typed symbol and auto-fill the price field. Waiting
    /// for the auto-fill (rather than sleeping a fixed interval) also guarantees the portal's populate
    /// cannot land AFTER our own limit price and overwrite it.
    /// </summary>
    public int SymbolResolveTimeoutMs { get; set; } = 6_000;

    /// <summary>
    /// How long to wait after loading the portal for it to settle into either the trading screen (a
    /// persisted session) or the login form, before deciding which one it is. Without this, a slow
    /// render looks like "username field not found" on an already-authenticated session.
    /// </summary>
    public int PageReadyTimeoutMs { get; set; } = 15_000;

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

    // ── Portfolio / balance (Exposure dialog) ──────────────────────────────────
    // The AHK portfolio lives in the "Exposure" modal (confirmed from the live portal DOM):
    // click #exposure in the menu, pick the account in #expaccount (the change event triggers the
    // data load), then flip to the Open Position tab and back to Collaterals — only after that does
    // the portal render the collaterals grid (#collateralstable) and the exposure summary panels
    // (#exposuretable1..3, where "Net Cash" lives). The defaults encode exactly that flow; every
    // step stays configurable in case the portal changes. On an extraction miss the broker dumps
    // portfolio_*.html + a screenshot into LogDir for selector re-discovery.

    /// <summary>
    /// Absolute or portal-relative URL of a page showing holdings and balance. Empty (default) →
    /// the portfolio is a dialog on the trading screen, opened via <see cref="PortfolioNavSelector"/>.
    /// </summary>
    public string PortfolioUrl { get; set; } = "";

    /// <summary>
    /// CSS selector of a collapsed sidebar/hamburger toggle that must be clicked to REVEAL the
    /// portfolio menu item before it can be clicked. On the AHK portal the "Exposure" item lives in
    /// a slide-out left menu that is hidden until the ☰ toggle is clicked. Empty → the menu item is
    /// assumed already reachable. Set this to the ☰ button's selector if the dialog never opens.
    /// </summary>
    public string PortfolioMenuToggleSelector { get; set; } = "";

    /// <summary>
    /// CSS selector of the menu element that opens the Exposure/portfolio dialog. Its click handler
    /// (site.js OpenExposureModalPopUp) builds the dialog's dynamic content and shows the modal; the
    /// broker fires it with a dispatched MouseEvent (see AhkBroker.ClickViaDomAsync).
    /// </summary>
    public string PortfolioNavSelector { get; set; } = "#exposure";

    /// <summary>
    /// CSS selector of the account dropdown inside the dialog. The first option with a non-"0"
    /// value is selected (option value "0" is the "Select Account" placeholder). Empty → skip.
    /// </summary>
    public string PortfolioAccountSelectSelector { get; set; } = "#expaccount";

    /// <summary>
    /// Elements clicked, in order, after the account is selected. The AHK dialog only renders the
    /// collaterals grid after flipping to Open Position and back to Collaterals (#collat).
    /// </summary>
    public List<string> PortfolioTabSequence { get; set; } = ["a[href='#openposition']", "#collat"];

    /// <summary>CSS selector of the holdings table. Empty → pick the best-scoring table by header names.</summary>
    public string HoldingsTableSelector { get; set; } = "#collateralstable";

    /// <summary>
    /// Exact column-header → field mapping for the holdings grid (case-insensitive header match).
    /// Keys are the internal field kinds: symbol, quantity, avgPrice, currentPrice, currentValue,
    /// investment, profitLoss. Defaults match the AHK collaterals grid; kinds not mapped here fall
    /// back to header-synonym heuristics. NOTE: on the AHK grid "Unsettled" is the unrealized P/L
    /// ((MTM_Price − Ave_Rate_Buy) × Qty, confirmed against live data) — never map profitLoss to
    /// "P/L_Settled", which is realized-only and normally 0.
    /// </summary>
    public Dictionary<string, string> HoldingsColumnMap { get; set; } = new()
    {
        ["symbol"]       = "Symbol",
        ["quantity"]     = "Total_Qty",
        ["avgPrice"]     = "Ave_Rate_Buy",
        ["currentPrice"] = "MTM_Price",
        ["currentValue"] = "Amount",
        ["profitLoss"]   = "Unsettled",
    };

    /// <summary>
    /// CSS selector of the element (panel) containing the cash amount. Defaults to the first
    /// exposure summary panel. Empty → the whole page text is scanned instead.
    /// </summary>
    public string AvailableBalanceSelector { get; set; } = "#exposuretable1";

    /// <summary>
    /// Label whose adjacent number is the available cash. The AHK exposure panel shows
    /// "Net Cash" (no "Available …" wording exists on this portal).
    /// </summary>
    public string AvailableBalanceLabel { get; set; } = "Net Cash";

    /// <summary>How long to wait for the dialog/grid to render after each navigation step (ms).</summary>
    public int PortfolioLoadTimeoutMs { get; set; } = 10_000;
}
