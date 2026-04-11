namespace AgentFox.Plugins.Interfaces;

/// <summary>
/// Contract for a messaging channel integration.
/// Implement this interface (or extend the <c>Channel</c> abstract base class in
/// <c>AgentFox.Channels</c>) to add a new channel type that the ChannelManager can
/// manage without any changes to the core.
/// </summary>
public interface IChannel
{
    /// <summary>Human-readable name, e.g. "Telegram", "Discord".</summary>
    string Name { get; }

    /// <summary>
    /// Stable unique identifier for this channel instance, e.g. "telegram", "discord_12345_67890".
    /// Used as the dictionary key in <see cref="IChannelManager"/>.
    /// </summary>
    string ChannelId { get; }

    /// <summary><c>true</c> once <see cref="ConnectAsync"/> succeeds.</summary>
    bool IsConnected { get; }

    /// <summary>Establish the connection to the underlying service.</summary>
    Task<bool> ConnectAsync();

    /// <summary>Gracefully close the connection.</summary>
    Task DisconnectAsync();

    /// <summary>Send a message to the channel's default target and return a receipt.</summary>
    Task<IChannelMessage> SendMessageAsync(string content);

    /// <summary>
    /// Poll for new messages (used by non-event-driven channels).
    /// Event-driven channels (e.g. Discord, Telegram) return an empty list because
    /// messages arrive via <see cref="OnMessageReceived"/>.
    /// </summary>
    Task<IList<IChannelMessage>> ReceiveMessagesAsync();

    /// <summary>
    /// Send a reply in the context of an inbound message.
    /// Multi-chat channels (Telegram, Discord) use metadata on
    /// <paramref name="originalMessage"/> to route the reply correctly.
    /// </summary>
    Task SendReplyAsync(IChannelMessage originalMessage, string content);

    /// <summary>
    /// Proactively send a message to a specific target within this channel.
    /// For single-recipient channels <paramref name="targetId"/> is ignored.
    /// </summary>
    Task SendToTargetAsync(string targetId, string content);

    /// <summary>
    /// Process an inbound webhook payload.
    /// Override in channels that support webhook mode; the default returns Unsupported.
    /// </summary>
    Task<WebhookResult> ProcessWebhookAsync(
        string body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct = default);

    /// <summary>Raised when a new inbound message is received.</summary>
    event EventHandler<IChannelMessage>? OnMessageReceived;

    /// <summary>Raise <see cref="OnMessageReceived"/> (used in tests and base implementations).</summary>
    void RaiseMessageReceived(IChannelMessage message);
}

/// <summary>
/// Represents an inbound or outbound channel message.
/// The concrete <c>ChannelMessage</c> class in AgentFox.Channels implements this interface.
/// </summary>
public interface IChannelMessage
{
    string Id { get; }
    string ChannelId { get; }
    string SenderId { get; }
    string SenderName { get; }
    string Content { get; }
    DateTime Timestamp { get; }
    Dictionary<string, string> Metadata { get; }
}

/// <summary>
/// Result returned by <see cref="IChannel.ProcessWebhookAsync"/>.
/// </summary>
public sealed record WebhookResult(bool Supported, bool Accepted, string? Error = null)
{
    /// <summary>The channel does not support webhook mode.</summary>
    public static WebhookResult Unsupported(string channelName) =>
        new(false, false, $"Channel '{channelName}' does not support webhooks.");

    /// <summary>Webhook payload was accepted and is being processed.</summary>
    public static WebhookResult Ok() => new(true, true);

    /// <summary>Webhook was received but processing failed.</summary>
    public static WebhookResult Failed(string error) => new(true, false, error);
}
