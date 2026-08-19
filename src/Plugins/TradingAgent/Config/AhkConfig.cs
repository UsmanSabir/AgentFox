namespace TradingAgent.Config;

public class AhkConfig
{
    public const string SectionName = "Ahk";

    /// <summary>
    /// The broker portal's base URL. This default is the LIVE one and must stay correct on its own:
    /// the previous default (<c>www.ahktrading.com</c>) no longer belongs to the broker and now
    /// redirects to an unrelated parked domain, so any deployment that did not override it in
    /// appsettings was pointing the login flow at a stranger's site. A wrong value here fails as a
    /// login-page-not-found rather than as anything that names the URL, so it is worth keeping this
    /// in step with <c>Plugins:Ahk:PortalUrl</c> in appsettings.json.
    /// </summary>
    public string PortalUrl { get; set; } = "https://web.ahletrade.com/";
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

    /// <summary>
    /// Read the portfolio through the portal's JSON API (<c>GetCollaterals</c> + <c>GetAccountBalance</c>)
    /// instead of driving the browser through the Exposure dialog. Default true.
    ///
    /// <para>
    /// The browser path costs a page load, a modal, a tab dance and a heuristic table scrape, and it
    /// holds the broker's single-page gate for the whole of it — which also makes the live feed yield.
    /// The API path is two HTTP GETs and takes no gate. Falling back is automatic: if the API read
    /// fails for any reason the browser scrape still runs, so switching this off is only needed to
    /// force the old path for cross-checking.
    /// </para>
    /// </summary>
    public bool PreferDirectApiForPortfolio { get; set; } = true;
    /// <summary>
    /// Market states, as reported by the BROKER's own feed, in which an order may be submitted.
    ///
    /// <para>
    /// <c>OHO</c> is included deliberately. It is PSX's pre-open order-handling state: the broker
    /// accepts the order and it becomes live at the open, which is when queue priority is worth
    /// having and when an overnight signal most wants to act. The portal renders OHO in the same
    /// green "success" style as OPEN and never disables its order form for it. Gating orders on the
    /// regular matching session alone — the original behaviour — silently forfeited that window.
    /// </para>
    ///
    /// <para>
    /// Both <c>OPEN</c> and <c>OPN</c> appear because the portal uses two vocabularies:
    /// <c>GetFeed.marketStatus</c> says <c>OPEN</c>/<c>CLOSED</c>/<c>OHO</c>, while
    /// <c>GetMarketStates[].state</c> says <c>OPN</c>/<c>CLO</c>/<c>OHO</c>/<c>Close</c>.
    /// </para>
    ///
    /// <para>
    /// Left EMPTY on purpose, meaning "use the built-in defaults" (see
    /// <see cref="Market.OrderWindow.DefaultAcceptingStates"/>). A pre-populated collection property
    /// is a trap here: .NET's ConfigurationBinder APPENDS to one rather than replacing it, so three
    /// defaults plus three values in appsettings bind to six. To turn broker-state gating off
    /// entirely, set <see cref="TrustBrokerMarketState"/> to false rather than emptying this.
    /// </para>
    /// </summary>
    public List<string> OrderAcceptingMarketStates { get; set; } = [];

    /// <summary>
    /// Prefer the broker's reported market state over the local trading calendar when deciding
    /// whether an order may be submitted (default true).
    ///
    /// <para>
    /// The venue's own state is authoritative in a way a hardcoded 09:32–15:30 schedule cannot be: it
    /// reflects halts, extended sessions and unscheduled closures as they happen. The calendar
    /// remains the fallback whenever the broker has not reported a state — the feed being switched
    /// off, or not yet polled. Set false to gate purely on the calendar.
    /// </para>
    /// </summary>
    public bool TrustBrokerMarketState { get; set; } = true;

    /// <summary>
    /// How long <c>cancel_order</c> waits for a cancelled order to actually leave the outstanding
    /// book before reporting the cancellation as unconfirmed. Default 30s.
    ///
    /// <para>
    /// Generous headroom rather than a measured requirement. Measured live, a cancel confirms on the
    /// first verification poll — about 2.1s end to end. An earlier 8s window did once report a cancel
    /// as unconfirmed, but that was the broker having blocked account access mid-test, not latency;
    /// the reasoning that produced a 30s default was therefore wrong even though the value is
    /// harmless. It stays because the cost of waiting is nothing (the loop exits as soon as the order
    /// is gone) while the cost of giving up too early is telling a user an order is still live when
    /// it is not.
    /// </para>
    ///
    /// <para>
    /// The portal returns no success indicator at all — its own UI fires the cancel and closes the
    /// dialog without reading the response — so the order book is the only evidence. Exceeding this
    /// window is reported honestly as unconfirmed rather than assumed either way.
    /// </para>
    /// </summary>
    public int CancelVerifyTimeoutMs { get; set; } = 30_000;

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
    /// After submitting, confirm the order actually exists by reading the account's own order book
    /// (Outstanding Log, then Activity Log) instead of trusting the portal's result popup.
    ///
    /// <para>
    /// This is not belt-and-braces, it is the only reliable signal. Observed directly against the live
    /// portal: an off-hours submission returns HTTP 200 with an EMPTY body and shows a green "success"
    /// alert while placing nothing at all, and the happy path returns no order number either. The order
    /// book is the only place that distinguishes "placed" from "silently discarded".
    /// </para>
    /// </summary>
    public bool VerifyOrderInBook { get; set; } = true;

