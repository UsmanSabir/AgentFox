using AgentFox.Helpers;
using AgentFox.Hitl;
using AgentFox.LLM;
using AgentFox.MCP;
using AgentFox.Memory;
using AgentFox.Plugins.Models;
using AgentFox.Planning;
using AgentFox.Sessions;
using AgentFox.Skills;
using AgentFox.Tools;
using AgentFox.Agents;
// Alias to avoid ambiguity with AgentFox.Skills.IAgentService (SkillContext.cs)
using IAgentService = AgentFox.Plugins.Interfaces.IAgentService;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentFox.Plugins.Interfaces;
using AgentFox.Runtime;

namespace AgentFox.Modules.Web;

public class WebModule : IAppModule
{
    public string Name => "web";

    /// <summary>Schema tag stamped on exported session bundles and required on import.</summary>
    private const string SessionExportSchema = "agentfox.session.v1";

    /// <summary>
    /// camelCase JSON options for SSE payloads, so nested objects (e.g. ResearchReference's
    /// Url/Title/Source) serialize consistently with the rest of the API instead of falling
    /// back to PascalCase under a bare JsonSerializer.Serialize call.
    /// </summary>
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web);

    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        services.AddEndpointsApiExplorer();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // ── Health ────────────────────────────────────────────────────────────
        endpoints.MapGet("/health", () =>
            Results.Ok(new { status = "Ok", version = VersionInfo.Version, timestamp = DateTimeOffset.UtcNow }));

        // ── Version ───────────────────────────────────────────────────────────
        endpoints.MapGet("/version", () =>
            Results.Ok(new
            {
                version = VersionInfo.Version,
                full    = VersionInfo.Full,
                commit  = VersionInfo.Commit,
                display = VersionInfo.Display
            }));

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
                version = VersionInfo.Version,
                uptime  = DateTimeOffset.UtcNow
            });
        });

        // ── Capabilities ──────────────────────────────────────────────────────
        // What the configured model accepts as chat input. The web UI reads this to decide
        // whether to offer the attachment button at all, and which file types to accept.
        endpoints.MapGet("/capabilities", (IConfiguration config) =>
        {
            var caps = AttachmentSupport.Resolve(config);
            return Results.Ok(new
            {
                attachments = new
                {
                    enabled            = caps.Enabled && caps.AnySupported,
                    images             = caps.Images,
                    documents          = caps.Documents,
                    textFiles          = caps.TextFiles,
                    maxFileSizeBytes   = caps.MaxFileSizeBytes,
                    maxFilesPerMessage = caps.MaxFilesPerMessage,
                    maxTotalBytes      = caps.MaxTotalBytes,
                    acceptedMediaTypes = caps.AcceptedMediaTypes,
                    provider           = caps.Provider,
                    model              = caps.Model,
                    source             = caps.Source
                }
            });
        });

        // ── Chat (request/response) ───────────────────────────────────────────

        endpoints.MapPost("/chat", async (
            IAgentService agentService,
            SessionManager sessionManager,
            MarkdownSessionStore sessionStore,
            IConfiguration config,
            ChatRequest req,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
                return Results.BadRequest(new ChatResponse
                {
                    Success = false,
                    Error = "Message must not be empty."
                });

            // Reject the whole turn on an unusable attachment rather than dropping it: a user
            // who attached a screenshot to a text-only model must be told, not left believing
            // the model looked at it.
            if (!AttachmentSupport.TryResolve(req.Attachments, AttachmentSupport.Resolve(config), out _, out var attachmentError))
                return Results.BadRequest(new ChatResponse { Success = false, Error = attachmentError });

            try
            {
                // Pre-generate a conversation ID so the same session is reused across turns.
                // If the client already has one (follow-up message) we keep it; otherwise we
                // mint a new one here and return it so the client can send it on the next turn.
                var conversationId = sessionManager.GetOrCreateWebSession("main", req.ConversationId);
                var reply = await agentService.RunAsync(req.Message, req.Attachments, conversationId, ct);
                return Results.Ok(new ChatResponse
                {
                    Response = reply.Output,
                    ConversationId = conversationId,
                    Success = true,
                    References = reply.References,
                    AssistantIndex = sessionStore.GetLatestAssistantIndex(conversationId)
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
    MarkdownSessionStore sessionStore,
    IConfiguration config,
    WebChatTurnCoordinator turnCoordinator,
    PendingNotificationStore pendingNotifications,
    HttpContext httpContext,
    CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
            {
                httpContext.Response.StatusCode = 400;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Message must not be empty." }, ct);
                return;
            }

            // Same all-or-nothing rule as /chat — see the comment there.
            if (!AttachmentSupport.TryResolve(req.Attachments, AttachmentSupport.Resolve(config), out _, out var attachmentError))
            {
                httpContext.Response.StatusCode = 400;
                await httpContext.Response.WriteAsJsonAsync(new { error = attachmentError }, ct);
                return;
            }

            httpContext.Response.ContentType = "text/event-stream; charset=utf-8";
            httpContext.Response.Headers.CacheControl = "no-cache";
            httpContext.Response.Headers.Connection = "keep-alive";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no"; // disable nginx buffering

            // Opt this response out of server-side buffering. Without this, the ASP.NET
            // pipeline may hold written chunks and only release them when the response
            // completes, so the client receives every token at once at the end instead of
            // as they stream. This is the piece that makes each FlushAsync below actually
            // reach the wire immediately.
            httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            try
            {
                // Pre-generate a conversation ID so the same session is reused across turns.
                var conversationId = sessionManager.GetOrCreateWebSession("main", req.ConversationId);

                async Task WriteEventAsync(string eventName, object payload)
                {
                    if (ct.IsCancellationRequested) return;
                    var data = JsonSerializer.Serialize(payload, SseJsonOptions);
                    await httpContext.Response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }

                // The client must know the session before the agent can block on a
                // first-turn approval. The terminal event remains for compatibility.
                await WriteEventAsync("session", new { conversationId });

                var turn = turnCoordinator.Enqueue(
                    conversationId,
                    async (runId, turnCt) =>
                    {
                        await WriteEventAsync("started", new { runId });
                        var reply = await agentService.StreamAsync(
                            req.Message,
                            req.Attachments,
                            conversationId,
                            async token =>
                            {
                                if (ct.IsCancellationRequested) return;
                                var data = JsonSerializer.Serialize(new { token }, SseJsonOptions);
                                await httpContext.Response.WriteAsync($"data: {data}\n\n", ct);
                                await httpContext.Response.Body.FlushAsync(ct);
                            },
                            reasoning => WriteEventAsync("reasoning", new { text = reasoning }),
                            status => WriteEventAsync("status", new { status }),
                            activity => WriteEventAsync("tool_activity", activity),
                            turnCt);
                        return WebChatTurnResult.Completed(reply);
                    });

                try
                {
                    await WriteEventAsync("queued", new
                    {
                        runId = turn.RunId,
                        position = turn.Position
                    });
                }
                finally
                {
                    // A disconnected browser must not leave a queued turn permanently
                    // parked behind its release gate.
                    turn.Release();
                }

                var result = await turn.Completion.ConfigureAwait(false);
                if (result.State == WebChatTurnState.Interrupted)
                {
                    await WriteEventAsync("interrupted", new { runId = turn.RunId });
                    return;
                }

                if (result.State == WebChatTurnState.Failed)
                {
                    await WriteEventAsync("error", new
                    {
                        runId = turn.RunId,
                        error = result.Error ?? "The turn failed."
                    });
                    return;
                }

                var reply = result.Reply ?? new AgentReply();
                if (ct.IsCancellationRequested)
                    pendingNotifications.Add(conversationId, reply.Output);

                // Terminal event — always includes the conversation ID so the client
                // can store it and send it with the next message.
                var donePayload = JsonSerializer.Serialize(new
                {
                    done = true,
                    runId = turn.RunId,
                    conversationId,
                    references = reply.References,
                    assistantIndex = sessionStore.GetLatestAssistantIndex(conversationId)
                }, SseJsonOptions);
                await httpContext.Response.WriteAsync($"event: done\ndata: {donePayload}\n\n", ct);
                await httpContext.Response.Body.FlushAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected — nothing to write
            }
            catch (Exception ex)
            {
                var errPayload = JsonSerializer.Serialize(new { error = ex.Message }, SseJsonOptions);
                try
                {
                    await httpContext.Response.WriteAsync($"event: error\ndata: {errPayload}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
                catch { /* response may already be gone */ }
            }
        });

        endpoints.MapPost("/chat/steer", (
            WebChatSteerRequest req,
            WebChatTurnCoordinator turnCoordinator) =>
        {
            if (string.IsNullOrWhiteSpace(req.ConversationId) ||
                string.IsNullOrWhiteSpace(req.RunId))
                return Results.BadRequest(new { error = "conversationId and runId are required." });

            return turnCoordinator.Steer(req.ConversationId, req.RunId)
                ? Results.Ok(new { ok = true, runId = req.RunId })
                : Results.NotFound(new { error = "queued_turn_not_found" });
        });

        endpoints.MapPost("/chat/cancel", (
            WebChatCancelRequest req,
            WebChatTurnCoordinator turnCoordinator) =>
        {
            if (string.IsNullOrWhiteSpace(req.ConversationId))
                return Results.BadRequest(new { error = "conversationId is required." });

            return turnCoordinator.CancelActive(req.ConversationId)
                ? Results.Ok(new { ok = true })
                : Results.NotFound(new { error = "active_turn_not_found" });
        });

        endpoints.MapGet("/chat/queue/{conversationId}", (
            string conversationId,
            WebChatTurnCoordinator turnCoordinator) =>
            Results.Ok(turnCoordinator.GetSnapshot(conversationId)));

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

        endpoints.MapDelete("/memory/{id}", async (
            string id,
            HybridMemory memory) =>
        {
            await memory.DeleteAsync(id);
            return Results.Ok(new { deleted = id });
        }).RequireAuthorization("ManagementAdministrator");

        endpoints.MapDelete("/memory", async (HybridMemory memory) =>
        {
            await memory.ClearAsync();
            return Results.Ok(new { cleared = true });
        }).RequireAuthorization("ManagementAdministrator");

        endpoints.MapGet("/memory/settings", (
            MemoryAccessPolicy policy,
            SpecialistAgentRegistry specialists) =>
        {
            var agents = specialists.GetDescriptors()
                .OrderBy(agent => agent.Name)
                .Select(agent =>
                {
                    policy.RegisterAgentMode(agent.Id, agent.MemoryMode);
                    return new
                    {
                        id = agent.Id,
                        name = agent.Name,
                        mode = policy.GetAgentMode(agent.Id).ToString()
                    };
                });
            return Results.Ok(new { globalEnabled = policy.GlobalEnabled, agents });
        });

        endpoints.MapPatch("/memory/settings", (
            GlobalMemorySettingsRequest req,
            MemoryAccessPolicy policy) =>
        {
            policy.SetGlobalEnabled(req.Enabled);
            return Results.Ok(new { globalEnabled = policy.GlobalEnabled });
        }).RequireAuthorization("ManagementAdministrator");

        endpoints.MapPatch("/memory/agents/{agentId}", (
            string agentId,
            SpecialistMemorySettingsRequest req,
            MemoryAccessPolicy policy,
            SpecialistAgentRegistry specialists) =>
        {
            var descriptor = specialists.GetDescriptors()
                .FirstOrDefault(agent => agent.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
            if (descriptor is null)
                return Results.NotFound(new { error = "specialist_agent_not_found" });
            if (!Enum.TryParse<SpecialistMemoryMode>(req.Mode, true, out var mode))
                return Results.BadRequest(new { error = "invalid_memory_mode", allowed = new[] { "Shared", "Isolated", "Disabled" } });

            policy.SetAgentMode(descriptor.Id, mode);
            return Results.Ok(new { agentId = descriptor.Id, mode = mode.ToString() });
        }).RequireAuthorization("ManagementAdministrator");

        // ── Sessions ──────────────────────────────────────────────────────────
        endpoints.MapGet("/sessions", (
            SessionManager sessionManager,
            MemoryAccessPolicy memoryPolicy) =>
        {
            var sessions = sessionManager.GetAllSessions()
                .OrderByDescending(s => s.LastActivityAt)
                .Select(s => new
            {
                id         = s.SessionId,
                title      = s.Title,
                memoryEnabled = memoryPolicy.IsEnabled(s.SessionId),
                memoryOverride = s.MemoryEnabled,
                agentId    = s.AgentId,
                origin     = s.Origin.ToString(),
                status     = s.Status.ToString(),
                createdAt  = s.CreatedAt,
                lastActive = s.LastActivityAt,
                channelType = s.ChannelType,
                forkedFromSessionId = s.ForkedFromSessionId,
                forkedAtAssistantIndex = s.ForkedAtAssistantIndex
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

        endpoints.MapGet("/sessions/{conversationId}/todos", async (
            string conversationId,
            SessionManager sessionManager,
            PlanStateStore planStateStore,
            FoxAgentHolder holder,
            CancellationToken ct) =>
        {
            if (!SessionManager.IsSafeSessionId(conversationId))
                return Results.BadRequest(new { error = "invalid_session_id" });
            if (sessionManager.GetSession(conversationId) is null)
                return Results.NotFound(new { error = "session_not_found" });

            var rawState = sessionManager.ReadProviderState(conversationId);
            var items = ReadTodoItems(rawState).ToList();
            var agent = holder.Agent;
            var liveSession = agent?.ConversationStore.GetSession(conversationId);
            if (agent?.TodoPlannerEnabled == true && liveSession != null)
            {
                try
                {
                    var remaining = await agent.GetRemainingTodosAsync(liveSession, ct);
                    var remainingIds = remaining.Select(item => item.Id.ToString())
                        .ToHashSet(StringComparer.Ordinal);

                    for (var i = 0; i < items.Count; i++)
                        items[i] = items[i] with { Completed = !remainingIds.Contains(items[i].Id) };

                    foreach (var item in remaining)
                    {
                        var id = item.Id.ToString();
                        if (items.All(existing => existing.Id != id))
                            items.Add(new TodoItemSnapshot(id, item.Title, false));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* persisted snapshot remains a safe fallback */ }
            }

            var plan = planStateStore.Peek(conversationId);
            return Results.Ok(new
            {
                enabled = agent?.TodoPlannerEnabled == true || rawState != null,
                phase = plan?.Phase.ToString() ?? "Research",
                plan = plan?.PlanText,
                items,
                remainingCount = items.Count(item => !item.Completed)
            });
        });

        endpoints.MapGet("/sessions/{conversationId}/activity", (
            string conversationId,
            SessionManager sessionManager,
            MarkdownSessionStore sessionStore) =>
        {
            if (!SessionManager.IsSafeSessionId(conversationId))
                return Results.BadRequest(new { error = "invalid_session_id" });
            if (sessionManager.GetSession(conversationId) is null)
                return Results.NotFound(new { error = "session_not_found" });

            var activities = sessionStore.GetConversationToolActivities(conversationId)
                .Select(activity => new
                {
                    callId = activity.CallId,
                    toolName = activity.ToolName,
                    status = activity.Status,
                    durationMs = activity.DurationMs
                });
            return Results.Ok(activities);
        });

        endpoints.MapGet("/sessions/{conversationId}/activity/{callId}", (
            string conversationId,
            string callId,
            SessionManager sessionManager,
            MarkdownSessionStore sessionStore) =>
        {
            if (!SessionManager.IsSafeSessionId(conversationId))
                return Results.BadRequest(new { error = "invalid_session_id" });
            if (sessionManager.GetSession(conversationId) is null)
                return Results.NotFound(new { error = "session_not_found" });

            var activity = sessionStore.GetConversationToolActivities(conversationId)
                .FirstOrDefault(item => string.Equals(item.CallId, callId, StringComparison.Ordinal));
            if (activity is null)
                return Results.NotFound(new { error = "activity_not_found" });

            return Results.Ok(new
            {
                callId = activity.CallId,
                toolName = activity.ToolName,
                status = activity.Status,
                durationMs = activity.DurationMs,
                arguments = RedactToolPayload(activity.Arguments),
                result = RedactToolPayload(activity.Result)
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

        endpoints.MapPost("/sessions/fork", (
            ForkSessionRequest req,
            SessionManager sessionManager) =>
        {
            if (!SessionManager.IsSafeSessionId(req.ConversationId))
                return Results.BadRequest(new { error = "invalid_session_id" });
            if (req.AssistantIndex < 0)
                return Results.BadRequest(new { error = "invalid_assistant_index" });

            try
            {
                var newId = sessionManager.ForkWebSession(
                    req.ConversationId, req.AssistantIndex);
                return Results.Ok(new
                {
                    success = true,
                    conversationId = newId,
                    sourceConversationId = req.ConversationId,
                    assistantIndex = req.AssistantIndex
                });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "session_not_found" });
            }
            catch (ArgumentOutOfRangeException)
            {
                return Results.BadRequest(new { error = "invalid_assistant_index" });
            }
            catch (InvalidOperationException ex) when (ex.Message == "session_busy")
            {
                return Results.Conflict(new { error = "session_busy" });
            }
            catch (InvalidOperationException ex) when (ex.Message == "session_not_web")
            {
                return Results.BadRequest(new { error = "session_not_web" });
            }
            catch (IOException)
            {
                return Results.Conflict(new { error = "session_busy" });
            }
        });

        endpoints.MapPatch("/sessions", (
            RenameSessionRequest req,
            SessionManager sessionManager) =>
        {
            if (!SessionManager.IsSafeSessionId(req.ConversationId))
                return Results.BadRequest(new { error = "invalid_session_id" });
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "empty_session_title" });
            if (req.Title.Trim().Length > SessionManager.MaxSessionTitleLength)
                return Results.BadRequest(new
                {
                    error = "session_title_too_long",
                    maxLength = SessionManager.MaxSessionTitleLength
                });

            return sessionManager.RenameSession(req.ConversationId, req.Title)
                ? Results.Ok(new
                {
                    success = true,
                    conversationId = req.ConversationId,
                    title = req.Title.Trim()
                })
                : Results.NotFound(new { error = "session_not_found" });
        });

        endpoints.MapPatch("/sessions/memory", (
            SessionMemorySettingsRequest req,
            SessionManager sessionManager,
            MemoryAccessPolicy memoryPolicy) =>
        {
            if (!SessionManager.IsSafeSessionId(req.ConversationId))
                return Results.BadRequest(new { error = "invalid_session_id" });

            return sessionManager.SetSessionMemoryEnabled(req.ConversationId, req.Enabled)
                ? Results.Ok(new
                {
                    success = true,
                    conversationId = req.ConversationId,
                    memoryOverride = req.Enabled,
                    memoryEnabled = memoryPolicy.IsEnabled(req.ConversationId)
                })
                : Results.NotFound(new { error = "session_not_found" });
        }).RequireAuthorization("ManagementAdministrator");

        endpoints.MapGet("/session-export", (
            string conversationId,
            SessionManager sessionManager) =>
        {
            if (!SessionManager.IsSafeSessionId(conversationId))
                return Results.BadRequest(new { error = "invalid_session_id" });

            var session = sessionManager.GetSession(conversationId);
            if (session is null)
                return Results.NotFound(new { error = "session_not_found" });

            var transcript = sessionManager.ReadTranscript(conversationId);
            if (transcript is null)
                return Results.NotFound(new { error = "transcript_not_found" });

            // Unfinished todos travel with the bundle so a session can be moved mid-task. Emitted
            // verbatim (savedAt included) — the import side keeps that original timestamp so the
            // agent recognises the work as stale and asks before resuming it.
            System.Text.Json.JsonElement? providerState = null;
            var rawState = sessionManager.ReadProviderState(conversationId);
            if (!string.IsNullOrWhiteSpace(rawState))
            {
                try
                {
                    using var stateDoc = System.Text.Json.JsonDocument.Parse(rawState);
                    providerState = stateDoc.RootElement.Clone();
                }
                catch { /* unreadable sidecar — export the transcript without it */ }
            }

            var envelope = new
            {
                schema     = SessionExportSchema,
                exportedAt = DateTime.UtcNow,
                session    = new
                {
                    title      = session.Title,
                    memoryEnabled = session.MemoryEnabled,
                    agentId    = session.AgentId,
                    origin     = session.Origin.ToString(),
                    createdAt  = session.CreatedAt,
                    lastActive = session.LastActivityAt
                },
                transcriptMarkdown = transcript,
                providerState,
                // Newline-delimited JSON, carried verbatim like the transcript so references stay
                // pinned to the assistant messages they were collected for.
                references = sessionManager.ReadReferences(conversationId)
            };

            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                envelope, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var fileName = conversationId.Replace('/', '_') + ".agentfox.json";
            return Results.File(bytes, "application/json", fileName);
        });

        endpoints.MapPost("/session-import", (
            SessionImportRequest req,
            SessionManager sessionManager) =>
        {
            if (req is null || !string.Equals(req.Schema, SessionExportSchema, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "invalid_schema" });
            if (string.IsNullOrWhiteSpace(req.TranscriptMarkdown))
                return Results.BadRequest(new { error = "empty_transcript" });
            if (req.Session?.Title?.Trim().Length > SessionManager.MaxSessionTitleLength)
                return Results.BadRequest(new
                {
                    error = "session_title_too_long",
                    maxLength = SessionManager.MaxSessionTitleLength
                });

            // Unwrap the optional provider-state envelope. Its contents are NOT trusted here —
            // what may actually be restored is allowlisted when the sidecar is read, so a crafted
            // bundle cannot inject chat history. We only pull out the bag and its original
            // savedAt, which is what makes imported work register as stale.
            System.Text.Json.JsonElement? stateBag = null;
            DateTimeOffset? savedAt = null;
            if (req.ProviderState is { ValueKind: System.Text.Json.JsonValueKind.Object } ps)
            {
                if (ps.TryGetProperty("stateBag", out var bag) &&
                    bag.ValueKind == System.Text.Json.JsonValueKind.Object)
                    stateBag = bag.Clone();

                if (ps.TryGetProperty("savedAt", out var sa) &&
                    sa.ValueKind == System.Text.Json.JsonValueKind.String &&
                    sa.TryGetDateTimeOffset(out var parsed))
                    savedAt = parsed;
            }

            var newId = sessionManager.ImportSession(
                req.Session?.AgentId,
                req.TranscriptMarkdown,
                req.Session?.CreatedAt,
                req.Session?.LastActive,
                req.Session?.Title,
                req.Session?.MemoryEnabled,
                stateBag,
                savedAt,
                req.References);

            return Results.Ok(new { success = true, conversationId = newId });
        });

        endpoints.MapDelete("/sessions", (
            string conversationId,
            SessionManager sessionManager,
            MarkdownSessionStore sessionStore) =>
        {
            if (!SessionManager.IsSafeSessionId(conversationId))
                return Results.BadRequest(new { error = "invalid_session_id" });

            var existed = sessionManager.DeleteSession(conversationId);
            sessionStore.DeleteSession(conversationId); // clear in-memory caches + any residual file

            return existed
                ? Results.Ok(new { success = true, conversationId })
                : Results.NotFound(new { error = "session_not_found" });
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
            MarkdownSessionStore sessionStore,
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
                TimeoutSeconds = descriptor.TimeoutSeconds
            };
            commandQueue.Enqueue(command);

            try
            {
                var reply = await command.ResultSource.Task.WaitAsync(ct);
                return Results.Ok(BuildSpecialistChatResponse(reply, conversationId, sessionStore));
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

        // ── Live conversation events (background sub-agent turns) ────────────
        // A long-lived SSE stream, opened per conversation and independent of any chat turn.
        // Turns triggered by a finishing background sub-agent have no HTTP request of their
        // own to stream into, so without this the browser sees nothing until the next poll
        // delivers one finished blob — while the console watches the same turn token by token.
        // Best-effort only: /chat/pending remains the durable path for a client that is closed
        // or reconnecting, and events carry runKey so a client can drop the polled duplicate.
        endpoints.MapGet("/chat/events/{conversationId}", async (
            string conversationId,
            ConversationEventBus bus,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            httpContext.Response.ContentType = "text/event-stream; charset=utf-8";
            httpContext.Response.Headers.CacheControl = "no-cache";
            httpContext.Response.Headers.Connection = "keep-alive";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no";
            httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            using var subscription = bus.Subscribe(conversationId);

            // The keep-alive ping and the event loop both write to one response body, which is
            // not safe for concurrent writers.
            using var writeLock = new SemaphoreSlim(1, 1);

            async Task WriteRawAsync(string payload)
            {
                await writeLock.WaitAsync(ct);
                try
                {
                    await httpContext.Response.WriteAsync(payload, ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
                finally { writeLock.Release(); }
            }

            // Idle streams are exactly the normal case here — a conversation may wait minutes
            // for its sub-agent — so comment pings keep proxies from reaping the connection.
            var keepAlive = Task.Run(async () =>
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
                    while (await timer.WaitForNextTickAsync(ct))
                        await WriteRawAsync(": ping\n\n");
                }
                catch (OperationCanceledException) { /* client gone */ }
                catch (Exception) { /* response closed mid-write */ }
            }, ct);

            try
            {
                await WriteRawAsync(": connected\n\n");

                await foreach (var evt in subscription.Reader.ReadAllAsync(ct))
                {
                    var data = JsonSerializer.Serialize(evt.Payload, SseJsonOptions);
                    await WriteRawAsync($"event: {evt.Type}\ndata: {data}\n\n");
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected — the subscription is disposed on the way out.
            }
            catch (Exception)
            {
                // Response already torn down; nothing useful to write back.
            }
            finally
            {
                await keepAlive.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        });

        // ── Pending notifications (background sub-agent results) ─────────────
        // Clients poll this after spawning a background sub-agent to receive the
        // result once it arrives. Each call drains the queue (deliver-once).
        // Also reports a HITL approval blocking this session, if any, so the web UI
        // can render Approve/Reject inline instead of just spinning on a blocked turn.
        endpoints.MapGet("/chat/pending/{conversationId}", (
            string conversationId,
            PendingNotificationStore pendingStore,
            HitlManager hitlManager) =>
        {
            var notifications = pendingStore.Drain(conversationId);
            var pendingApproval = hitlManager.GetPendingForSession(conversationId);
            return Results.Ok(new
            {
                conversationId,
                count         = notifications.Count,
                notifications = notifications.Select(n => new
                {
                    message      = n.Message,
                    timestamp    = n.Timestamp,
                    subAgentRunId = n.SubAgentRunId,
                    kind         = n.Kind
                }),
                pendingApproval = pendingApproval == null ? null : new
                {
                    approvalId  = pendingApproval.ApprovalId,
                    trigger     = pendingApproval.Trigger.ToString(),
                    description = pendingApproval.Description,
                    details     = pendingApproval.Details
                }
            });
        });

        // ── HITL approve / reject ─────────────────────────────────────────────
        // Lets any connected surface — not just the channel/console a request
        // originated from — resolve a pending approval. HitlManager.Respond is already
        // channel-agnostic (first response to a given id wins); this just gives the web
        // UI the same one-click action Discord/Telegram buttons and CLI commands have.
        endpoints.MapPost("/hitl/{approvalId}/approve", (
            string approvalId,
            HitlDecisionRequest? body,
            HitlManager hitlManager) =>
            Results.Ok(new { ok = hitlManager.Respond(approvalId, approved: true, body?.Message) }));

        endpoints.MapPost("/hitl/{approvalId}/reject", (
            string approvalId,
            HitlDecisionRequest? body,
            HitlManager hitlManager) =>
            Results.Ok(new { ok = hitlManager.Respond(approvalId, approved: false, body?.Message) }));

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

        endpoints.MapPut("/cron/{name}", (SchedulingHolder scheduling, string name, CronJobUpdateRequest req) =>
        {
            if (!scheduling.IsAvailable) return Results.StatusCode(503);
            if (string.IsNullOrWhiteSpace(req.CronExpression)
                || string.IsNullOrWhiteSpace(req.Task))
                return Results.BadRequest(new { error = "CronExpression and Task are required." });

            var updated = scheduling.CronScheduler!.UpdateJob(name, req.CronExpression, req.Task);
            return updated ? Results.Ok(new { success = true }) : Results.NotFound();
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

    internal static ChatResponse BuildSpecialistChatResponse(
        string reply,
        string conversationId,
        MarkdownSessionStore sessionStore)
    {
        // Specialist commands currently return only their response text. Their research
        // references are persisted by the agent before the command completes, so recover the
        // completed turn from the same projection used when a browser reloads the session.
        var latestAssistant = sessionStore.GetConversationMessages(conversationId)
            .LastOrDefault(message => message.Role == "assistant");

        return new ChatResponse
        {
            Response = reply,
            ConversationId = conversationId,
            Success = true,
            References = latestAssistant?.References.ToList() ?? [],
            AssistantIndex = latestAssistant?.AssistantIndex
        };
    }

    internal static IReadOnlyList<TodoItemSnapshot> ReadTodoItems(string? rawState)
    {
        if (string.IsNullOrWhiteSpace(rawState)) return Array.Empty<TodoItemSnapshot>();
        try
        {
            using var doc = JsonDocument.Parse(rawState);
            var root = doc.RootElement;
            if (root.TryGetProperty("stateBag", out var bag))
                root = bag;
            if (!root.TryGetProperty("TodoProvider", out var provider) ||
                provider.ValueKind != JsonValueKind.Object ||
                !provider.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return Array.Empty<TodoItemSnapshot>();

            return items.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .Select(item => new TodoItemSnapshot(
                    item.TryGetProperty("id", out var id) ? id.ToString() : string.Empty,
                    item.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("isComplete", out var complete) && complete.ValueKind == JsonValueKind.True))
                .Where(item => !string.IsNullOrWhiteSpace(item.Title))
                .ToList();
        }
        catch
        {
            return Array.Empty<TodoItemSnapshot>();
        }
    }

    internal static JsonNode? RedactToolPayload(object? value)
    {
        if (value is null) return null;
        try
        {
            if (value is string text)
            {
                try
                {
                    var parsed = JsonNode.Parse(text);
                    if (parsed != null) return RedactToolNode(parsed);
                }
                catch { /* ordinary text result */ }
                return JsonValue.Create(RedactToolText(text));
            }

            var node = JsonNode.Parse(JsonSerializer.Serialize(value));
            return RedactToolNode(node);
        }
        catch
        {
            return JsonValue.Create(RedactToolText(value.ToString()));
        }
    }

    private static JsonNode? RedactToolNode(JsonNode? node, int depth = 0)
    {
        if (node is null) return null;
        if (depth > 6) return JsonValue.Create("[truncated]");

        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (IsSensitiveToolKey(property.Key))
                    obj[property.Key] = "[redacted]";
                else
                    obj[property.Key] = RedactToolNode(property.Value, depth + 1);
            }
            return obj;
        }

        if (node is JsonArray array)
        {
            while (array.Count > 50)
                array.RemoveAt(array.Count - 1);
            for (var i = 0; i < array.Count; i++)
                array[i] = RedactToolNode(array[i], depth + 1);
            return array;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return JsonValue.Create(TruncateToolText(text));

        return node;
    }

    private static bool IsSensitiveToolKey(string key)
    {
        var normalized = key.Replace("-", string.Empty).Replace("_", string.Empty)
            .Replace(" ", string.Empty).ToLowerInvariant();
        return normalized.Contains("token")
            || normalized.Contains("secret")
            || normalized.Contains("password")
            || normalized.Contains("authorization")
            || normalized.Contains("cookie")
            || normalized.Contains("apikey")
            || normalized.Contains("privatekey")
            || normalized.Contains("environment")
            || normalized == "env";
    }

    private static string TruncateToolText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= 4096 ? text : text[..4096] + "… [truncated]";
    }

    private static string RedactToolText(string? text)
    {
        var truncated = TruncateToolText(text);
        return Regex.Replace(
            truncated,
            @"(?i)\b(authorization|bearer|token|secret|password|api[_-]?key|cookie|private[_-]?key|env)\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s,;]+)",
            "$1=[redacted]",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }
}

// ── Request / response models ─────────────────────────────────────────────────

/// <summary>Optional feedback/reason accompanying a HITL /hitl/{id}/approve|reject call.</summary>
public record HitlDecisionRequest(string? Message);

public record HeartbeatRequest(
    string Name,
    string Task,
    int IntervalSeconds = 60,
    int MaxMissed = 3);

public record ResumeSessionRequest(string ConversationId);

public record ForkSessionRequest(string ConversationId, int AssistantIndex);

public record RenameSessionRequest(string ConversationId, string Title);

public record WebChatSteerRequest(string ConversationId, string RunId);

public record WebChatCancelRequest(string ConversationId);

public record SessionMemorySettingsRequest(string ConversationId, bool? Enabled);

public record GlobalMemorySettingsRequest(bool Enabled);

public record SpecialistMemorySettingsRequest(string Mode);

public record SessionImportRequest(
    string? Schema,
    SessionImportMeta? Session,
    string? TranscriptMarkdown,
    /// <summary>
    /// Optional persisted provider state (the unfinished todo list), shaped like the
    /// <c>.state.json</c> sidecar: <c>{"savedAt":"...","stateBag":{"TodoProvider":{...}}}</c>.
    /// Absent in bundles exported before this field existed, which import unchanged.
    /// </summary>
    System.Text.Json.JsonElement? ProviderState = null,
    /// <summary>
    /// Optional research references as newline-delimited JSON, one
    /// <c>{"i":assistantIndex,"items":[...]}</c> per line. Malformed lines are dropped on import.
    /// Absent in bundles exported before this field existed, which import unchanged.
    /// </summary>
    string? References = null);

public record SessionImportMeta(
    string? Title,
    bool? MemoryEnabled,
    string? AgentId,
    string? Origin,
    DateTime? CreatedAt,
    DateTime? LastActive);

public record TodoItemSnapshot(string Id, string Title, bool Completed);

public record HeartbeatUpdateRequest(
    string? Task = null,
    int? IntervalSeconds = null,
    int? MaxMissed = null);

public record CronJobRequest(
    string Name,
    string CronExpression,
    string Task);

public record CronJobUpdateRequest(
    string CronExpression,
    string Task);
