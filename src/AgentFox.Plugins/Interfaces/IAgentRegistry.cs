namespace AgentFox.Plugins.Interfaces;

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
    public string? ModelKey { get; init; }
    public int MaxIterations { get; init; } = 8;
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
