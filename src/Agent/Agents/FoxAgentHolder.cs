using AgentFox.Memory;
using AgentFox.Plugins.Interfaces;
using AgentFox.Plugins.Models;
using AgentFox.Tools;

namespace AgentFox.Agents;

/// <summary>
/// Singleton that holds the primary <see cref="FoxAgent"/> once it has been created by
/// <see cref="AgentFox.Modules.Cli.CliWorker"/> (or another initialization service).
/// <para>
/// Other services — e.g. <see cref="FoxAgentService"/> powering the web /chat endpoint —
/// call <see cref="WaitAsync"/> to block until the agent is published, so they can handle
/// requests even before the CLI REPL has printed its first prompt.
/// </para>
/// </summary>
public sealed class FoxAgentHolder
{
    private readonly TaskCompletionSource<FoxAgent> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Called once by CliWorker (or the startup path) after the agent is fully configured.
    /// Subsequent calls are no-ops.
    /// </summary>
    public void Publish(FoxAgent agent) => _tcs.TrySetResult(agent);

    /// <summary>The agent if already published, otherwise null.</summary>
    public FoxAgent? Agent => _tcs.Task.IsCompletedSuccessfully ? _tcs.Task.Result : null;

    /// <summary>Awaitable that completes once <see cref="Publish"/> is called.</summary>
    public Task<FoxAgent> WaitAsync(CancellationToken ct = default) =>
        _tcs.Task.WaitAsync(ct);
}

// ─────────────────────────────────────────────────────────────────────────────
// IAgentService implementation
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Bridges <see cref="IAgentService"/> (the plugin-facing contract) to the live
/// <see cref="FoxAgent"/> held in <see cref="FoxAgentHolder"/>.
/// Registered as a singleton in DI so WebModule and API endpoints can inject it.
/// </summary>
internal sealed class FoxAgentService : IAgentService
{
    private readonly FoxAgentHolder _holder;
    private readonly PendingNotificationStore? _pendingStore;

    public FoxAgentService(FoxAgentHolder holder, PendingNotificationStore? pendingStore = null)
    {
        _holder = holder;
        _pendingStore = pendingStore;
    }

    // A turn (e.g. one blocked on a HITL approval) must survive the caller's HTTP
    // connection dropping mid-wait — it is not the caller's turn to cancel just because
    // a browser tab closed or a proxy timed out. `ct` is honored only for the initial
    // "agent not ready yet" wait and to decide, once the turn finishes, whether the
    // result still has a live connection to be written to or needs to fall back to
    // PendingNotificationStore for the client to pick up on its next poll.

    public Task<AgentReply> RunAsync(
        string input,
        string? conversationId = null,
        CancellationToken ct = default)
        => RunAsync(input, null, conversationId, ct);

    public async Task<AgentReply> RunAsync(
        string input,
        IReadOnlyList<ChatAttachment>? attachments,
        string? conversationId = null,
        CancellationToken ct = default)
    {
        var agent = await _holder.WaitAsync(ct);
        var result = await agent.ProcessAsync(
            input, conversationId, cancellationToken: CancellationToken.None, attachments: attachments);
        var reply = new AgentReply { Output = result.Output ?? string.Empty, References = result.References };

        if (ct.IsCancellationRequested && conversationId != null)
            _pendingStore?.Add(conversationId, reply.Output);

        return reply;
    }

    public Task<AgentReply> StreamAsync(
        string input,
        string? conversationId,
        Func<string, Task> onToken,
        Func<string, Task>? onReasoning = null,
        Func<string, Task>? onStatus = null,
        Func<AgentToolActivity, Task>? onToolActivity = null,
        CancellationToken ct = default)
        => StreamAsync(input, null, conversationId, onToken, onReasoning, onStatus, onToolActivity, ct);

