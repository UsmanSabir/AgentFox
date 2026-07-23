namespace AgentFox.Plugins.Models;

/// <summary>
/// A safe, user-visible summary of a tool invocation. Detailed arguments/results are
/// intentionally not part of the streaming contract.
/// </summary>
public sealed class AgentToolActivity
{
    public string CallId { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public string Status { get; init; } = "running";
    public long? DurationMs { get; init; }
}
