using AgentFox.Channels;

namespace AgentFox.Agents;

/// <summary>
/// Represents a command to announce a sub-agent result back to the requesting channel/agent
/// Inspired by OpenClaw's result routing mechanism, enabling bidirectional result flow
/// </summary>
public class ResultAnnouncementCommand : ICommand
{
    /// <summary>
    /// Unique identifier for this command execution
    /// </summary>
    public string RunId { get; set; } = Guid.NewGuid().ToString();
    
    /// <summary>
    /// Session key identifying the source of the announcement
    /// Format: "channel:channel-id:message-id" or "agent:agent-id:subagent:guid"
    /// </summary>
    public string SessionKey { get; set; } = string.Empty;
    
    /// <summary>
    /// The lane this command executes in (typically Main for channel announcements)
    /// </summary>
    public CommandLane Lane { get; set; } = CommandLane.Main;
    
    /// <summary>
    /// Creation timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Priority for execution (0 = lowest, higher = higher priority)
    /// Result announcements typically have higher priority than regular commands
    /// </summary>
    public int Priority { get; set; } = 1;
    
    /// <summary>
    /// The sub-agent result being announced
    /// </summary>
    public SubAgentCompletionResult? Result { get; set; }
    
    /// <summary>
    /// The channel to announce the result back to
    /// If null, result is only stored locally (not sent to external channel)
    /// </summary>
    public Channel? RequesterChannel { get; set; }
    
    /// <summary>
    /// Correlation ID linking back to the original request
    /// Enables full tracing of request → sub-agent → response
    /// </summary>
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
    
    /// <summary>
    /// Template for formatting the result announcement
    /// Common placeholders: {status}, {output}, {error}, {duration}, {timestamp}
    /// </summary>
    public string? FormattingTemplate { get; set; }
    
    /// <summary>
    /// Metadata associated with this announcement for tracking
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
    
    /// <summary>
    /// The originating channel message ID (for reference)
    /// </summary>
    public string? OriginatingMessageId { get; set; }
    
    /// <summary>
    /// Indicates whether this announcement should be sent to the channel or only stored
    /// </summary>
    public bool SuppressChannelNotification { get; set; } = false;

    /// <summary>
    /// The session key of the parent agent's active conversation.
    /// When set, the Main lane handler injects the result into the parent agent's context
    /// so the LLM can reason about the completed sub-task on its next turn.
    /// </summary>
    public string? ParentSessionKey { get; set; }

    /// <summary>
    /// The session key of the sub-agent that produced this result.
    /// Included in the parent notification for traceability.
    /// </summary>
    public string? SubAgentSessionKey { get; set; }
    
    /// <summary>
    /// Create a result announcement for sending to a channel
    /// </summary>
    public static ResultAnnouncementCommand CreateChannelAnnouncement(
        SubAgentCompletionResult result,
        Channel channel,
        string originatingMessageId,
        string correlationId,
        string channelId,
        string? formattingTemplate = null)
    {
        return new ResultAnnouncementCommand
        {
            SessionKey = $"channel:{channelId}:{originatingMessageId}",
            Result = result,
            RequesterChannel = channel,
            OriginatingMessageId = originatingMessageId,
            CorrelationId = correlationId,
            FormattingTemplate = formattingTemplate ?? GetDefaultTemplate(result.Status),
            Metadata = new Dictionary<string, string>
            {
                ["announcement_type"] = "channel_result",
                ["channel_id"] = channelId,
                ["result_status"] = result.Status.ToString()
            }
        };
    }
    
    /// <summary>
    /// Create a result announcement that routes back to the parent agent's conversation.
    /// The Main lane handler will inject the formatted result into the parent's session
    /// so the LLM sees it on the next turn.
    /// </summary>
    public static ResultAnnouncementCommand CreateParentAgentAnnouncement(
        SubAgentCompletionResult result,
        string correlationId,
        string parentSessionKey,
        string subAgentSessionKey)
    {
        return new ResultAnnouncementCommand
        {
            SessionKey = parentSessionKey,
            Result = result,
            RequesterChannel = null,
            CorrelationId = correlationId,
            ParentSessionKey = parentSessionKey,
            SubAgentSessionKey = subAgentSessionKey,
            SuppressChannelNotification = true,
            Metadata = new Dictionary<string, string>
            {
                ["announcement_type"] = "parent_agent_result",
                ["result_status"] = result.Status.ToString(),
                ["sub_agent_session"] = subAgentSessionKey
            }
        };
    }

    /// <summary>
    /// Create a result announcement for local storage only (no channel notification)
    /// </summary>
    public static ResultAnnouncementCommand CreateLocalAnnouncement(
        SubAgentCompletionResult result,
        string correlationId,
        string sessionKey)
    {
        return new ResultAnnouncementCommand
        {
            SessionKey = sessionKey,
            Result = result,
            RequesterChannel = null,
            CorrelationId = correlationId,
            SuppressChannelNotification = true,
            Metadata = new Dictionary<string, string>
            {
                ["announcement_type"] = "local_result",
                ["result_status"] = result.Status.ToString()
            }
        };
    }
    
    /// <summary>
    /// Get the default formatting template based on result status
    /// </summary>
    private static string GetDefaultTemplate(SubAgentState status)
    {
        return status switch
        {
            SubAgentState.Completed => "✅ Sub-task completed successfully:\n{output}",
            SubAgentState.Failed => "❌ Sub-task failed:\n{error}",
            SubAgentState.TimedOut => "⏱️ Sub-task timed out after {duration} seconds",
            SubAgentState.Cancelled => "⚠️ Sub-task was cancelled",
            _ => "📝 Sub-task result:\n{output}"
        };
    }
    
    /// <summary>
    /// Format the result message using the template
    /// </summary>
    public string FormatMessage()
    {
        if (Result == null)
            return "No result available";
        
        var template = FormattingTemplate ?? GetDefaultTemplate(Result.Status);
        var duration = Result.Duration?.TotalSeconds ?? 0;
        
        return template
            .Replace("{status}", Result.Status.ToString())
            .Replace("{output}", Result.Output ?? string.Empty)
            .Replace("{error}", Result.Error ?? string.Empty)
            .Replace("{duration}", duration.ToString("F1"))
            .Replace("{timestamp}", DateTime.UtcNow.ToString("O"));
    }
}