    public async Task<AgentReply> StreamAsync(
        string input,
        IReadOnlyList<ChatAttachment>? attachments,
        string? conversationId,
        Func<string, Task> onToken,
        Func<string, Task>? onReasoning = null,
        Func<string, Task>? onStatus = null,
        Func<AgentToolActivity, Task>? onToolActivity = null,
        CancellationToken ct = default)
    {
        var agent = await _holder.WaitAsync(ct);

        // Once the caller's connection is gone, stop trying to write tokens to it. The
        // execution token is owned by the web-turn coordinator, so a disconnected browser
        // does not cancel a queued/running turn; an explicit Stop/Steer request does.
        async Task SafeOnToken(string token)
        {
            if (ct.IsCancellationRequested) return;
            try { await onToken(token); }
            catch { /* connection gone; the turn keeps running */ }
        }

        async Task SafeOnReasoning(string text)
        {
            if (ct.IsCancellationRequested || onReasoning == null) return;
            try { await onReasoning(text); }
            catch { /* connection gone; the turn keeps running */ }
        }

        async Task SafeOnStatus(string status)
        {
            if (ct.IsCancellationRequested || onStatus == null) return;
            try { await onStatus(status); }
            catch { /* connection gone; the turn keeps running */ }
        }

        async Task SafeOnToolActivity(AgentToolActivity activity)
        {
            if (ct.IsCancellationRequested || onToolActivity == null) return;
            try { await onToolActivity(activity); }
            catch { /* connection gone; the turn keeps running */ }
        }

        var streaming = new StreamingCallbacks
        {
            OnToken = SafeOnToken,
            OnReasoning = SafeOnReasoning,
            OnStatus = SafeOnStatus,
            OnToolActivity = SafeOnToolActivity
        };
        var result = await agent.ProcessAsync(input, conversationId, streaming, ct, attachments);
        var reply = new AgentReply { Output = result.Output ?? string.Empty, References = result.References };

        if (ct.IsCancellationRequested && conversationId != null)
            _pendingStore?.Add(conversationId, reply.Output);

        return reply;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IPluginContext implementation
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Concrete implementation of <see cref="IPluginContext"/> that bridges the
/// plugin-facing contract to the internal agent infrastructure.
/// Created by CliWorker after the agent is built and passed to every
/// <see cref="IAgentAwareModule"/> via <c>OnAgentReadyAsync</c>.
/// </summary>
internal sealed class PluginContextAdapter : IPluginContext
{
    private readonly ToolRegistry _toolRegistry;
    private readonly PromptContributorRegistry _promptRegistry;
    private readonly SpecialistAgentRegistry _agentRegistry;

    public PluginContextAdapter(
        ToolRegistry toolRegistry,
        PromptContributorRegistry promptRegistry,
        IConversationStore conversationStore,
        SpecialistAgentRegistry agentRegistry)
    {
        _toolRegistry = toolRegistry;
        _promptRegistry = promptRegistry;
        _agentRegistry = agentRegistry;
        Conversations = new ConversationReaderAdapter(conversationStore);
    }

    // ── Tool registration ────────────────────────────────────────────────────
    private int _mainToolRegistrations;
    private int _specialistToolRegistrations;

    public void RegisterTool(ITool tool)
    {
        _toolRegistry.Register(tool);
        Interlocked.Increment(ref _mainToolRegistrations);
    }

    public void RegisterAgentTool(string agentId, ITool tool)
    {
        _agentRegistry.RegisterTool(agentId, tool);
        Interlocked.Increment(ref _specialistToolRegistrations);
    }

    /// <summary>
    /// Returns what the last plugin registered and resets the counters, so the caller can report each
    /// module separately. Specialist-scoped tools are counted apart from main-agent tools: they never
    /// enter the main <see cref="ToolRegistry"/>, so a plugin that registers only those (the trading
    /// agent does) otherwise reports "0 tool(s)" and looks broken when it is working exactly as designed.
    /// </summary>
    internal (int Main, int Specialist) TakeRegistrationCounts() =>
        (Interlocked.Exchange(ref _mainToolRegistrations, 0),
         Interlocked.Exchange(ref _specialistToolRegistrations, 0));

    public void RegisterAgent(SpecialistAgentDescriptor descriptor) =>
        _agentRegistry.Register(descriptor);

    // ── Dynamic prompt injection ─────────────────────────────────────────────
    public void ContributeToSystemPrompt(string contributorId, Func<string?> fragmentProvider) =>
        _promptRegistry.Add(new LambdaPromptContributor(contributorId, fragmentProvider));

    public void RemoveSystemPromptContributor(string contributorId) =>
        _promptRegistry.Remove(contributorId);

    // ── Tool hooks ───────────────────────────────────────────────────────────
    public void OnToolPreExecute(Func<string, IDictionary<string, object?>, string, Task> handler) =>
        _toolRegistry.HookRegistry.OnToolPreExecute +=
            (name, args, id) => handler(name, args, id);

    public void OnToolPostExecute(Func<string, string, long, string, Task> handler) =>
        _toolRegistry.HookRegistry.OnToolPostExecute +=
            (name, result, ms, id) => handler(name, result.Output ?? string.Empty, ms, id);

    public void OnToolError(Func<string, string, long, string, Task> handler) =>
        _toolRegistry.HookRegistry.OnToolError +=
            (name, error, ms, id) => handler(name, error, ms, id);

    // ── Skill hooks ──────────────────────────────────────────────────────────
    public void OnSkillEnabled(Func<string, int, Task> handler) =>
        _toolRegistry.HookRegistry.OnSkillPostEnable +=
            (name, count) => handler(name, count);

    public void OnSkillDisabled(Func<string, Task> handler) =>
        _toolRegistry.HookRegistry.OnSkillDisabled +=
            name => handler(name);

    // ── Conversation access ──────────────────────────────────────────────────
    public IPluginConversationAccess Conversations { get; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Lambda prompt contributor
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Wraps a Func&lt;string?&gt; as an <see cref="IPromptContributor"/>.
/// Used by <see cref="PluginContextAdapter.ContributeToSystemPrompt"/>.
/// </summary>
internal sealed class LambdaPromptContributor : IPromptContributor
{
    private readonly Func<string?> _provider;
    public string ContributorId { get; }

    public LambdaPromptContributor(string id, Func<string?> provider)
    {
        ContributorId = id;
        _provider = provider;
    }

    public string? GetFragment() => _provider();
}

// ─────────────────────────────────────────────────────────────────────────────
// IPluginConversationAccess implementation
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class ConversationReaderAdapter : IPluginConversationAccess
{
    private readonly IConversationStore _store;

    public ConversationReaderAdapter(IConversationStore store) => _store = store;

    public IEnumerable<string> GetSessionIds() => _store.GetAllSessionIds();

    public Task<IReadOnlyList<IPluginMessage>> GetMessagesAsync(string sessionId)
    {
        // The current IConversationStore doesn't expose raw message lists —
        // messages live inside AgentSession / ChatHistoryProvider.
        // Return empty for now; implementations backed by MarkdownSessionStore
        // can override this in a future iteration.
        IReadOnlyList<IPluginMessage> empty = Array.Empty<IPluginMessage>();
        return Task.FromResult(empty);
    }
}
