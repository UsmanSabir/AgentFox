using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _concurrencyGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SpecialistMetrics> _metrics =
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

    public async Task<string> RunAsync(
        string agentId,
        string input,
        string? conversationId = null,
        CancellationToken ct = default)
    {
        if (!_runners.TryGetValue(agentId, out var runner))
            throw new InvalidOperationException($"Specialist agent '{agentId}' is not active.");
        var descriptor = _descriptors[agentId];
        var metrics = _metrics.GetOrAdd(agentId, _ => new SpecialistMetrics());
        var gate = _concurrencyGates.GetOrAdd(agentId,
            _ => new SemaphoreSlim(
                Math.Clamp(descriptor.MaxConcurrentTurns, 1, 32),
                Math.Clamp(descriptor.MaxConcurrentTurns, 1, 32)));
        Interlocked.Increment(ref metrics.WaitingTurns);
        try { await gate.WaitAsync(ct); }
        finally { Interlocked.Decrement(ref metrics.WaitingTurns); }
        Interlocked.Increment(ref metrics.ActiveTurns);
        Interlocked.Increment(ref metrics.TotalTurns);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await runner(input, conversationId, ct);
            metrics.LastError = null;
            return result;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref metrics.FailedTurns);
            metrics.LastError = ex.GetType().Name;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            metrics.LastDurationMilliseconds = stopwatch.ElapsedMilliseconds;
            metrics.LastActivityUtc = DateTime.UtcNow;
            Interlocked.Decrement(ref metrics.ActiveTurns);
            gate.Release();
        }
    }

    public IReadOnlyList<SpecialistAgentRuntimeStatus> GetRuntimeStatuses() =>
        _descriptors.Values
            .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(descriptor =>
            {
                var metrics = _metrics.GetOrAdd(descriptor.Id, _ => new SpecialistMetrics());
                return new SpecialistAgentRuntimeStatus(
                    descriptor.Id,
                    descriptor.Name,
                    descriptor.Description,
                    _runners.ContainsKey(descriptor.Id),
                    descriptor.ModelKey,
                    descriptor.ToolNames,
                    descriptor.ChannelTypes,
                    descriptor.RouteHints,
                    descriptor.MaxIterations,
                    descriptor.MaxConcurrentTurns,
                    Volatile.Read(ref metrics.WaitingTurns),
                    Volatile.Read(ref metrics.ActiveTurns),
                    Interlocked.Read(ref metrics.TotalTurns),
                    Interlocked.Read(ref metrics.FailedTurns),
                    metrics.ActivatedUtc,
                    metrics.LastActivityUtc,
                    Interlocked.Read(ref metrics.LastDurationMilliseconds),
                    metrics.LastError);
            })
            .ToList();

    internal void Activate(
        string agentId,
        Func<string, string?, CancellationToken, Task<string>> runner)
    {
        _runners[agentId] = runner;
        _metrics.GetOrAdd(agentId, _ => new SpecialistMetrics()).ActivatedUtc = DateTime.UtcNow;
    }

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

    private sealed class SpecialistMetrics
    {
        public int WaitingTurns;
        public int ActiveTurns;
        public long TotalTurns;
        public long FailedTurns;
        public long LastDurationMilliseconds;
        public DateTime? ActivatedUtc;
        public DateTime? LastActivityUtc;
        public string? LastError;
    }
}

public sealed record SpecialistAgentRuntimeStatus(
    string Id,
    string Name,
    string Description,
    bool IsActive,
    string? ModelKey,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> ChannelTypes,
    IReadOnlyList<string> RouteHints,
    int MaxIterations,
    int MaxConcurrentTurns,
    int WaitingTurns,
    int ActiveTurns,
    long TotalTurns,
    long FailedTurns,
    DateTime? ActivatedUtc,
    DateTime? LastActivityUtc,
    long LastDurationMilliseconds,
    string? LastError);