    /// <summary>How long to wait for a submitted order to appear in the order book. Default 8s.</summary>
    public int OrderBookVerifyTimeoutMs { get; set; } = 8_000;

    /// <summary>
    /// Records the RAW request and response of the portal's own <c>POST /Home/PlaceOrder</c> and
    /// <c>POST /Home/CancelOrder</c> calls to <c>{LogDir}/order_api_capture.log</c> while the browser
    /// path drives them. Pure observation: it changes nothing about how an order is placed.
    ///
    /// <para>
    /// <b>Why this is on by default.</b> Moving placement off the browser is blocked on one unknown —
    /// what the portal actually ANSWERS. Its own UI throws the answer away (<c>site.js</c> shows a
    /// hardcoded "Your buy order has been sent." on the buy side and never reads <c>res</c>), so
    /// neither the DOM path nor any amount of reading <c>site.js</c> can reveal it, and the only place
    /// it exists is on the wire of a real submission. Capturing it here means the evidence comes from
    /// an order the agent was going to place anyway, instead of from a test order placed with real
    /// money to see what happens. See <c>docs/ahk-direct-api-migration.md</c>.
    /// </para>
    ///
    /// <para>
    /// The <c>PIN</c> form field is redacted before anything is written. Nothing else in the payload is
    /// a secret — it is the order the user asked for.
    /// </para>
    /// </summary>
    public bool CaptureOrderApiTraffic { get; set; } = true;

    /// <summary>Tab that reveals resting orders.</summary>
    public string OutstandingLogTabSelector { get; set; } = "a[href='#out_log']";

    /// <summary>Panel containing the resting-order table.</summary>
    public string OutstandingLogPanelSelector { get; set; } = "#out_log";

    /// <summary>
    /// Tab that reveals filled/working activity. Checked as well as the outstanding log because an
    /// order that filled immediately never rests, so its absence from the outstanding book would
    /// otherwise be misread as "never placed".
    /// </summary>
    public string ActivityLogTabSelector { get; set; } = "a[href='#act_log']";

    /// <summary>Panel containing the activity table.</summary>
    public string ActivityLogPanelSelector { get; set; } = "#act_log";

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
    /// How long to wait, after the credentials are submitted, for the portal to actually land on the
    /// trading screen. Default 60s.
    ///
    /// <para>
    /// This is not the same wait as <see cref="PageReadyTimeoutMs"/>, which decides whether a freshly
    /// loaded portal is showing the login form or an already-authenticated session. This one covers the
    /// gap AFTER a successful credential submission, and it is long because the portal really does take
    /// that long: it was hardcoded to 15s, and the resulting failure was maximally misleading — the login
    /// had SUCCEEDED, the dumped page still showed the login form because navigation had not finished,
    /// and the broker reported "login could not be confirmed" and retried, spending a second real login
    /// attempt against a broker that withdraws access after roughly fifteen in two hours. A slow portal
    /// must cost patience, not login attempts.
    /// </para>
    /// </summary>
    public int LoginVerifyTimeoutMs { get; set; } = 60_000;

    /// <summary>
    /// Launch the browser only when an order is placed and close it once the order finishes (default
    /// true). The persisted profile keeps the session authenticated, so the next order re-launches and
    /// usually skips the full login. Set false to keep one long-lived browser session across orders.
    /// </summary>
    public bool CloseBrowserAfterOrder { get; set; } = true;

    /// <summary>
    /// After handing session cookies to the direct JSON API, navigate the browser to
    /// <c>about:blank</c> instead of leaving it parked on the portal's trading screen. Default true.
    ///
    /// <para>
    /// The cookie harvest deliberately keeps the browser alive (the direct API's session depends on the
    /// portal still considering it live), which means that with the live feed enabled the browser sits
    /// on the trading screen for the whole run. That page's own <c>site.js</c> keeps polling
    /// <c>/Home/GetFeed</c> and keeps re-subscribing an empty <c>Page1</c> — competing with our own
    /// poller on the same session, invisibly, because <c>BrowserHoldsTradingScreen</c> only counts our
    /// in-flight operations and an idle window is not one. Parking the page keeps the warm session and
    /// removes the competition. See <c>AhkBroker.ParkPageAsync</c>.
    /// </para>
    /// </summary>
    public bool ParkPageAfterCookieHarvest { get; set; } = true;

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
