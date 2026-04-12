using AgentFox.Agents;
using AgentFox.Channels;
using AgentFox.Plugins.Channels;
using AgentFox.Plugins.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentFox.Modules.Webhook;

/// <summary>
/// Module that exposes a generic <c>POST /webhook/{channelName}</c> endpoint.
/// <para>
/// Any channel registered in <see cref="ChannelManagerHolder"/> can receive inbound
/// webhook payloads by overriding <see cref="Channel.ProcessWebhookAsync"/>.  The
/// endpoint accepts the raw request body and all HTTP headers, then delegates parsing
/// and routing to the channel itself.  This keeps the transport concern (HTTP) in this
/// module while the protocol concern (JSON parsing, signature validation, message
/// conversion) stays in the channel implementation.
/// </para>
/// <para>
/// Typical flow for a Telegram webhook:
/// <list type="number">
///   <item>Telegram POSTs an Update JSON to <c>/webhook/telegram</c>.</item>
///   <item><see cref="TelegramChannel.ProcessWebhookAsync"/> parses the payload and
///         calls <see cref="Channel.RaiseMessageReceived"/>.</item>
///   <item><see cref="ChannelManager"/> handles the event, creates or retrieves the
///         session, and enqueues an <c>AgentCommand</c> in the Main lane.</item>
///   <item><see cref="AgentOrchestrator"/>'s command processor executes the agent turn
///         and sends the reply back via <see cref="Channel.SendReplyAsync"/>.</item>
/// </list>
/// </para>
/// </summary>
public class WebhookModule : IAppModule
{
    public string Name => "webhook";

    public void RegisterServices(IServiceCollection services, IConfiguration config) { }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // POST /webhook/{channelName}
        // Returns 200 immediately after handing the payload to the channel.
        // Long-running agent processing happens asynchronously via the command queue.
        endpoints.MapPost("/webhook/{channelName}", async (
            string channelName,
            HttpRequest request,
            ChannelManagerHolder channelManagerHolder,
            ILogger<WebhookModule> logger,
            CancellationToken ct) =>
        {
            // The channel manager may not be ready if the orchestrator is still initializing.
            var manager = channelManagerHolder.Manager;
            if (manager == null)
                return Results.Json(new { error = "Agent not yet initialized. Retry shortly." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var channel = manager.GetChannelByName(channelName);
            if (channel == null)
            {
                logger.LogWarning("Webhook: channel '{ChannelName}' not found.", channelName);
                return Results.NotFound(new { error = $"Channel '{channelName}' is not registered." });
            }

            // Read the raw body
            string body;
            try
            {
                using var reader = new StreamReader(request.Body);
                body = await reader.ReadToEndAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook: failed to read request body for channel '{ChannelName}'.", channelName);
                return Results.Problem("Failed to read request body.", statusCode: StatusCodes.Status400BadRequest);
            }

            // Collect headers as a flat dictionary (last value wins for duplicates)
            var headers = request.Headers
                .ToDictionary(h => h.Key, h => h.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);

            // Delegate to the channel — it owns parsing and routing
            WebhookResult result;
            try
            {
                result = await channel.ProcessWebhookAsync(body, headers, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook: unhandled exception in channel '{ChannelName}'.", channelName);
                return Results.Problem("Channel webhook handler threw an exception.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            if (!result.Supported)
            {
                logger.LogWarning("Webhook: channel '{ChannelName}' does not support webhooks.", channelName);
                return Results.BadRequest(new { error = result.Error });
            }

            if (!result.Accepted)
            {
                logger.LogWarning("Webhook: channel '{ChannelName}' rejected payload: {Error}", channelName, result.Error);
                return Results.UnprocessableEntity(new { error = result.Error });
            }

            // 200 OK — processing continues asynchronously in the command queue
            return Results.Ok(new { accepted = true, channel = channelName });
        });

        // GET /webhook/{channelName}/status — lightweight probe for webhook registration
        endpoints.MapGet("/webhook/{channelName}/status", (
            string channelName,
            ChannelManagerHolder channelManagerHolder) =>
        {
            var manager = channelManagerHolder.Manager;
            if (manager == null)
                return Results.Json(new { status = "initializing" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var channel = manager.GetChannelByName(channelName);
            if (channel == null)
                return Results.NotFound(new { status = "not_found", channel = channelName });

            return Results.Ok(new
            {
                status      = "ready",
                channel     = channelName,
                connected   = channel.IsConnected,
                channelId   = channel.ChannelId,
            });
        });
    }

    public Task StartAsync(IServiceProvider services) => Task.CompletedTask;
}
