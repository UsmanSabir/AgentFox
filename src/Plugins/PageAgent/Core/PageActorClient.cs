using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageAgent.Config;
using PageAgent.Models;
using PuppeteerSharp;

namespace PageAgent.Core;

/// <summary>
/// Wraps PuppeteerSharp to provide high-level browser interactions.
///
/// Supports explicit <see cref="KillAsync"/> and <see cref="RestartAsync"/> so the
/// autonomous agent can recover from crashes or hung pages without losing the profile
/// copy or having to re-initialize from scratch.
/// </summary>
public sealed class PageActorClient : IAsyncDisposable
{
    private readonly BrowserAgentOptions _options;
    private readonly ILogger<PageActorClient> _logger;

    // ── Browser state ────────────────────────────────────────────────────────
    private IBrowser? _browser;
    private IPage? _page;
    private bool _initialized;
    private volatile bool _crashed;

    // ── Saved launch parameters (reused on restart) ──────────────────────────
    private string? _resolvedExecutablePath;
    private string? _resolvedUserDataDir;
    private string? _tempProfileDir;   // non-null when we own a profile copy

    private readonly SemaphoreSlim _initLock = new(1, 1);

    // ── Public state ─────────────────────────────────────────────────────────

    /// <summary>Current page URL reported by the browser.</summary>
    public string CurrentUrl => _page?.Url ?? "about:blank";

    /// <summary>
    /// True when the browser process has disconnected unexpectedly (crash, OOM, etc.).
    /// Cleared automatically by <see cref="RestartAsync"/>.
    /// </summary>
    public bool IsCrashed => _crashed;

    /// <summary>True when the browser is running and has not crashed.</summary>
    public bool IsAlive => _initialized && !_crashed;

    public PageActorClient(IOptions<BrowserAgentOptions> options, ILogger<PageActorClient> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches the browser for the first time.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            _resolvedExecutablePath = await EnsureExecutableAsync(ct);
            _resolvedUserDataDir    = ResolveUserDataDir();

            await LaunchBrowserCoreAsync(_resolvedExecutablePath, _resolvedUserDataDir);

            _initialized = true;
            _logger.LogInformation("Browser session ready.");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Forcefully kills the browser process. The instance remains usable —
    /// call <see cref="RestartAsync"/> to bring it back up.
    /// </summary>
    public async Task KillAsync()
    {
        _logger.LogWarning("Killing browser process.");

        if (_page is not null)
        {
            try { await _page.CloseAsync(); } catch { }
            try { await _page.DisposeAsync(); } catch { }
            _page = null;
        }

        if (_browser is not null)
        {
            try { await _browser.CloseAsync(); } catch { }
            try { await _browser.DisposeAsync(); } catch { }
            _browser = null;
        }
    }

    /// <summary>
    /// Kills the current browser process and launches a fresh one using the same
    /// executable and user-data directory (including the profile copy, if any).
    /// The agent can continue working without re-copying the profile.
    /// </summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Restarting browser...");

        await KillAsync();
        _crashed = false;

        await LaunchBrowserCoreAsync(_resolvedExecutablePath, _resolvedUserDataDir);

        _logger.LogInformation("Browser restarted successfully.");
    }

    // ── High-level actions ───────────────────────────────────────────────────

    /// <summary>Searches via the configured search engine and waits for results.</summary>
    public async Task<string> SearchAsync(string query, CancellationToken ct = default)
    {
        var url = _options.SearchEngineUrl + Uri.EscapeDataString(query);
        _logger.LogInformation("Search: \"{Query}\"", query);
        return await NavigateAsync(url, ct);
    }

