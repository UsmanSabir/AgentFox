using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageAgent.Config;
using PageAgent.Models;
using PuppeteerSharp;

namespace PageAgent.Core;

/// <summary>
/// Wraps PuppeteerSharp to provide high-level, human-like browser interactions:
/// navigation, page analysis, content extraction, and smart element clicking.
///
/// One instance = one browser session. Create a fresh instance per agent run
/// and dispose when finished (<see cref="IAsyncDisposable"/>).
/// </summary>
public sealed class PageActorClient : IAsyncDisposable
{
    private readonly BrowserAgentOptions _options;
    private readonly ILogger<PageActorClient> _logger;

    private IBrowser? _browser;
    private IPage? _page;
    private bool _initialized;
    private string? _tempProfileDir;   // non-null when we created a profile copy
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>Current page URL reported by the browser.</summary>
    public string CurrentUrl => _page?.Url ?? "about:blank";

    public PageActorClient(IOptions<BrowserAgentOptions> options, ILogger<PageActorClient> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches the browser. Prefers a system-installed Chromium/Chrome/Edge;
    /// downloads Chromium automatically when no system browser is found.
    /// Safe to call multiple times (idempotent after the first call).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var executablePath = BrowserSystemDetector.ExecutablePath;

            if (executablePath is null)
            {
                _logger.LogInformation(
                    "No system browser found. Downloading Chromium (this may take a moment)...");
                var fetcher = new BrowserFetcher();
                await fetcher.DownloadAsync();
                _logger.LogInformation("Chromium download complete.");
            }
            else
            {
                _logger.LogInformation("Using system browser: {Path}", executablePath);
            }

            var userDataDir = ResolveUserDataDir();

            var launchOptions = new LaunchOptions
            {
                Headless = _options.Headless,
                ExecutablePath = executablePath,  // null → use PuppeteerSharp's downloaded copy
                UserDataDir = userDataDir,        // null → ephemeral profile
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
            _page = await _browser.NewPageAsync();

            await _page.SetViewportAsync(new ViewPortOptions
            {
                Width = _options.ViewportWidth,
                Height = _options.ViewportHeight,
            });

            await _page.SetUserAgentAsync(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");

            // Suppress console noise from pages
            _page.Console += (_, e) =>
                _logger.LogTrace("[browser-console] {Type}: {Text}", e.Message.Type, e.Message.Text);

            _initialized = true;
            _logger.LogDebug("Browser session ready.");
        }
        finally
        {
            _initLock.Release();
        }
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
        EnsureInitialized();
        try
        {
            var title = await _page!.GetTitleAsync();

            var headingsJson = await _page.EvaluateExpressionAsync<string>(
                DomAnalyzer.HeadingsScript(_options.MaxHeadings));

            var linksJson = await _page.EvaluateExpressionAsync<string>(
                DomAnalyzer.LinksScript(_options.MaxLinks));

            var headings = DomAnalyzer.DeserialiseStrings(headingsJson);
            var rawLinks = DomAnalyzer.DeserialiseLinks(linksJson);
            var links = rawLinks
                .Select(l => new LinkInfo { Text = l.Text, Href = l.Href })
                .ToList();

            _logger.LogDebug("Analyzed page: \"{Title}\" — {H} headings, {L} links",
                title, headings.Count, links.Count);

            return new PageAnalysis
            {
                Title = title,
                Url = _page.Url,
                Headings = headings,
                Links = links,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Page analysis failed: {Error}", ex.Message);
            return new PageAnalysis { Url = _page!.Url, Error = ex.Message };
        }
    }

    /// <summary>
    /// Extracts the visible text of the current page, stripping navigation,
    /// footers, scripts, and ads. Content is truncated to <c>MaxExtractLength</c>.
    /// </summary>
    public async Task<string> ExtractContentAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        try
        {
            var text = await _page!.EvaluateExpressionAsync<string>(
                DomAnalyzer.TextContentScript(_options.MaxExtractLength));

            _logger.LogDebug("Extracted {Chars} characters from {Url}", text?.Length ?? 0, _page.Url);
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
    /// Waits briefly for any resulting navigation to settle.
    /// </summary>
    public async Task<string> SmartClickAsync(string targetText, CancellationToken ct = default)
    {
        EnsureInitialized();
        try
        {
            var result = await _page!.EvaluateExpressionAsync<string>(
                DomAnalyzer.SmartClickScript(targetText));

            if (result?.StartsWith("clicked:", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogInformation("Clicked element: {Label}", result[8..]);

                // Best-effort wait — navigation may or may not occur after a click
                try
                {
                    await _page.WaitForNavigationAsync(new NavigationOptions
                    {
                        WaitUntil = [WaitUntilNavigation.Networkidle2],
                        Timeout = 6_000,
                    });
                }
                catch (TimeoutException)
                {
                    // No full navigation triggered; that's fine (SPA route, modal, etc.)
                }

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

    // ── Internal helpers ─────────────────────────────────────────────────────

    private async Task<string> NavigateAsync(string url, CancellationToken ct)
    {
        EnsureInitialized();

        Exception? lastEx = null;
        for (int attempt = 1; attempt <= _options.RetryAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _page!.GoToAsync(url, new NavigationOptions
                {
                    WaitUntil = [WaitUntilNavigation.Networkidle2],
                    Timeout = _options.NavigationTimeoutMs,
                });

                _logger.LogDebug("Navigated to: {Url}", _page.Url);
                return $"Navigated to: {_page.Url}";
            }
            catch (Exception ex) when (attempt < _options.RetryAttempts)
            {
                lastEx = ex;
                _logger.LogWarning(
                    "Navigation attempt {Attempt}/{Max} failed for {Url}: {Error}",
                    attempt, _options.RetryAttempts, url, ex.Message);

                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }

        // Final attempt — let the exception propagate
        await _page!.GoToAsync(url, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.Networkidle2],
            Timeout = _options.NavigationTimeoutMs,
        });

        return $"Navigated to: {_page.Url}";
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException(
                "PageActorClient is not initialised. Call InitializeAsync first.");
    }

    /// <summary>
    /// Resolves which user-data directory (if any) to pass to the browser.
    /// Priority: explicit config → auto-detect system profile → none (ephemeral).
    /// When <c>UseProfileCopy</c> is true, copies the directory to a temp path first.
    /// </summary>
    private string? ResolveUserDataDir()
    {
        // 1. Explicit path from config
        string? sourceDir = _options.UserDataDir;

        // 2. Auto-detect the default profile for the system browser
        if (sourceDir is null && _options.UseSystemProfile)
        {
            sourceDir = BrowserSystemDetector.DefaultUserDataDir;
            if (sourceDir is not null)
                _logger.LogInformation("Auto-detected system profile: {Dir}", sourceDir);
        }

        if (sourceDir is null)
            return null;   // no profile → ephemeral session

        if (!Directory.Exists(sourceDir))
        {
            _logger.LogWarning(
                "UserDataDir '{Dir}' does not exist — launching with ephemeral profile.", sourceDir);
            return null;
        }

        // 3. Optional: copy to temp dir so the original browser can stay running
        if (_options.UseProfileCopy)
        {
            _tempProfileDir = Path.Combine(Path.GetTempPath(), "PageAgent_" + Guid.NewGuid().ToString("N"));
            _logger.LogInformation(
                "Copying profile to temp dir (UseProfileCopy=true): {Temp}", _tempProfileDir);
            CopyDirectory(sourceDir, _tempProfileDir);
            return _tempProfileDir;
        }

        _logger.LogInformation(
            "Using existing browser profile: {Dir} — close any running browser windows first.",
            sourceDir);
        return sourceDir;
    }

    /// <summary>Recursively copies a directory tree, skipping locked/inaccessible files.</summary>
    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            try { File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true); }
            catch { /* skip locked files (e.g. LevelDB lock) */ }
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            // Skip the cache — it's large and not needed for auth
            if (name.Equals("Cache", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Code Cache", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("GPUCache", StringComparison.OrdinalIgnoreCase))
                continue;
            CopyDirectory(dir, Path.Combine(dest, name));
        }
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _initLock.Dispose();

        if (_page is not null)
        {
            try { await _page.CloseAsync(); } catch { /* best-effort */ }
            await _page.DisposeAsync();
        }

        if (_browser is not null)
        {
            try { await _browser.CloseAsync(); } catch { /* best-effort */ }
            await _browser.DisposeAsync();
        }

        // Clean up profile copy (if we made one)
        if (_tempProfileDir is not null && Directory.Exists(_tempProfileDir))
        {
            try { Directory.Delete(_tempProfileDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}
