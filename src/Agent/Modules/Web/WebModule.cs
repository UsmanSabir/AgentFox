using AgentFox.Plugins.Interfaces;
using AgentFox.Plugins.Models;
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
            Results.Ok(new { status = "Ok", timestamp = DateTimeOffset.UtcNow }));

        endpoints.MapPost("/chat", async (
            AgentFox.Plugins.Interfaces.IAgentService agentService,
            ChatRequest req,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
                return Results.BadRequest(new ChatResponse
                {
                    Success = false,
                    Error   = "Message must not be empty."
                });

            try
            {
                var reply = await agentService.RunAsync(req.Message, req.ConversationId, ct);
                return Results.Ok(new ChatResponse
                {
                    Response       = reply,
                    ConversationId = req.ConversationId,
                    Success        = true
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new ChatResponse
                {
                    Success = false,
                    Error   = ex.Message
                });
            }
        });
    }

    public Task StartAsync(IServiceProvider services) => Task.CompletedTask;
}
