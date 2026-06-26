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

    /// <summary>Selector that exists only once logged in (used to confirm a successful login).</summary>
    public string LoggedInSelector { get; set; } = "#buysymbol";
}
