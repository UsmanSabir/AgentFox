namespace AgentFox.Plugins.Interfaces;

/// <summary>
/// Thin interface over <c>ChannelManager</c> (in AgentFox.Channels) exposed to plugins
/// via <see cref="IPluginContext"/> so that channel-provider plugins can add, remove,
/// and query channels without referencing the concrete manager class.
/// </summary>
public interface IChannelManager
{
    /// <summary>All currently registered channel instances, keyed by <see cref="IChannel.ChannelId"/>.</summary>
    IReadOnlyDictionary<string, IChannel> Channels { get; }

    /// <summary>
    /// Register a channel and subscribe to its <see cref="IChannel.OnMessageReceived"/> event.
    /// The caller is responsible for connecting the channel first if desired.
    /// </summary>
    void AddChannel(IChannel channel);

    /// <summary>
    /// Connect a channel and register it live at runtime — no restart needed.
    /// Returns <c>false</c> if the connection fails (the channel is not added in that case).
    /// </summary>
    Task<bool> AddAndConnectAsync(IChannel channel);

    /// <summary>Disconnect and remove a channel by its <see cref="IChannel.ChannelId"/>.</summary>
    Task RemoveChannelAsync(string channelId);

    /// <summary>
    /// Look up a registered channel by its human-readable name (case-insensitive).
    /// Returns <c>null</c> if no matching channel is registered.
    /// </summary>
    IChannel? GetChannelByName(string name);
}
