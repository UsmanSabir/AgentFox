using AgentFox.MCP;
using AgentFox.Memory;
using AgentFox.Plugins.Models;
using AgentFox.Sessions;
using AgentFox.Skills;
using AgentFox.Tools;
using AgentFox.Agents;
// Alias to avoid ambiguity with AgentFox.Skills.IAgentService (SkillContext.cs)
using IAgentService = AgentFox.Plugins.Interfaces.IAgentService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using AgentFox.Plugins.Interfaces;

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
        // ── Health ────────────────────────────────────────────────────────────
        endpoints.MapGet("/health", () =>
            Results.Ok(new { status = "Ok", timestamp = DateTimeOffset.UtcNow }));

        // ── Status ────────────────────────────────────────────────────────────
        endpoints.MapGet("/status", (FoxAgentHolder holder) =>
        {
            var agent = holder.Agent;
            return Results.Ok(new
            {
                status  = agent?.Status.ToString() ?? "initializing",
                name    = agent?.Name ?? "AgentFox",
                id      = agent?.Id,
                ready   = agent != null,
                uptime  = DateTimeOffset.UtcNow
            });
        });

        // ── Chat (request/response) ───────────────────────────────────────────
        endpoints.MapPost("/chat", async (
            IAgentService agentService,
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

        // ── Chat (SSE streaming) ──────────────────────────────────────────────
        // Emits Server-Sent Events:
        //   data: {"token":"..."}          — one per LLM token
        //   event: done\ndata: {...}       — final event with conversationId
        //   event: error\ndata: {...}      — on failure
        endpoints.MapPost("/chat/stream", async (
            ChatRequest req,
            IAgentService agentService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
            {
                httpContext.Response.StatusCode = 400;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Message must not be empty." }, ct);
                return;
            }

            httpContext.Response.ContentType     = "text/event-stream; charset=utf-8";
            httpContext.Response.Headers.CacheControl  = "no-cache";
            httpContext.Response.Headers.Connection    = "keep-alive";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no"; // disable nginx buffering

            try
            {
                await agentService.StreamAsync(
                    req.Message,
                    req.ConversationId,
                    async token =>
                    {
                        if (ct.IsCancellationRequested) return;
                        var data = JsonSerializer.Serialize(new { token });
                        await httpContext.Response.WriteAsync($"data: {data}\n\n", ct);
                        await httpContext.Response.Body.FlushAsync(ct);
                    },
                    ct);

                // Terminal event
                var donePayload = JsonSerializer.Serialize(new
                {
                    done           = true,
                    conversationId = req.ConversationId
                });
                await httpContext.Response.WriteAsync($"event: done\ndata: {donePayload}\n\n", ct);
                await httpContext.Response.Body.FlushAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected — nothing to write
            }
            catch (Exception ex)
            {
                var errPayload = JsonSerializer.Serialize(new { error = ex.Message });
                try
                {
                    await httpContext.Response.WriteAsync($"event: error\ndata: {errPayload}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
                catch { /* response may already be gone */ }
            }
        });

        // ── Tools ─────────────────────────────────────────────────────────────
        endpoints.MapGet("/tools", (ToolRegistry toolRegistry) =>
        {
            var tools = toolRegistry.GetAll().Select(t => new
            {
                name        = t.Name,
                description = t.Description
            });
            return Results.Ok(tools);
        });

        // ── Skills ────────────────────────────────────────────────────────────
        endpoints.MapGet("/skills", (SkillRegistry skillRegistry) =>
        {
            var manifests = skillRegistry.GetSkillManifests().Select(m => new
            {
                name        = m.Name,
                description = m.Description,
                toolCount   = m.ToolCount,
                skillType   = m.SkillType
            });
            return Results.Ok(manifests);
        });

        // ── Memory ────────────────────────────────────────────────────────────
        endpoints.MapGet("/memory", async (HybridMemory memory, CancellationToken ct) =>
        {
            var entries = await memory.GetAllAsync();
            var result = entries
                .OrderByDescending(e => e.Timestamp)
                .Take(200)
                .Select(e => new
                {
                    id         = e.Id,
                    type       = e.Type.ToString(),
                    content    = e.Content,
                    timestamp  = e.Timestamp,
                    importance = e.Importance
                });
            return Results.Ok(result);
        });

        // ── Sessions ──────────────────────────────────────────────────────────
        endpoints.MapGet("/sessions", (SessionManager sessionManager) =>
        {
            var sessions = sessionManager.GetAllSessions().Select(s => new
            {
                id         = s.SessionId,
                agentId    = s.AgentId,
                origin     = s.Origin.ToString(),
                status     = s.Status.ToString(),
                createdAt  = s.CreatedAt,
                lastActive = s.LastActivityAt,
                channelType = s.ChannelType
            });
            return Results.Ok(sessions);
        });

        // ── MCP Servers ───────────────────────────────────────────────────────
        endpoints.MapGet("/mcp", (McpManager mcpManager) =>
        {
            var connected = mcpManager.GetConnectedServers().Select(s => new
            {
                name      = s.Name,
                toolCount = s.ToolCount,
                tools     = s.ToolNames,
                status    = "connected"
            });

            var failed = mcpManager.Failures.Select(kv => new
            {
                name      = kv.Key,
                toolCount = 0,
                tools     = (IReadOnlyList<string>)Array.Empty<string>(),
                status    = "failed",
                error     = kv.Value
            });

            return Results.Ok(new
            {
                servers      = connected.Cast<object>().Concat(failed.Cast<object>()),
                totalTools   = mcpManager.GetAllTools().Count,
                serverCount  = mcpManager.Servers.Count,
                failureCount = mcpManager.Failures.Count
            });
        });

        // ── Agents (main + sub-agents snapshot) ───────────────────────────────
        endpoints.MapGet("/agents", (FoxAgentHolder holder) =>
        {
            var agent = holder.Agent;
            if (agent == null)
                return Results.Ok(Array.Empty<object>());

            var list = new List<object>
            {
                new
                {
                    id       = agent.Id,
                    name     = agent.Name,
                    status   = agent.Status.ToString(),
                    role     = "main",
                    subAgentCount = agent.SubAgents.Count
                }
            };
            foreach (var sub in agent.SubAgents)
            {
                list.Add(new
                {
                    id     = sub.Config.Id,
                    name   = sub.Config.Name,
                    status = sub.Status.ToString(),
                    role   = "sub"
                });
            }
            return Results.Ok(list);
        });
    }

    public Task StartAsync(IServiceProvider services) => Task.CompletedTask;
}
