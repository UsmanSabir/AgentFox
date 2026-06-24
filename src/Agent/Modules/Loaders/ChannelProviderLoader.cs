using System.Reflection;
using AgentFox.Plugins.Channels;

namespace AgentFox.Modules.Loaders;

public class ChannelProviderLoader
{
    public List<Type> LoadProviderTypes(string pluginFolder)
    {
        var providerTypes = new List<Type>();

        foreach (var dll in Directory.GetFiles(pluginFolder, "*.dll", SearchOption.AllDirectories))
        {
            var context = new PluginLoadContext(dll);
            var assembly = context.LoadFromAssemblyPath(dll);

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }

            providerTypes.AddRange(types
                .Where(t => typeof(IChannelProvider).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface));
        }

        return providerTypes;
    }
}
