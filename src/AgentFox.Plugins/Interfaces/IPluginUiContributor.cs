using Microsoft.Extensions.FileProviders;

namespace AgentFox.Plugins.Interfaces;

/// <summary>
/// Lets a plugin ship its own web UI without the host frontend knowing anything about it.
///
/// <para>
/// The host does three generic things with what this returns: lists the pages at
/// <c>GET /api/plugin-ui</c> (so the sidebar can show them), serves each page's
/// <see cref="PluginUiPage.Assets"/> as static files under <c>/ext/{slug}/</c>, and renders a
/// generic route that hosts the page. It never learns what the page is *about* — no host-side
/// route, type, API client, or npm dependency is added per plugin.
/// </para>
///
/// <para>
/// Implement it on the module itself (or register a separate singleton) and return a
/// <see cref="ManifestEmbeddedFileProvider"/> over the plugin assembly's own embedded
/// <c>wwwroot</c>, so the UI travels inside the plugin DLL exactly as the host's own SPA travels
/// inside the host assembly.
/// </para>
/// </summary>
public interface IPluginUiContributor
{
    IEnumerable<PluginUiPage> GetPages();
}

/// <summary>URL layout the host uses for plugin pages. A plugin's bundler must know the asset prefix.</summary>
public static class PluginUiPaths
{
    /// <summary>
    /// Where a page's <see cref="PluginUiPage.Assets"/> are served: <c>/plugin-assets/{slug}/…</c>.
    /// A plugin's frontend build must use this as its base URL (Vite <c>base</c>, webpack
    /// <c>publicPath</c>), because every asset reference inside its bundle is resolved against it.
    /// </summary>
    public const string AssetPrefix = "/plugin-assets";

    /// <summary>
    /// Where the HOST renders the page: <c>/ext/{slug}</c>. Deliberately a different prefix from
    /// <see cref="AssetPrefix"/> — serving assets under the same path would let the static-file
    /// middleware answer <c>/ext/{slug}</c> with the plugin's raw document, bypassing the host's
    /// navigation and header entirely.
    /// </summary>
    public const string PagePrefix = "/ext";

    public static string AssetPathFor(string slug) => $"{AssetPrefix}/{slug}";
    public static string PagePathFor(string slug)  => $"{PagePrefix}/{slug}";
}

/// <summary>One plugin-supplied page, mounted by the host at <c>/ext/{Slug}</c>.</summary>
public sealed class PluginUiPage
{
    /// <summary>
    /// URL segment for the page: <c>"trading"</c> is served at <c>/ext/trading/</c> and navigated to
    /// at <c>/ext/trading</c>. Must be a single lowercase path segment — the host rejects anything
    /// containing a slash or path traversal, since it is used to build a static-file request path.
    /// </summary>
    public required string Slug { get; init; }

    /// <summary>Sidebar label.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Assets for this page: the built HTML/JS/CSS bundle. Served UNAUTHENTICATED, exactly like the
    /// host's own <c>wwwroot</c> — put no secrets here. The page authenticates its own data calls to
    /// <c>/api/...</c> with the management API key, which the host hands it at load time.
    /// </summary>
    public required IFileProvider Assets { get; init; }

    /// <summary>
    /// Icon name from the frontend's icon set (lucide), resolved host-side; unknown names fall back
    /// to a generic plugin icon. A name rather than an SVG, so the plugin ships no markup into the
    /// host's DOM.
    /// </summary>
    public string Icon { get; init; } = "puzzle";

    /// <summary>Entry document within <see cref="Assets"/>.</summary>
    public string EntryPath { get; init; } = "index.html";

    /// <summary>Optional one-line description for the plugins overview.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Management role required to SEE this page in the navigation (the assets themselves are public,
    /// and the plugin's own API endpoints do the real authorization). Defaults to the lowest role.
    /// </summary>
    public string RequiredRole { get; init; } = "Viewer";

    /// <summary>Sort hint within the plugin section of the sidebar; lower comes first.</summary>
    public int Order { get; init; } = 100;
}
