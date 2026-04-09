using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentFox.Modules.Loaders;

public class ToolLoader(IServiceProvider serviceProvider)
{
    public List<ITool> LoadTools(string pluginFolder)
    {
        var tools = new List<ITool>();

        foreach (var dll in Directory.GetFiles(pluginFolder, "*.dll"))
        {
            var context = new PluginLoadContext(dll);
            var assembly = context.LoadFromAssemblyPath(dll);

            var types = assembly.GetTypes()
                .Where(t => typeof(ITool).IsAssignableFrom(t) && !t.IsAbstract);

            foreach (var type in types)
            {
                var tool = (ITool)ActivatorUtilities.CreateInstance(serviceProvider, type);
                tools.Add(tool);
            }
        }

        return tools;
    }
}