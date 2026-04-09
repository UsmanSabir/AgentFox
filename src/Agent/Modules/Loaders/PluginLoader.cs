using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace AgentFox.Modules.Loaders;

public class PluginLoader(ServiceProvider pluginCofigProvider)
{
    public List<IAppModule> LoadModules(string pluginFolder)
    {
        var modules = new List<IAppModule>();

        foreach (var dll in Directory.GetFiles(pluginFolder, "*.dll"))
        {
            var context = new PluginLoadContext(dll);

            var assembly = context.LoadFromAssemblyPath(dll);

            var types = assembly.GetTypes()
                .Where(t => typeof(IAppModule).IsAssignableFrom(t) && !t.IsAbstract);

            foreach (var type in types)
            {
                var module = (IAppModule)ActivatorUtilities.CreateInstance(pluginCofigProvider, type)!; //Activator.CreateInstance(type)!;
                modules.Add(module);
            }
        }

        return modules;
    }
}