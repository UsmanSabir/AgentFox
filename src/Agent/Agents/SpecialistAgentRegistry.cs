using System.Collections.Concurrent;
using AgentFox.Plugins.Interfaces;

namespace AgentFox.Agents;

/// <summary>Stores plugin descriptors and their host-built isolated runners.</summary>
public sealed class SpecialistAgentRegistry : IAgentRegistry
{
    private readonly ConcurrentDictionary<string, SpecialistAgentDescriptor> _descriptors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Func<string, string?, CancellationToken, Task<string>>> _runners =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ITool>> _tools =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(SpecialistAgentDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.SystemPrompt);
        _descriptors[descriptor.Id] = descriptor;
    }

    public IReadOnlyList<SpecialistAgentDescriptor> GetDescriptors() =>
        _descriptors.Values.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();

    public SpecialistAgentDescriptor? ResolveForChannel(string channelType) =>
        _descriptors.Values.FirstOrDefault(d =>
            d.ChannelTypes.Contains(channelType, StringComparer.OrdinalIgnoreCase));

    public Task<string> RunAsync(
        string agentId,
        string input,
        string? conversationId = null,
        CancellationToken ct = default)
    {
        if (!_runners.TryGetValue(agentId, out var runner))
            throw new InvalidOperationException($"Specialist agent '{agentId}' is not active.");
        return runner(input, conversationId, ct);
    }

    internal void Activate(
        string agentId,
        Func<string, string?, CancellationToken, Task<string>> runner) =>
        _runners[agentId] = runner;

    internal void RegisterTool(string agentId, ITool tool)
    {
        var tools = _tools.GetOrAdd(agentId,
            _ => new ConcurrentDictionary<string, ITool>(StringComparer.OrdinalIgnoreCase));
        tools[tool.Name] = tool;
    }

    internal ITool? GetTool(string agentId, string toolName) =>
        _tools.TryGetValue(agentId, out var tools) && tools.TryGetValue(toolName, out var tool)
            ? tool
            : null;
}
