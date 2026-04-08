using System.Reflection;
using System.Runtime.Loader;

namespace AgentFox.Modules.Loaders;

public class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath)
        : base(isCollectible: true) // allows unloading
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == "AgentFox.Plugins")
        {
            return null; // fallback to Default context
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);

        if (path != null)
            return LoadFromAssemblyPath(path);

        return null;
    }
}