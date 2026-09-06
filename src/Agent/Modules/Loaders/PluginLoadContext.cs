using System.Reflection;
using System.Runtime.Loader;

namespace AgentFox.Modules.Loaders;

public class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath)
        // Non-collectible on purpose. Plugins are loaded once at startup and never unloaded (nothing
        // calls Unload()). A COLLECTIBLE context can transition to an "unloading" state, and because
        // plugins load some dependencies LAZILY (PuppeteerSharp pulls in WebDriverBiDi only at
        // Puppeteer.LaunchAsync, the first time an order is placed), that later load then throws
        // "AssemblyLoadContext is unloading or was already unloaded". Non-collectible removes the
        // entire failure mode; the small memory cost is irrelevant since these live for the app's life.
        : base(isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    /// <summary>
    /// Whether an assembly is resolved from the HOST's default load context rather than from the
    /// plugin folder — i.e. host and plugin share one copy and therefore one type identity.
    ///
    /// <para>
    /// Extracted as a static predicate so it can be asserted on directly. The decision is a pure
    /// function of a name, but <see cref="Load"/> is protected and its instance needs a real
    /// plugin path and an <see cref="AssemblyDependencyResolver"/>, which put a rule with real
    /// safety consequences out of reach of any test.
    /// </para>
    /// </summary>
    public static bool IsSharedWithHost(string? assemblyName) =>
        assemblyName is not null
        && (assemblyName == "AgentFox.Plugins"
            || assemblyName.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
            || assemblyName == "System.Diagnostics.DiagnosticSource"
            || assemblyName == "Newtonsoft.Json"
            || assemblyName.StartsWith("Polly", StringComparison.Ordinal));

    protected override Assembly Load(AssemblyName assemblyName)
    {
        // Share host contracts and common framework libraries rather than loading duplicates
        //
        // System.Diagnostics.DiagnosticSource is here for a reason that is invisible at every call
        // site: it defines ActivitySource and Activity, and an ActivityListener only sees spans
        // from the SAME type identity it was registered against. Today that assembly ships in the
        // shared framework and resolves once, so plugin spans reach the host's listener. But the
        // moment any plugin takes a PackageReference on OpenTelemetry.Api or on DiagnosticSource
        // itself, the DLL lands beside the plugin, AssemblyDependencyResolver finds it locally, and
        // the plugin gets a second copy — after which every plugin span is created and silently
        // never collected, with nothing failing and nothing logged. Delegating by name removes the
        // failure mode rather than relying on nobody adding that reference.
        if (IsSharedWithHost(assemblyName.Name))
        {
            return null; // fallback to Default context
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);

        if (path != null)
            return LoadFromAssemblyPath(path);

        return null;
    }
}