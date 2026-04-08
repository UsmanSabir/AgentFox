using AgentFox.Plugins.Interfaces;
using AgentFox.Skills;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentFox.Modules.Web;


public class WebModule : IAppModule
{
    public string Name => "web";

    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        services.AddEndpointsApiExplorer();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", () =>
        {
            return Results.Ok(new { status = "Ok" });
        });

        endpoints.MapPost("/chat", async (IAgentService agent, ChatMessage req) =>
        {
            //TODO
            return Results.Ok(new { Response = $"Response: {req.Message}" });
        });
    }

    public Task StartAsync(IServiceProvider services) => Task.CompletedTask;
}

record ChatMessage(string Message, string ConversationId);