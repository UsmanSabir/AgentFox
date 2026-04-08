using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentFox.Plugins.Interfaces;

public interface IAppModule
{
    string Name { get; }

    void RegisterServices(IServiceCollection services, IConfiguration config);

    void MapEndpoints(IEndpointRouteBuilder endpoints);

    Task StartAsync(IServiceProvider services);
}