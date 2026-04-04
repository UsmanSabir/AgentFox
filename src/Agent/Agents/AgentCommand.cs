using AgentFox.Models;

namespace AgentFox.Agents;

/// <summary>
/// Bundles the three streaming lifecycle callbacks for a single agent turn.
/// All three fire on the lane task that executes the agent, so they share the
/// same async context and can safely coordinate with each other (e.g. OnComplete
/// can await a Task that OnStart kicked off).
/// </summary>
public sealed class StreamingCallbacks
{
    /// <summary>
    /// Called once immediately before the streaming loop starts.
    /// Typical use: launch the live display (fire-and-forget task) and store the
    /// Task reference so <see cref="OnComplete"/> can await it.
    /// </summary>
    public Func<Task>? OnStart { get; set; }

    /// <summary>
    /// Called for each text token as the LLM produces it.
    /// Typical use: write the token to a channel that the live display reads from.
    /// </summary>
    public Func<string, Task>? OnToken { get; set; }

    /// <summary>
    /// Called in a <c>finally</c> block immediately after the streaming loop ends,
    /// before any post-processing (session save, logging, etc.).
    /// Typical use: close the token channel then await the live display task so the
    /// Spectre.Console exclusive context is released before loggers try to write.
    /// </summary>
    public Func<Task>? OnComplete { get; set; }
}

/// <summary>
/// Represents a command for agent execution.
/// This encapsulates a task that should be processed by an agent.
/// </summary>
public class AgentCommand : ICommand
{
    /// <summary>
    /// Unique identifier for this command execution
    /// </summary>
    public string RunId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Session key identifying the agent and context
    /// Format example: "agent:main-agent-id" or "agent:agent-id:subagent:guid"
    /// </summary>
    public string SessionKey { get; set; } = string.Empty;

    /// <summary>
    /// The lane this command executes in
    /// </summary>
    public CommandLane Lane { get; set; } = CommandLane.Main;

    /// <summary>
    /// Creation timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Priority for execution (0 = lowest, higher = higher priority)
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    /// The actual message/task to execute
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// The agent ID this command targets
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Model to use for this command (if different from agent default)
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Thinking level for this command ("low", "medium", "high")
    /// </summary>
    public string? ThinkingLevel { get; set; }

    /// <summary>
    /// Timeout in seconds for this command execution
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Optional maximum number of tool iterations for this command
    /// </summary>
    public int? MaxIterations { get; set; }

    /// <summary>
    /// Metadata associated with this command for tracking/logging
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// Optional result source for request/reply over the queue.
    /// Set by the caller; the lane handler completes it when execution finishes.
    /// Leave null for fire-and-forget commands (background sub-agents).
    /// </summary>
    public TaskCompletionSource<AgentResult>? ResultSource { get; set; }

    /// <summary>
    /// Optional streaming lifecycle hooks for channels that support live output
    /// (e.g. console/terminal). Leave null for non-streaming channels.
    /// </summary>
    public StreamingCallbacks? Streaming { get; set; }

    /// <summary>
    /// Creates an agent command for the main execution lane
    /// </summary>
    public static AgentCommand CreateMainCommand(
        string sessionKey,
        string agentId,
        string message,
        string? model = null,
        string? thinkingLevel = null)
    {
        return new AgentCommand
        {
            SessionKey = sessionKey,
            AgentId = agentId,
            Lane = CommandLane.Main,
            Message = message,
            Model = model,
            ThinkingLevel = thinkingLevel
        };
    }

    /// <summary>
    /// Creates an agent command for the subagent execution lane
    /// </summary>
    public static AgentCommand CreateSubagentCommand(
        string sessionKey,
        string agentId,
        string message,
        string? model = null,
        string? thinkingLevel = null,
        int? timeoutSeconds = null)
    {
        return new AgentCommand
        {
            SessionKey = sessionKey,
            AgentId = agentId,
            Lane = CommandLane.Subagent,
            Message = message,
            Model = model,
            ThinkingLevel = thinkingLevel,
            TimeoutSeconds = timeoutSeconds
        };
    }
}
