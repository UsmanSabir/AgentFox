namespace PageAgent.Core;

/// <summary>
/// Locates a Chromium-compatible browser and its default user-data directory
/// on the host system. Covers Chrome, Edge, and Brave across Windows, macOS, and Linux.
/// </summary>
public static class BrowserSystemDetector
{
    private static readonly Lazy<string?> _found = new(Scan);
    private static readonly Lazy<(string browser, string dataDir)?> _profileFound = new(ScanProfile);

    /// <summary>
    /// Path to the first system browser executable found, or null if none exists.
    /// Cached after the first call.
    /// </summary>
    public static string? ExecutablePath => _found.Value;

    /// <summary>
    /// The user-data directory for the first system browser found, or null.
    /// This is the folder passed to <c>--user-data-dir</c> that contains the
    /// "Default" profile, cookies, and saved logins.
    /// Cached after the first call.
    /// </summary>
    public static string? DefaultUserDataDir => _profileFound.Value?.dataDir;

    private static string? Scan()
    {
        foreach (var candidate in GetCandidatePaths())
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (UnauthorizedAccessException)
            {
                // Skip inaccessible paths
            }
        }
        return null;
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // ── Windows ─────────────────────────────────────────────────────────────
        // Google Chrome
        yield return Path.Combine(programFiles, @"Google\Chrome\Application\chrome.exe");
        yield return Path.Combine(programFilesX86, @"Google\Chrome\Application\chrome.exe");
        yield return Path.Combine(localAppData, @"Google\Chrome\Application\chrome.exe");

        // Microsoft Edge (ships with every modern Windows)
        yield return Path.Combine(programFilesX86, @"Microsoft\Edge\Application\msedge.exe");
        yield return Path.Combine(programFiles, @"Microsoft\Edge\Application\msedge.exe");

        // Brave
        yield return Path.Combine(programFiles, @"BraveSoftware\Brave-Browser\Application\brave.exe");
        yield return Path.Combine(programFilesX86, @"BraveSoftware\Brave-Browser\Application\brave.exe");

        // Chromium (unofficial builds)
        yield return Path.Combine(localAppData, @"Chromium\Application\chrome.exe");

        // ── macOS ────────────────────────────────────────────────────────────────
        yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        yield return "/Applications/Google Chrome Beta.app/Contents/MacOS/Google Chrome Beta";
        yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
        yield return "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser";
        yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";

        // ── Linux ────────────────────────────────────────────────────────────────
        yield return "/usr/bin/google-chrome";
        yield return "/usr/bin/google-chrome-stable";
        yield return "/usr/bin/chromium-browser";
        yield return "/usr/bin/chromium";
        yield return "/snap/bin/chromium";
        yield return "/usr/bin/brave-browser";
        yield return "/usr/bin/microsoft-edge";
        yield return "/usr/bin/microsoft-edge-stable";
    }

    // ── Profile detection ────────────────────────────────────────────────────

    /// <summary>
    /// Maps each known browser executable to its default user-data directory.
    /// Order matters: checked in priority order (Chrome before Edge before Brave).
    /// </summary>
    private static (string browser, string dataDir)? ScanProfile()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Each entry: (executable path, user-data directory)
        var candidates = new (string exe, string dataDir)[]
        {
            // ── Windows ─────────────────────────────────────────────────────
            (
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(localAppData, @"Google\Chrome\User Data")
            ),
            (
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(localAppData, @"Google\Chrome\User Data")
            ),
            (
                Path.Combine(localAppData, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(localAppData, @"Google\Chrome\User Data")
            ),
            (
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(localAppData, @"Microsoft\Edge\User Data")
            ),
            (
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(localAppData, @"Microsoft\Edge\User Data")
            ),
            (
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                Path.Combine(localAppData, @"BraveSoftware\Brave-Browser\User Data")
            ),

            // ── macOS ────────────────────────────────────────────────────────
            (
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                Path.Combine(home, "Library/Application Support/Google/Chrome")
            ),
            (
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                Path.Combine(home, "Library/Application Support/Microsoft Edge")
            ),
            (
                "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
                Path.Combine(home, "Library/Application Support/BraveSoftware/Brave-Browser")
            ),

            // ── Linux ────────────────────────────────────────────────────────
            ("/usr/bin/google-chrome",        Path.Combine(home, ".config/google-chrome")),
            ("/usr/bin/google-chrome-stable", Path.Combine(home, ".config/google-chrome")),
            ("/usr/bin/chromium-browser",     Path.Combine(home, ".config/chromium")),
            ("/usr/bin/chromium",             Path.Combine(home, ".config/chromium")),
            ("/snap/bin/chromium",            Path.Combine(home, "snap/chromium/current/.config/chromium")),
            ("/usr/bin/brave-browser",        Path.Combine(home, ".config/BraveSoftware/Brave-Browser")),
            ("/usr/bin/microsoft-edge",       Path.Combine(home, ".config/microsoft-edge")),
            ("/usr/bin/microsoft-edge-stable",Path.Combine(home, ".config/microsoft-edge")),
        };

        foreach (var (exe, dataDir) in candidates)
        {
            try
            {
                if (File.Exists(exe) && Directory.Exists(dataDir))
                    return (exe, dataDir);
            }
            catch (UnauthorizedAccessException) { }
        }

        return null;
    }
}
