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
using AgentFox.Runtime;

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
            SessionManager sessionManager,
            ChatRequest req,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
                return Results.BadRequest(new ChatResponse
                {
                    Success = false,
                    Error = "Message must not be empty."
                });

            try
            {
                // Pre-generate a conversation ID so the same session is reused across turns.
                // If the client already has one (follow-up message) we keep it; otherwise we
                // mint a new one here and return it so the client can send it on the next turn.
                var conversationId = sessionManager.GetOrCreateWebSession("main", req.ConversationId);
                var reply = await agentService.RunAsync(req.Message, conversationId, ct);
                return Results.Ok(new ChatResponse
                {
                    Response = reply,
                    ConversationId = conversationId,
                    Success = true
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new ChatResponse
                {
                    Success = false,
                    Error = ex.Message
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
    SessionManager sessionManager,
    HttpContext httpContext,
    CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
            {
                httpContext.Response.StatusCode = 400;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Message must not be empty." }, ct);
                return;
            }

            httpContext.Response.ContentType = "text/event-stream; charset=utf-8";
            httpContext.Response.Headers.CacheControl = "no-cache";
            httpContext.Response.Headers.Connection = "keep-alive";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no"; // disable nginx buffering

            try
            {
                // Pre-generate a conversation ID so the same session is reused across turns.
                var conversationId = sessionManager.GetOrCreateWebSession("main", req.ConversationId);

                await agentService.StreamAsync(
                    req.Message,
                    conversationId,
                    async token =>
                    {
                        if (ct.IsCancellationRequested) return;
                        var data = JsonSerializer.Serialize(new { token });
                        await httpContext.Response.WriteAsync($"data: {data}\n\n", ct);
                        await httpContext.Response.Body.FlushAsync(ct);
                    },
                    ct);

                // Terminal event — always includes the conversation ID so the client
                // can store it and send it with the next message.
                var donePayload = JsonSerializer.Serialize(new
                {
                    done = true,
                    conversationId
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
            var sessions = sessionManager.GetAllSessions()
                .OrderByDescending(s => s.LastActivityAt)
                .Select(s => new
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

        endpoints.MapGet("/session-messages", (
            string conversationId,
            SessionManager sessionManager,
            MarkdownSessionStore sessionStore) =>
        {
            if (!SessionManager.IsSafeSessionId(conversationId))
                return Results.BadRequest(new { error = "invalid_session_id" });

            var session = sessionManager.GetSession(conversationId);
            if (session is null)
                return Results.NotFound(new { error = "session_not_found" });
            if (session.Status == SessionStatus.Archived)
                return Results.Conflict(new { error = "session_archived", message = "Resume the session before loading messages." });

            return Results.Ok(new
            {
                conversationId,
                agentId = session.AgentId,
                messages = sessionStore.GetConversationMessages(conversationId)
            });
        });

        endpoints.MapPost("/sessions/resume", (
            ResumeSessionRequest req,
            SessionManager sessionManager) =>
        {
            if (!SessionManager.IsSafeSessionId(req.ConversationId))
                return Results.BadRequest(new { error = "invalid_session_id" });

            return sessionManager.ResumeSession(req.ConversationId)
                ? Results.Ok(new { success = true, conversationId = req.ConversationId })
                : Results.NotFound(new { error = "session_not_found_or_unavailable" });
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

        endpoints.MapGet("/specialist-agents", (SpecialistAgentRegistry registry) =>
            Results.Ok(registry.GetRuntimeStatuses()));

        endpoints.MapGet("/specialist-agents/{agentId}", (
            string agentId,
            SpecialistAgentRegistry registry) =>
        {
            var status = registry.GetRuntimeStatuses().FirstOrDefault(x =>
                x.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
            return status is null
                ? Results.NotFound(new { error = "specialist_agent_not_found" })
                : Results.Ok(status);
        });

        endpoints.MapPost("/specialist-agents/{agentId}/chat", async (
            string agentId,
            ChatRequest req,
            SpecialistAgentRegistry registry,
            SessionManager sessionManager,
            ICommandQueue commandQueue,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
                return Results.BadRequest(new ChatResponse { Success = false, Error = "Message must not be empty." });

            var descriptor = registry.GetDescriptors().FirstOrDefault(x =>
                x.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
            if (descriptor is null)
                return Results.NotFound(new { error = "specialist_agent_not_found" });

            var prefix = $"specialist/{SessionManager.Sanitize(descriptor.Id)}/";
            var requested = req.ConversationId;
            if (!string.IsNullOrWhiteSpace(requested) &&
                !requested.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "session_agent_mismatch" });

            var conversationId = requested ?? $"{prefix}web_{Guid.NewGuid():N}";
            sessionManager.GetOrCreateWebSession(descriptor.Id, conversationId);

            var command = new SpecialistAgentCommand
            {
                SessionKey = conversationId,
                AgentId = descriptor.Id,
                Input = req.Message,
                TimeoutSeconds = 300
            };
            commandQueue.Enqueue(command);

            try
            {
                var reply = await command.ResultSource.Task.WaitAsync(ct);
                return Results.Ok(new ChatResponse
                {
                    Response = reply,
                    ConversationId = conversationId,
                    Success = true
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Ok(new ChatResponse { Success = false, Error = ex.Message, ConversationId = conversationId });
            }
        });

        endpoints.MapGet("/command-queues", (
            ICommandQueue queue,
            CommandProcessor processor) => Results.Ok(new
        {
            totalQueuedCommands = queue.GetTotalQueueCount(),
            processor = processor.GetStatistics(),
            lanes = processor.GetLaneStatistics(),
            checkedUtc = DateTime.UtcNow
        }));

        // ── Pending notifications (background sub-agent results) ─────────────
        // Clients poll this after spawning a background sub-agent to receive the
        // result once it arrives. Each call drains the queue (deliver-once).
        endpoints.MapGet("/chat/pending/{conversationId}", (
            string conversationId,
            PendingNotificationStore pendingStore) =>
        {
            var notifications = pendingStore.Drain(conversationId);
            return Results.Ok(new
            {
                conversationId,
                count         = notifications.Count,
                notifications = notifications.Select(n => new
                {
                    message      = n.Message,
                    timestamp    = n.Timestamp,
                    subAgentRunId = n.SubAgentRunId
                })
            });
        });

        // ── Channels ─────────────────────────────────────────────────────────

        endpoints.MapGet("/channels", (ChannelManagerHolder channelHolder) =>
        {
            var manager = channelHolder.Manager;
            if (manager == null)
                return Results.Ok(new { ready = false, channels = Array.Empty<object>() });

            var channels = manager.Channels.Values.Select(ch => new
            {
                id          = ch.ChannelId,
                name        = ch.Name,
                type        = ch.Type,
                isConnected = ch.IsConnected,
                status      = ch.IsConnected ? "connected" : "disconnected"
            });

            return Results.Ok(new
            {
                ready    = true,
                channels,
                total     = manager.Channels.Count,
                connected = manager.Channels.Values.Count(c => c.IsConnected)
            });
        });

        // ── Heartbeats ────────────────────────────────────────────────────────

        endpoints.MapGet("/heartbeats", (SchedulingHolder scheduling) =>
        {
            if (!scheduling.IsAvailable)
                return Results.Ok(Array.Empty<object>());

            var beats = scheduling.HeartbeatManager!.GetHeartbeats().Values.Select(b => new
            {
                name            = b.Name,
                task            = b.Task,
                intervalSeconds = b.IntervalSeconds,
                maxMissed       = b.MaxMissed,
                missedCount     = b.MissedCount,
                lastTriggered   = b.LastTriggered,
                isPaused        = b.IsPaused,
                status          = b.IsPaused ? "paused" : "active"
            });
            return Results.Ok(beats);
        });

        endpoints.MapPost("/heartbeats", (SchedulingHolder scheduling, HeartbeatRequest req) =>
        {
            if (!scheduling.IsAvailable)
                return Results.StatusCode(503);
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Task))
                return Results.BadRequest(new { error = "Name and Task are required." });

            scheduling.HeartbeatManager!.AddHeartbeat(
                req.Name, req.Task,
                req.IntervalSeconds > 0 ? req.IntervalSeconds : 60,
                req.MaxMissed > 0        ? req.MaxMissed        : 3);

            return Results.Ok(new { success = true });
        });

        endpoints.MapDelete("/heartbeats/{name}", (SchedulingHolder scheduling, string name) =>
        {
            if (!scheduling.IsAvailable) return Results.StatusCode(503);
            var removed = scheduling.HeartbeatManager!.RemoveHeartbeat(name);
            return removed ? Results.Ok(new { success = true }) : Results.NotFound();
        });

        endpoints.MapPost("/heartbeats/{name}/pause", (SchedulingHolder scheduling, string name) =>
        {
            if (!scheduling.IsAvailable) return Results.StatusCode(503);
            var ok = scheduling.HeartbeatManager!.PauseHeartbeat(name);
            return ok ? Results.Ok(new { success = true }) : Results.NotFound();
        });

        endpoints.MapPost("/heartbeats/{name}/resume", (SchedulingHolder scheduling, string name) =>
        {
            if (!scheduling.IsAvailable) return Results.StatusCode(503);
            var ok = scheduling.HeartbeatManager!.ResumeHeartbeat(name);
            return ok ? Results.Ok(new { success = true }) : Results.NotFound();
        });

        endpoints.MapPost("/heartbeats/{name}/update", (
            SchedulingHolder scheduling, string name, HeartbeatUpdateRequest req) =>
        {
            if (!scheduling.IsAvailable) return Results.StatusCode(503);
            var ok = scheduling.HeartbeatManager!.UpdateHeartbeat(
                name, req.Task, req.IntervalSeconds, req.MaxMissed);
            return ok ? Results.Ok(new { success = true }) : Results.NotFound();
        });

        // ── Cron Jobs ─────────────────────────────────────────────────────────

        endpoints.MapGet("/cron", (SchedulingHolder scheduling) =>
        {
            if (!scheduling.IsAvailable)
                return Results.Ok(Array.Empty<object>());

            var jobs = scheduling.CronScheduler!.GetJobs().Values.Select(j => new
            {
                name           = j.Name,
                cronExpression = j.CronExpression,
                task           = j.Task,
                lastExecuted   = j.LastExecuted == DateTime.MinValue ? (DateTime?)null : j.LastExecuted,
                nextExecution  = j.NextExecution
            });
            return Results.Ok(jobs);
        });

        endpoints.MapPost("/cron", (SchedulingHolder scheduling, CronJobRequest req) =>
        {
            if (!scheduling.IsAvailable) return Results.StatusCode(503);
            if (string.IsNullOrWhiteSpace(req.Name)
                || string.IsNullOrWhiteSpace(req.CronExpression)
                || string.IsNullOrWhiteSpace(req.Task))
                return Results.BadRequest(new { error = "Name, CronExpression and Task are required." });

            scheduling.CronScheduler!.AddJob(req.Name, req.CronExpression, req.Task);
            return Results.Ok(new { success = true });
        });

        endpoints.MapDelete("/cron/{name}", (SchedulingHolder scheduling, string name) =>
        {
            if (!scheduling.IsAvailable) return Results.StatusCode(503);
            var removed = scheduling.CronScheduler!.RemoveJob(name);
            return removed ? Results.Ok(new { success = true }) : Results.NotFound();
        });

        // ── Plugin Sessions (tracking and audit trail) ──────────────────────────

        endpoints.MapGet("/plugin-sessions", (AgentFox.Plugins.PluginSessionStore sessionStore) =>
        {
            var allSessions = sessionStore.GetAllSessions();
            return Results.Ok(allSessions);
        });

        endpoints.MapGet("/plugin-sessions/{pluginName}", (
            string pluginName,
            AgentFox.Plugins.PluginSessionStore sessionStore) =>
        {
            var sessions = sessionStore.GetActiveSessions(pluginName);
            return Results.Ok(sessions);
        });

        endpoints.MapGet("/plugin-sessions/{pluginName}/{sessionId}", (
            string pluginName,
            string sessionId,
            AgentFox.Plugins.PluginSessionStore sessionStore) =>
        {
            var session = sessionStore.GetSession(pluginName, sessionId);
            if (session == null)
                return Results.NotFound(new { error = "Session not found" });

            return Results.Ok(session);
        });

        endpoints.MapGet("/plugin-sessions/{pluginName}/stats", (
            string pluginName,
            AgentFox.Plugins.PluginSessionStore sessionStore) =>
        {
            var stats = sessionStore.GetStats(pluginName);
            return Results.Ok(stats);
        });

        // ── Plugin Configuration (dynamic, updatable from web UI) ───────────────

        endpoints.MapGet("/plugin-config", (
            AgentFox.Plugins.PluginConfigManager configMgr,
            IEnumerable<AgentFox.Plugins.IPluginConfigDefinitionProvider> definitionProviders) =>
        {
            var definitions = definitionProviders.SelectMany(provider => provider.GetDefinitions()).ToList();
            var definedNames = definitions.Select(definition => definition.PluginName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = definitions.Select(definition => BuildPluginConfigResponse(configMgr, definition))
                .Cast<object>()
                .Concat(configMgr.GetAllConfigs()
                    .Where(config => !definedNames.Contains(config.PluginName))
                    .Cast<object>());
            return Results.Ok(result);
        });

        endpoints.MapGet("/plugin-config/{pluginName}", (
            string pluginName,
            AgentFox.Plugins.PluginConfigManager configMgr,
            IEnumerable<AgentFox.Plugins.IPluginConfigDefinitionProvider> definitionProviders) =>
        {
            var definition = definitionProviders.SelectMany(provider => provider.GetDefinitions())
                .FirstOrDefault(item => item.PluginName.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
            var config = definition is null
                ? configMgr.GetConfigWithSchema(pluginName)
                : BuildPluginConfigResponse(configMgr, definition);
            return Results.Ok(config);
        });

        endpoints.MapPost("/plugin-config/{pluginName}", async (
            string pluginName,
            AgentFox.Plugins.PluginConfigUpdateRequest req,
            AgentFox.Plugins.PluginConfigManager configMgr,
            IEnumerable<AgentFox.Plugins.IPluginConfigDefinitionProvider> definitionProviders) =>
        {
            if (req.Config == null || req.Config.Count == 0)
                return Results.BadRequest(new { error = "Config object cannot be empty" });

            var definition = definitionProviders.SelectMany(provider => provider.GetDefinitions())
                .FirstOrDefault(item => item.PluginName.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
            var config = SanitizeIncomingConfig(definition, req.Config, configMgr.GetConfig(pluginName));
            if (config.Count == 0)
                return Results.BadRequest(new { error = "No editable configuration values in request" });

            bool success;
            if (req.Merge)
                success = await configMgr.MergeConfigAsync(pluginName, config);
            else
                success = await configMgr.SaveConfigAsync(pluginName, config);

            if (!success)
                return Results.StatusCode(500);

            return Results.Ok(new { success = true, message = "Configuration updated" });
        }).RequireAuthorization("ManagementAdministrator");

        endpoints.MapDelete("/plugin-config/{pluginName}", (
            string pluginName,
            AgentFox.Plugins.PluginConfigManager configMgr) =>
        {
            var deleted = configMgr.DeleteConfig(pluginName);
            return deleted ? Results.Ok(new { success = true }) : Results.NotFound();
        }).RequireAuthorization("ManagementAdministrator");
    }

    private static object BuildPluginConfigResponse(
        AgentFox.Plugins.PluginConfigManager configManager,
        AgentFox.Plugins.PluginConfigDefinition definition)
    {
        var stored = configManager.GetConfigWithSchema(definition.PluginName);
        var effective = definition.Fields.ToDictionary(
            field => field.Key,
            field => stored.Config.TryGetValue(field.Key, out var value) ? value : field.DefaultValue);
        foreach (var item in stored.Config)
            effective.TryAdd(item.Key, item.Value);

        // Never send stored secrets to the browser — replace with the mask placeholder,
        // which SanitizeIncomingConfig recognizes on the way back as "unchanged".
        foreach (var field in definition.Fields.Where(f => f.Sensitive))
        {
            if (effective.TryGetValue(field.Key, out var value) && value is string { Length: > 0 })
                effective[field.Key] = AgentFox.Plugins.PluginConfigSecrets.Mask;
        }

        return new
        {
            pluginName = definition.PluginName,
            displayName = definition.DisplayName,
            description = definition.Description,
            config = effective,
            fields = definition.Fields,
            lastUpdatedAt = stored.LastUpdatedAt,
            isDefault = stored.IsDefault
        };
    }

    /// <summary>
    /// For plugins with a config definition, keeps only defined, runtime-editable fields and
    /// resolves the sensitive-field mask placeholder back to the stored value (i.e. "unchanged").
    /// Plugins without a definition are schema-less and pass through untouched.
    /// </summary>
    private static Dictionary<string, object?> SanitizeIncomingConfig(
        AgentFox.Plugins.PluginConfigDefinition? definition,
        Dictionary<string, object?> incoming,
        Dictionary<string, object?> stored)
    {
        if (definition is null)
            return incoming;

        var fields = definition.Fields
            .GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, object?>();
        foreach (var (key, value) in incoming)
        {
            if (!fields.TryGetValue(key, out var field) || !field.RuntimeEditable)
                continue;

            if (field.Sensitive && IsSecretMask(value))
            {
                // Round-tripped placeholder: keep whatever is stored (explicit copy so the
                // secret survives a non-merge save too).
                if (stored.TryGetValue(key, out var existing))
                    result[key] = existing;
                continue;
            }

            result[key] = value;
        }
        return result;
    }

    // Request-bound dictionary values arrive as JsonElement, not string.
    private static bool IsSecretMask(object? value) => value switch
    {
        string s => s == AgentFox.Plugins.PluginConfigSecrets.Mask,
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } je =>
            je.GetString() == AgentFox.Plugins.PluginConfigSecrets.Mask,
        _ => false
    };

    public Task StartAsync(IServiceProvider services) => Task.CompletedTask;
}

// ── Request / response models ─────────────────────────────────────────────────

public record HeartbeatRequest(
    string Name,
    string Task,
    int IntervalSeconds = 60,
    int MaxMissed = 3);

public record ResumeSessionRequest(string ConversationId);

public record HeartbeatUpdateRequest(
    string? Task = null,
    int? IntervalSeconds = null,
    int? MaxMissed = null);

public record CronJobRequest(
    string Name,
    string CronExpression,
    string Task);
