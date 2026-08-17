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
/// Delivery is a broadcast to every connected channel, matching what the <c>notify_user</c> tool
/// does. It is best-effort: a channel that fails is logged and skipped rather than throwing, so one
/// dead transport cannot suppress the message on the others.
/// </para>
/// </summary>
public interface IUserNotifier
{
    /// <summary>
    /// Broadcasts <paramref name="message"/> to every connected channel. Markdown renders on
    /// Telegram and Discord. Returns the number of channels the message actually reached — 0 means
    /// nothing was delivered (no channels configured, none connected yet, or all of them failed),
    /// which callers should treat as a delivery failure worth logging.
    /// </summary>
    Task<int> NotifyAsync(string message, CancellationToken ct = default);
}
