using AgentFox.Modules.Cli;
using AgentFox.Modules.Web;
using AgentFox.Plugins.Interfaces;

namespace AgentFox.Modules.Loaders;

public class ModuleLoader
{
    public static List<IAppModule> LoadModules()
    {
        return new List<IAppModule>
        {
            new CliModule(),
            new WebModule()
        };
    }
}