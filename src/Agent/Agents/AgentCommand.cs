using AgentFox.Models;

namespace AgentFox.Agents;

/// <summary>
/// Represents a command for agent execution
/// This encapsulates a task that should be processed by an agent
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