    /// <summary>
    /// Navigates directly to <paramref name="url"/>, retrying on transient failures.
    /// Prepends "https://" if no scheme is present.
    /// </summary>
    public async Task<string> OpenAsync(string url, CancellationToken ct = default)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        _logger.LogInformation("Open: {Url}", url);
        return await NavigateAsync(url, ct);
    }

    /// <summary>
    /// Analyses the current page's DOM and returns a structured <see cref="PageAnalysis"/>
    /// containing the title, top headings, and top links.
    /// </summary>
    public async Task<PageAnalysis> AnalyzePageAsync(CancellationToken ct = default)
    {
        EnsureAlive();
        try
        {
            var title       = await _page!.GetTitleAsync();
            var headingsJson = await _page.EvaluateExpressionAsync<string>(
                DomAnalyzer.HeadingsScript(_options.MaxHeadings));
            var linksJson   = await _page.EvaluateExpressionAsync<string>(
                DomAnalyzer.LinksScript(_options.MaxLinks));

            var headings = DomAnalyzer.DeserialiseStrings(headingsJson);
            var links    = DomAnalyzer.DeserialiseLinks(linksJson)
                .Select(l => new LinkInfo { Text = l.Text, Href = l.Href })
                .ToList();

            _logger.LogDebug("Analyzed: \"{Title}\" — {H} headings, {L} links",
                title, headings.Count, links.Count);

            return new PageAnalysis { Title = title, Url = _page.Url, Headings = headings, Links = links };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Page analysis failed: {Error}", ex.Message);
            return new PageAnalysis { Url = _page!.Url, Error = ex.Message };
        }
    }

    /// <summary>
    /// Extracts the visible text of the current page, stripping navigation,
    /// footers, scripts, and ads. Truncated to <c>MaxExtractLength</c> characters.
    /// </summary>
    public async Task<string> ExtractContentAsync(CancellationToken ct = default)
    {
        EnsureAlive();
        try
        {
            var text = await _page!.EvaluateExpressionAsync<string>(
                DomAnalyzer.TextContentScript(_options.MaxExtractLength));
            _logger.LogDebug("Extracted {Chars} chars from {Url}", text?.Length ?? 0, _page.Url);
            return text ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Content extraction failed: {Error}", ex.Message);
            return string.Empty;
        }
    }

    /// <summary>
    /// Finds and clicks the best-matching link/button for <paramref name="targetText"/>
    /// using cascading fuzzy matching (exact → contains → word-level).
    /// </summary>
    public async Task<string> SmartClickAsync(string targetText, CancellationToken ct = default)
    {
        EnsureAlive();
        try
        {
            var result = await _page!.EvaluateExpressionAsync<string>(
                DomAnalyzer.SmartClickScript(targetText));

            if (result?.StartsWith("clicked:", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogInformation("Clicked: {Label}", result[8..]);
                try
                {
                    await _page.WaitForNavigationAsync(new NavigationOptions
                    {
                        WaitUntil = [WaitUntilNavigation.Networkidle2],
                        Timeout = 6_000,
                    });
                }
                catch (TimeoutException) { /* SPA route or modal — no full navigation, that's fine */ }
                return result;
            }

            _logger.LogWarning("SmartClick: no element matched \"{Target}\"", targetText);
            return result ?? "no_match";
        }
        catch (Exception ex)
        {
            _logger.LogWarning("SmartClick failed: {Error}", ex.Message);
            return $"click_error: {ex.Message}";
        }
    }

    // ── Internal: launch ─────────────────────────────────────────────────────

    /// <summary>
    /// Core launch logic — creates <see cref="_browser"/> and <see cref="_page"/>,
    /// subscribes to the <c>Disconnected</c> event for crash detection, and configures
    /// the page viewport + user-agent. Does NOT touch <see cref="_tempProfileDir"/>.
    /// </summary>
    private async Task LaunchBrowserCoreAsync(string? executablePath, string? userDataDir)
    {
        // When using the profile directly (no copy), free any process holding the dir
        // and remove stale lock files so Chrome accepts a new instance.
        if (userDataDir is not null && !_options.UseProfileCopy)
        {
            if (_options.KillConflictingBrowser)
                await KillConflictingBrowserProcessesAsync(executablePath);

            CleanProfileLockFiles(userDataDir);
        }

        var launchOptions = new LaunchOptions
        {
            Headless     = _options.Headless,
            ExecutablePath = executablePath,
            UserDataDir  = userDataDir,
            Args =
            [
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--disable-gpu",
                "--window-size=1280,900",
            ],
        };

        _browser = await Puppeteer.LaunchAsync(launchOptions);

        // ── Crash detection ──────────────────────────────────────────────────
        _browser.Disconnected += (_, _) =>
        {
            if (!_crashed)   // only log once
            {
                _crashed = true;
                _logger.LogWarning("Browser disconnected unexpectedly (crash or external kill).");
            }
        };

        _page = await _browser.NewPageAsync();

        await _page.SetViewportAsync(new ViewPortOptions
        {
            Width  = _options.ViewportWidth,
            Height = _options.ViewportHeight,
        });

        await _page.SetUserAgentAsync(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");

        _page.Console += (_, e) =>
            _logger.LogTrace("[browser] {Type}: {Text}", e.Message.Type, e.Message.Text);
    }

    /// <summary>
    /// Ensures a browser executable is available, downloading Chromium if needed.
    /// Returns the path (or null when PuppeteerSharp's bundled copy should be used).
    /// </summary>
    private async Task<string?> EnsureExecutableAsync(CancellationToken ct)
    {
        var path = BrowserSystemDetector.ExecutablePath;
        if (path is not null)
        {
            _logger.LogInformation("Using system browser: {Path}", path);
            return path;
        }

        _logger.LogInformation(
            "No system browser found. Downloading Chromium (this may take a moment)...");
        var fetcher = new BrowserFetcher();
        await fetcher.DownloadAsync();
        _logger.LogInformation("Chromium download complete.");
        return null;
    }

    // ── Internal: navigation ─────────────────────────────────────────────────

    private async Task<string> NavigateAsync(string url, CancellationToken ct)
    {
        EnsureAlive();

        for (int attempt = 1; attempt <= _options.RetryAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _page!.GoToAsync(url, new NavigationOptions
                {
                    WaitUntil = [WaitUntilNavigation.Networkidle2],
                    Timeout   = _options.NavigationTimeoutMs,
                });
                _logger.LogDebug("Navigated to: {Url}", _page.Url);
                return $"Navigated to: {_page.Url}";
            }
            catch (Exception ex) when (attempt < _options.RetryAttempts)
            {
                _logger.LogWarning("Navigation attempt {A}/{Max} failed: {Err}",
                    attempt, _options.RetryAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }

        // Final attempt — propagate exception
        await _page!.GoToAsync(url, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.Networkidle2],
            Timeout   = _options.NavigationTimeoutMs,
        });
        return $"Navigated to: {_page.Url}";
    }

    // ── Internal: profile ────────────────────────────────────────────────────

    /// <summary>
    /// Resolves which user-data directory to pass on first launch.
    /// On restart this is skipped — <see cref="_resolvedUserDataDir"/> is reused directly.
    /// </summary>
    private string? ResolveUserDataDir()
    {
        string? sourceDir = _options.UserDataDir;

        if (sourceDir is null && _options.UseSystemProfile)
        {
            sourceDir = BrowserSystemDetector.DefaultUserDataDir;
            if (sourceDir is not null)
                _logger.LogInformation("Auto-detected system profile: {Dir}", sourceDir);
        }

        if (sourceDir is null) return null;

        if (!Directory.Exists(sourceDir))
        {
            _logger.LogWarning(
                "UserDataDir '{Dir}' does not exist — launching with ephemeral profile.", sourceDir);
            return null;
        }

        if (_options.UseProfileCopy)
        {
            _tempProfileDir = Path.Combine(Path.GetTempPath(), "PageAgent_" + Guid.NewGuid().ToString("N"));
            _logger.LogInformation("Copying profile → {Temp}", _tempProfileDir);
            CopyDirectory(sourceDir, _tempProfileDir);
            return _tempProfileDir;
        }

        _logger.LogInformation(
            "Using profile directly: {Dir} — close any running browser windows first.", sourceDir);
        return sourceDir;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            try { File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true); }
            catch { /* skip locked files */ }
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals("Cache",       StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Code Cache",  StringComparison.OrdinalIgnoreCase) ||
                name.Equals("GPUCache",    StringComparison.OrdinalIgnoreCase))
                continue;
            CopyDirectory(dir, Path.Combine(dest, name));
        }
    }

    // ── Profile conflict resolution ───────────────────────────────────────────

    /// <summary>
    /// Finds running browser processes that match the resolved executable (or common
    /// Chromium-family names when the executable is unknown) and kills them so the
    /// profile directory lock is released before we launch.
    ///
    /// A brief delay afterwards lets the OS close any remaining file handles.
    /// </summary>
    private async Task KillConflictingBrowserProcessesAsync(string? executablePath)
    {
        // Derive process name(s) to target
        string[] names = executablePath is not null
            ? [Path.GetFileNameWithoutExtension(executablePath)]
            : ["chrome", "msedge", "brave", "chromium", "chromium-browser"];

        var killed = 0;
        foreach (var name in names)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    _logger.LogWarning(
                        "Killing '{Name}' (PID {Pid}) to release profile lock.", name, proc.Id);
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(4_000);
                    killed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not kill '{Name}' PID {Pid}: {Err}",
                        name, proc.Id, ex.Message);
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        if (killed > 0)
        {
            // Give the OS time to flush file handles before we re-open the profile
            await Task.Delay(600);
        }
    }

    /// <summary>
    /// Deletes stale Chrome/Chromium lock files left in <paramref name="userDataDir"/>
    /// after a crash or forceful kill. These prevent a fresh instance from starting.
    /// </summary>
    private void CleanProfileLockFiles(string userDataDir)
    {
        // Files Chrome/Chromium create to prevent concurrent access
        ReadOnlySpan<string> lockFiles =
        [
            "SingletonLock",    // symlink on Linux/macOS; Chrome's primary lock
            "SingletonSocket",  // Unix socket (Linux)
            "SingletonCookie",  // macOS alternative
            "lockfile",         // some Chromium builds
        ];

        foreach (var name in lockFiles)
        {
            var path = Path.Combine(userDataDir, name);
            try
            {
                // File.Exists returns false for broken symlinks — use FileInfo instead
                var info = new FileInfo(path);
                if (info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    File.Delete(path);
                    _logger.LogDebug("Removed stale lock file: {File}", name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not remove lock file '{File}': {Err}", name, ex.Message);
            }
        }
    }

    // ── Guards ───────────────────────────────────────────────────────────────

    private void EnsureAlive()
    {
        if (!_initialized)
            throw new InvalidOperationException(
                "PageActorClient is not initialised. Call InitializeAsync first.");
        if (_crashed)
            throw new InvalidOperationException(
                "Browser has crashed. Call RestartAsync() to recover.");
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _initLock.Dispose();

        if (_page is not null)
        {
            try { await _page.CloseAsync(); } catch { }
            await _page.DisposeAsync();
        }

        if (_browser is not null)
        {
            try { await _browser.CloseAsync(); } catch { }
            await _browser.DisposeAsync();
        }

        if (_tempProfileDir is not null && Directory.Exists(_tempProfileDir))
        {
            try { Directory.Delete(_tempProfileDir, recursive: true); }
            catch { }
        }
    }
}
