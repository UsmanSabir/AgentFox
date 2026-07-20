using AgentFox.Plugins.Interfaces;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentFox.Modules.Cli;

public class CliModule : IAppModule
{
    public string Name => "cli";

    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        services.AddHostedService<CliWorker>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }

    public Task StartAsync(IServiceProvider services)
    {
        return Task.CompletedTask;
    }
}