namespace AgentFox.Plugins.Interfaces;

/// <summary>Controls which long-term memory store a specialist agent can access.</summary>
public enum SpecialistMemoryMode
{
    Disabled,
    Shared,
    Isolated
}

/// <summary>Host-neutral declaration of a persistent specialist agent supplied by a plugin.</summary>
public sealed class SpecialistAgentDescriptor
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required string SystemPrompt { get; init; }
    public IReadOnlyList<string> ToolNames { get; init; } = [];
    public IReadOnlyList<string> ChannelTypes { get; init; } = [];
    public IReadOnlyList<string> RouteHints { get; init; } = [];
    /// <summary>High-confidence hints that may force deterministic delegation on the first model turn.</summary>
    public IReadOnlyList<string> StrongRouteHints { get; init; } = [];
    public string? ModelKey { get; init; }
    /// <summary>
    /// Shared uses the main agent's memory; Isolated uses a private persistent store;
    /// Disabled prevents recall and memory-tool access. Defaults to Shared for compatibility.
    /// </summary>
    public SpecialistMemoryMode MemoryMode { get; init; } = SpecialistMemoryMode.Shared;
    public int MaxIterations { get; init; } = 8;
    public int MaxConcurrentTurns { get; init; } = 1;
    /// <summary>
    /// Wall-clock budget for a single specialist turn, enforced by the Specialist lane handler.
    /// Defaults to 300s; specialists that do slow work per turn (browser automation, multiple
    /// external fetches across several tool-call iterations) should raise this explicitly.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 300;
    /// <summary>
    /// Optional per-turn availability of individual tools, keyed by tool name (case-insensitive). A tool with an entry is
    /// asked before EVERY model call: when the answer is false it is left out of the tool list the model is offered and
    /// a call to it anyway is refused by the host. A tool with no entry is always offered, so null (the default) changes
    /// nothing. A predicate that throws counts as false for that tool: unknown is never availability. The host never
    /// caches an answer, so a predicate that does real work must be cheap or cache for itself.
    /// </summary>
    public IReadOnlyDictionary<string, Func<CancellationToken, ValueTask<bool>>>? ToolGates { get; init; }
    /// <summary>
    /// Optional prompt text appended after <see cref="SystemPrompt"/> before every model call, given the names of the tools
    /// that call is actually offered (after <see cref="ToolGates"/>). It lets prose that names a tool appear only while that
    /// tool does. Return null or whitespace for nothing; a throw omits the text for that call.
    /// </summary>
    public Func<IReadOnlySet<string>, string?>? PromptFragment { get; init; }
}

/// <summary>
/// Registry for persistent specialist agents. Plugins register descriptors; the host activates them
/// with isolated runtimes after plugin tool registration completes.
/// </summary>
public interface IAgentRegistry
{
    void Register(SpecialistAgentDescriptor descriptor);
    IReadOnlyList<SpecialistAgentDescriptor> GetDescriptors();
    SpecialistAgentDescriptor? ResolveForChannel(string channelType);
    Task<string> RunAsync(
        string agentId,
        string input,
        string? conversationId = null,
        CancellationToken ct = default);
}
