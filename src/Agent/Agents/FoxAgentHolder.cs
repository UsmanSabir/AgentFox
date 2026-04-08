using AgentFox.Memory;
using AgentFox.Plugins.Interfaces;
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

    public FoxAgentService(FoxAgentHolder holder) => _holder = holder;

    public async Task<string> RunAsync(
        string input,
        string? conversationId = null,
        CancellationToken ct = default)
    {
        var agent = await _holder.WaitAsync(ct);
        var result = await agent.ProcessAsync(input, conversationId, cancellationToken: ct);
        return result.Output ?? string.Empty;
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

    public PluginContextAdapter(
        ToolRegistry toolRegistry,
        PromptContributorRegistry promptRegistry,
        IConversationStore conversationStore)
    {
        _toolRegistry = toolRegistry;
        _promptRegistry = promptRegistry;
        Conversations = new ConversationReaderAdapter(conversationStore);
    }

    // ── Tool registration ────────────────────────────────────────────────────
    public void RegisterTool(ITool tool) => _toolRegistry.Register(tool);

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
