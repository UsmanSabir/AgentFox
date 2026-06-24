using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Reflection;

namespace AgentFox.Modules.Loaders;

public class PluginLoader(ServiceProvider pluginCofigProvider)
{
    public List<IAppModule> LoadModules(string pluginFolder)
    {
        var modules = new List<IAppModule>();

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

            foreach (var type in types.Where(t => typeof(IAppModule).IsAssignableFrom(t) && !t.IsAbstract))
            {
                var module = (IAppModule)ActivatorUtilities.CreateInstance(pluginCofigProvider, type)!; //Activator.CreateInstance(type)!;
                modules.Add(module);
            }
        }

        return modules;
    }
}