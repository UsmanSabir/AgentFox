using System.Reflection;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentFox.Modules.Loaders;

public class ToolLoader
{
    public List<Type> LoadTools(string pluginFolder)
    {
        var toolTypes = new List<Type>();

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

            foreach (var type in types.Where(t => typeof(ITool).IsAssignableFrom(t) && !t.IsAbstract))
            {
                toolTypes.Add(type);
            }
        }

        return toolTypes;
    }
}