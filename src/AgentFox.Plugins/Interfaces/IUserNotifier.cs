namespace AgentFox.Plugins.Interfaces;

/// <summary>
/// Lets a plugin deliver a message to the user's messaging channels (Discord, Telegram, WhatsApp, …)
/// from code that is not running inside an agent turn — a background worker, a hosted service, an
/// event handler.
/// <para>
/// The host's <c>ChannelManager</c> lives in the host assembly, which plugins deliberately cannot
/// reference (see <c>PluginLoadContext</c>). This interface is the shared, versioned seam over it,
/// resolved from the <see cref="IServiceProvider"/> handed to <see cref="IAppModule.StartAsync"/>.
/// </para>
/// <para>
/// Delivery goes to the channels subscribed to the notification's topic, matching what the
/// <c>notify_user</c> tool does. It is best-effort: a channel that fails is logged and skipped
/// rather than throwing, so one dead transport cannot suppress the message on the others.
/// </para>
/// </summary>
public interface IUserNotifier
{
    /// <summary>
    /// Broadcasts <paramref name="message"/> to every connected channel, bypassing subscription
    /// filtering. Markdown renders on Telegram and Discord. Returns the number of channels the
    /// message actually reached — 0 means nothing was delivered (no channels configured, none
    /// connected yet, or all of them failed), which callers should treat as a delivery failure
    /// worth logging.
    /// </summary>
    Task<int> NotifyAsync(string message, CancellationToken ct = default);

    /// <summary>
    /// Publishes <paramref name="message"/> on <paramref name="topic"/>, reaching only the channels
    /// whose subscriptions match. Prefer this over the untopiced overload: an unaddressed
    /// notification cannot be filtered, so it lands on every channel the user has configured
    /// regardless of what they asked to see there.
    /// <para>
    /// Publish from a constant, not a literal — see <see cref="Channels.NotificationTopics"/>.
    /// A topic nothing subscribes to is dropped silently by design, so a typo costs the message.
    /// </para>
    /// <para>
    /// Default-implemented as the unfiltered broadcast so an implementation compiled against the
    /// earlier version of this interface keeps working; hosts override it to route properly.
    /// </para>
    /// </summary>
    /// <param name="topic">
    /// Dot-separated subject, e.g. <c>trading.order.accepted</c>. Null or blank behaves as the
    /// untopiced overload.
    /// </param>
    Task<int> NotifyAsync(string message, string? topic, CancellationToken ct = default)
        => NotifyAsync(message, ct);
}
