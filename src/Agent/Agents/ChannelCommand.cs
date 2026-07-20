using AgentFox.Plugins.Channels;

namespace AgentFox.Agents;

/// <summary>
/// Represents a command originated from a channel message
/// Implements ICommand to integrate channel messages with the command lane system
/// </summary>
public class ChannelCommand : ICommand
{
    /// <summary>
    /// Unique identifier for this command execution
    /// </summary>
    public string RunId { get; set; } = Guid.NewGuid().ToString();
    
    /// <summary>
    /// Session key identifying the agent session
    /// Format: "channel:channel-id:message-id"
    /// </summary>
    public string SessionKey { get; set; } = string.Empty;
    
    /// <summary>
    /// The lane this command executes in (usually Main for channel messages)
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
    /// The original channel message
    /// </summary>
    public ChannelMessage ChannelMessage { get; set; } = new();
    
    /// <summary>
    /// The channel that originated this message
    /// </summary>
    public Channel? OriginatingChannel { get; set; }
    
    /// <summary>
    /// Target agent ID to execute this command
    /// </summary>
    public string AgentId { get; set; } = string.Empty;
    
    /// <summary>
    /// Model override for this command (optional)
    /// </summary>
    public string? Model { get; set; }
    
    /// <summary>
    /// Thinking level override for this command (optional)
    /// </summary>
    public string? ThinkingLevel { get; set; }
    
    /// <summary>
    /// Timeout in seconds for this command
    /// </summary>
    public int? TimeoutSeconds { get; set; }
    
    /// <summary>
    /// Metadata for tracking/logging
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
    
    /// <summary>
    /// Creates a channel command from a channel message
    /// </summary>
    public static ChannelCommand CreateFromChannelMessage(
        ChannelMessage channelMessage,
        Channel originatingChannel,
        string agentId,
        CommandLane lane = CommandLane.Main,
        int priority = 0,
        string? model = null,
        string? thinkingLevel = null,
        int? timeoutSeconds = null)
    {
        var sessionKey = $"channel:{originatingChannel.ChannelId}:{channelMessage.Id}";
        
        return new ChannelCommand
        {
            SessionKey = sessionKey,
            ChannelMessage = channelMessage,
            OriginatingChannel = originatingChannel,
            AgentId = agentId,
            Lane = lane,
            Priority = priority,
            Model = model,
            ThinkingLevel = thinkingLevel,
            TimeoutSeconds = timeoutSeconds,
            Metadata = new Dictionary<string, string>
            {
                ["channel_id"] = originatingChannel.ChannelId,
                ["channel_name"] = originatingChannel.Name,
                ["sender_id"] = channelMessage.SenderId,
                ["sender_name"] = channelMessage.SenderName,
                ["message_type"] = channelMessage.Type.ToString(),
                ["received_at"] = channelMessage.Timestamp.ToString("O")
            }
        };
    }
}
