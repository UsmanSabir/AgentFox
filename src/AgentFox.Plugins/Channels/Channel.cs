namespace AgentFox.Plugins.Channels;

/// <summary>
/// Base class for all channel integrations.
/// </summary>
public abstract class Channel
{
    public string Type { get; protected set; } = string.Empty;
    public string Name { get; protected set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public bool IsConnected { get; protected set; }

    public abstract Task<bool> ConnectAsync();

    public abstract Task DisconnectAsync();

    public abstract Task<ChannelMessage> SendMessageAsync(string content);

    public abstract Task<List<ChannelMessage>> ReceiveMessagesAsync();

    public event EventHandler<ChannelMessage>? OnMessageReceived;

    public void RaiseMessageReceived(ChannelMessage message)
    {
        OnMessageReceived?.Invoke(this, message);
    }

    public virtual async Task SendReplyAsync(ChannelMessage originalMessage, string content)
    {
        await SendMessageAsync(content);
    }

    public virtual Task<WebhookResult> ProcessWebhookAsync(
        string body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct = default)
        => Task.FromResult(WebhookResult.Unsupported(Name));

    public virtual async Task SendToTargetAsync(string targetId, string content)
    {
        await SendMessageAsync(content);
    }
}

public sealed record WebhookResult(bool Supported, bool Accepted, string? Error = null)
{
    public static WebhookResult Unsupported(string channelName) =>
        new(false, false, $"Channel '{channelName}' does not support webhooks.");

    public static WebhookResult Ok() => new(true, true);

    public static WebhookResult Failed(string error) => new(true, false, error);
}

public class ChannelMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ChannelId { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public MessageType Type { get; set; } = MessageType.Text;
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public enum MessageType
{
    Text,
    Image,
    File,
    Audio,
    Video,
    Location,
    Command
}
