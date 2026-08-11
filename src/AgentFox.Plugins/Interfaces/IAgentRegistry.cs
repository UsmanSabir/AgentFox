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
