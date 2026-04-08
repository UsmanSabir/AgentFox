using AgentFox.Plugins.Interfaces;

namespace AgentFox.Modules.Loaders;

public class ToolLoader
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
                var tool = (ITool)Activator.CreateInstance(type)!;
                tools.Add(tool);
            }
        }

        return tools;
    }
}