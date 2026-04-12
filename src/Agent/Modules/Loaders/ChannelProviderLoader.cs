using AgentFox.Plugins.Channels;

namespace AgentFox.Modules.Loaders;

public class ChannelProviderLoader
{
    public List<Type> LoadProviderTypes(string pluginFolder)
    {
        var providerTypes = new List<Type>();

        foreach (var dll in Directory.GetFiles(pluginFolder, "*.dll"))
        {
            var context = new PluginLoadContext(dll);
            var assembly = context.LoadFromAssemblyPath(dll);

            providerTypes.AddRange(assembly.GetTypes()
                .Where(t => typeof(IChannelProvider).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface));
        }

        return providerTypes;
    }
}
