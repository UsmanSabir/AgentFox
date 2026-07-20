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

    protected override Assembly Load(AssemblyName assemblyName)
    {
        // Share host contracts and common framework libraries rather than loading duplicates
        if (assemblyName.Name == "AgentFox.Plugins"
            || assemblyName.Name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
            || assemblyName.Name == "Newtonsoft.Json"
            || assemblyName.Name.StartsWith("Polly", StringComparison.Ordinal))
        {
            return null; // fallback to Default context
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);

        if (path != null)
            return LoadFromAssemblyPath(path);

        return null;
    }
}