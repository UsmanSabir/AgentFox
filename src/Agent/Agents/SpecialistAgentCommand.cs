using AgentFox.Plugins.Observability;
namespace AgentFox.Agents;

/// <summary>Queue-native invocation of a persistent plugin specialist.</summary>
public sealed class SpecialistAgentCommand : ICommand
{

    /// <summary>
    /// Captured at CONSTRUCTION, which is the moment still inside the causing call chain —
    /// by the time a lane loop dequeues this the ambient value is long gone. Ensure() adopts
    /// an id already in force and mints one otherwise, so a command raised by a timer or the
    /// heartbeat is still groupable.
    /// </summary>
    public string? CorrelationId { get; init; } = CorrelationContext.Ensure();
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public required string SessionKey { get; init; }
    public CommandLane Lane => CommandLane.Specialist;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public int Priority { get; init; }
    public required string AgentId { get; init; }
    public required string Input { get; init; }
    public int TimeoutSeconds { get; init; } = 300;
    public TaskCompletionSource<string> ResultSource { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
