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

    /// <summary>
    /// The topics this channel receives notifications for. Defaults to the catch-all, so a channel
    /// nobody configured behaves exactly as it did before subscriptions existed.
    /// <para>
    /// Set by the host from config or <c>manage_channel</c>; providers never need to touch it.
    /// Replaced wholesale rather than mutated — <c>ChannelManager</c> reads it from the fan-out
    /// while the config surface may be rewriting it.
    /// </para>
    /// </summary>
    public TopicSubscription Subscriptions { get; set; } = TopicSubscription.All;

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

    /// <summary>
    /// Sends a message paired with one or more follow-up actions (e.g. HITL approve/reject).
    /// Channels that support interactive UI (Discord buttons, Telegram inline keyboards)
    /// should override this to render <paramref name="actions"/> as clickable controls that,
    /// when triggered, raise <see cref="OnMessageReceived"/> with the action's
    /// <see cref="ChannelAction.Command"/> as the message content — reusing the same
    /// text-command handling every channel already supports. The default implementation
    /// just sends the text as-is; callers should ensure that text also spells out the
    /// commands themselves as a fallback for channels with no interactive UI.
    /// </summary>
    public virtual Task SendActionableAsync(string content, IReadOnlyList<ChannelAction> actions) =>
        SendToTargetAsync(string.Empty, content);
}

/// <summary>
/// A single follow-up action offered alongside a channel message (e.g. an Approve button).
/// <see cref="Command"/> is the exact text that should be treated as if the user typed it
/// themselves — e.g. "/approve A1B2C3D4" — so channels can implement interactive UI without
/// any new server-side resolution logic.
/// </summary>
public sealed record ChannelAction(string Label, string Command);

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
