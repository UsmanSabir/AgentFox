using AgentFox.Models;
using AgentFox.Channels;

namespace AgentFox.Agents;

/// <summary>
/// Represents the current state of a sub-agent execution
/// </summary>
public enum SubAgentState
{
    Pending,
    Running,
    Completed,
    Failed,
    TimedOut,
    Cancelled
}

/// <summary>
/// Encapsulates the execution context and lifecycle management of a sub-agent task
/// </summary>
public class SubAgentTask
{
    /// <summary>
    /// Unique session key for this sub-agent
    /// Format: "agent:parent-agent-id:subagent:guid"
    /// </summary>
    public string SessionKey { get; set; } = string.Empty;
    
    /// <summary>
    /// Unique run/execution ID for this task
    /// </summary>
    public string RunId { get; set; } = Guid.NewGuid().ToString();
    
    /// <summary>
    /// The agent ID of the parent that spawned this sub-agent
    /// </summary>
    public string ParentAgentId { get; set; } = string.Empty;

    /// <summary>
    /// The session key of the parent agent's active conversation.
    /// Used to route the result back into the parent agent's context when complete.
    /// </summary>
    public string ParentSessionKey { get; set; } = string.Empty;
    
    /// <summary>
    /// The ID of the channel that originated this sub-agent task (if spawned from a channel)
    /// Enables result routing back to the requesting channel
    /// </summary>
    public string? OriginatingChannelId { get; set; }
    
    /// <summary>
    /// The channel instance that originated this sub-agent task (if spawned from a channel)
    /// Used to announce results back to the channel
    /// </summary>
    public Channel? OriginatingChannel { get; set; }
    
    /// <summary>
    /// The ID of the message that triggered this sub-agent spawn (if from a channel)
    /// Used for correlation between request and response
    /// </summary>
    public string? OriginatingMessageId { get; set; }
    
    /// <summary>
    /// Unique correlation ID linking the original request to this sub-agent task
    /// Enables tracing of the full request → execute → announce cycle
    /// </summary>
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
    
    /// <summary>
    /// Metadata about the source/requester (user ID, channel type, etc.)
    /// Used for enriching result announcements and audit trails
    /// </summary>
    public Dictionary<string, object> SourceMetadata { get; set; } = new();
    
    /// <summary>
    /// Current state of this sub-agent task
    /// </summary>
    public SubAgentState State { get; set; } = SubAgentState.Pending;
    
    /// <summary>
    /// The task payload/command to execute
    /// </summary>
    public string TaskPayload { get; set; } = string.Empty;
    
    /// <summary>
    /// The model to use for this sub-agent execution
    /// </summary>
    public string? Model { get; set; }
    
    /// <summary>
    /// Thinking level for this execution
    /// </summary>
    public string? ThinkingLevel { get; set; }
    
    /// <summary>
    /// Timeout in seconds for this sub-agent execution
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;
    
    /// <summary>
    /// Maximum number of tool iterations allowed
    /// </summary>
    public int MaxIterations { get; set; } = 10;
    
    /// <summary>
    /// Spawn depth - how many levels deep this sub-agent is
    /// 0 = main agent, 1 = first level sub-agent, etc.
    /// </summary>
    public int SpawnDepth { get; set; } = 0;
    
    /// <summary>
    /// Time when this task was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Time when execution started
    /// </summary>
    public DateTime? StartedAt { get; set; }
    
    /// <summary>
    /// Time when execution completed
    /// </summary>
    public DateTime? CompletedAt { get; set; }
    
    /// <summary>
    /// Cancellation token source for graceful shutdown
    /// </summary>
    public CancellationTokenSource CancellationTokenSource { get; set; } = new();
    
    /// <summary>
    /// Task completion source for awaiting results
    /// </summary>
    public TaskCompletionSource<SubAgentCompletionResult> Completion { get; set; } = new();
    
    /// <summary>
    /// The actual sub-agent instance
    /// </summary>
    public Agent? SubAgent { get; set; }
    
    /// <summary>
    /// Metadata for tracking and correlation
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
    
    /// <summary>
    /// Get the elapsed time since creation
    /// </summary>
    public TimeSpan ElapsedTime => DateTime.UtcNow - CreatedAt;
    
    /// <summary>
    /// Get the execution duration, or null if not completed
    /// </summary>
    public TimeSpan? ExecutionDuration => 
        StartedAt.HasValue && CompletedAt.HasValue 
            ? CompletedAt.Value - StartedAt.Value 
            : null;
    
    /// <summary>
    /// Check if this task has exceeded its timeout
    /// </summary>
    public bool IsTimedOut => ElapsedTime.TotalSeconds > TimeoutSeconds;
    
    /// <summary>
    /// Check if this task is currently active
    /// </summary>
    public bool IsActive => State == SubAgentState.Running || State == SubAgentState.Pending;
}

/// <summary>
/// Result of a sub-agent execution
/// </summary>
public class SubAgentCompletionResult
{
    /// <summary>
    /// Status of the completion (success, failed, timeout, cancelled)
    /// </summary>
    public SubAgentState Status { get; set; } = SubAgentState.Completed;
    
    /// <summary>
    /// The output/result from the sub-agent execution
    /// </summary>
    public string? Output { get; set; }
    
    /// <summary>
    /// Any error message if execution failed
    /// </summary>
    public string? Error { get; set; }
    
    /// <summary>
    /// The agent result from execution
    /// </summary>
    public AgentResult? AgentResult { get; set; }
    
    /// <summary>
    /// Execution duration
    /// </summary>
    public TimeSpan? Duration { get; set; }
    
    /// <summary>
    /// Additional metadata about completion
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
    
    /// <summary>
    /// Create a successful completion result
    /// </summary>
    public static SubAgentCompletionResult Success(string output, AgentResult? agentResult = null)
    {
        return new SubAgentCompletionResult
        {
            Status = SubAgentState.Completed,
            Output = output,
            AgentResult = agentResult
        };
    }
    
    /// <summary>
    /// Create a failed completion result
    /// </summary>
    public static SubAgentCompletionResult Failure(string error)
    {
        return new SubAgentCompletionResult
        {
            Status = SubAgentState.Failed,
            Error = error
        };
    }
    
    /// <summary>
    /// Create a timeout completion result
    /// </summary>
    public static SubAgentCompletionResult Timeout(string message = "Sub-agent execution timed out")
    {
        return new SubAgentCompletionResult
        {
            Status = SubAgentState.TimedOut,
            Error = message
        };
    }
    
    /// <summary>
    /// Create a cancelled completion result
    /// </summary>
    public static SubAgentCompletionResult Cancelled(string message = "Sub-agent execution was cancelled")
    {
        return new SubAgentCompletionResult
        {
            Status = SubAgentState.Cancelled,
            Error = message
        };
    }
}
