namespace PageAgent.Config;

/// <summary>
/// Configuration options for the autonomous browser agent.
/// Bind from appsettings.json under the "PageAgent" section.
/// </summary>
public sealed class BrowserAgentOptions
{
    public const string SectionName = "PageAgent";

    /// <summary>Run the browser in headless mode (no visible window). Default: true.</summary>
    public bool Headless { get; set; } = true;

    /// <summary>Maximum number of reasoning/action steps per goal. Default: 15.</summary>
    public int MaxSteps { get; set; } = 15;

    /// <summary>Maximum characters extracted from a page. Default: 3000.</summary>
    public int MaxExtractLength { get; set; } = 3000;

    /// <summary>Maximum number of links captured during page analysis. Default: 20.</summary>
    public int MaxLinks { get; set; } = 20;

    /// <summary>Maximum number of headings captured during page analysis. Default: 10.</summary>
    public int MaxHeadings { get; set; } = 10;

    /// <summary>Navigation timeout in milliseconds. Default: 30000.</summary>
    public int NavigationTimeoutMs { get; set; } = 30_000;

    /// <summary>Number of retry attempts for failed navigation. Default: 2.</summary>
    public int RetryAttempts { get; set; } = 2;

    /// <summary>Overall timeout per RunAsync call in minutes. Default: 5.</summary>
    public int RunTimeoutMinutes { get; set; } = 5;

    /// <summary>Google search URL prefix.</summary>
    public string SearchEngineUrl { get; set; } = "https://www.google.com/search?q=";

    /// <summary>Fallback search engine (Bing) used when Google is blocked.</summary>
    public string FallbackSearchEngineUrl { get; set; } = "https://www.bing.com/search?q=";

    /// <summary>Browser viewport width. Default: 1280.</summary>
    public int ViewportWidth { get; set; } = 1280;

    /// <summary>Browser viewport height. Default: 900.</summary>
    public int ViewportHeight { get; set; } = 900;

    /// <summary>
    /// Path to an existing browser user-data directory (the folder that contains
    /// the "Default" profile, bookmarks, cookies, and saved logins).
    /// When set, the agent inherits all cookies and logged-in sessions from that profile.
    ///
    /// Leave null (default) to launch with a clean ephemeral profile.
    ///
    /// ⚠ The browser must NOT already be running with this profile — Chrome/Edge
    /// lock the directory and will refuse a second instance.
    /// Use <c>UseProfileCopy = true</c> to work around this.
    ///
    /// Typical paths:
    ///   Windows Chrome : %LOCALAPPDATA%\Google\Chrome\User Data
    ///   Windows Edge   : %LOCALAPPDATA%\Microsoft\Edge\User Data
    ///   macOS Chrome   : ~/Library/Application Support/Google/Chrome
    ///   Linux Chrome   : ~/.config/google-chrome
    /// </summary>
    public string? UserDataDir { get; set; }

    /// <summary>
    /// When true AND <see cref="UserDataDir"/> is set, the agent copies the profile
    /// to a temporary directory before launching so the original browser can stay open.
    /// The copy is deleted automatically when the browser session ends.
    /// Default: false.
    /// </summary>
    public bool UseProfileCopy { get; set; } = false;

    /// <summary>
    /// When true, auto-detect the default user-data directory for the system browser
    /// found by <see cref="Core.BrowserSystemDetector"/>. Ignored when
    /// <see cref="UserDataDir"/> is set explicitly. Default: false.
    /// </summary>
    public bool UseSystemProfile { get; set; } = true;

    /// <summary>
    /// Automatically restart the browser if it crashes mid-run and continue from
    /// where the agent left off. Default: true.
    /// </summary>
    public bool AutoRestartOnCrash { get; set; } = true;

    /// <summary>
    /// Maximum number of automatic browser restarts allowed per run.
    /// Prevents an infinite crash-restart loop. Default: 3.
    /// </summary>
    public int MaxRestarts { get; set; } = 3;

    /// <summary>
    /// When true AND <see cref="UseProfileCopy"/> is false, kill any running browser
    /// process that holds the profile directory before launching (or restarting).
    /// This allows the agent to take over the real browser's profile without first
    /// closing it manually.
    ///
    /// ⚠ This terminates the user's browser and all its open tabs. Default: false.
    /// </summary>
    public bool KillConflictingBrowser { get; set; } = true;
}
