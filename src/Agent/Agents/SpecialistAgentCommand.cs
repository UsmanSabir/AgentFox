namespace AgentFox.Agents;

/// <summary>Queue-native invocation of a persistent plugin specialist.</summary>
public sealed class SpecialistAgentCommand : ICommand
{
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
