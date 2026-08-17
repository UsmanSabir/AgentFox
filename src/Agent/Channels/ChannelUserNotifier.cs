using AgentFox.Agents;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgentFox.Channels;

/// <summary>
/// Host-side implementation of <see cref="IUserNotifier"/>, the seam plugins use to reach the
/// user's channels. Delegates to <see cref="ChannelManager.BroadcastAsync"/>, which is the same
/// fan-out <c>notify_user</c> and the HITL approval prompts already use.
/// <para>
/// It reads the manager through <see cref="ChannelManagerHolder"/> rather than taking it directly,
/// because <c>ChannelManager</c> is built during agent startup and is not in DI. A notification
/// raised before channels are up therefore returns 0 instead of throwing — background workers start
/// with the host, and a trading alert at t+0s must not crash a hosted service.
/// </para>
/// </summary>
public sealed class ChannelUserNotifier : IUserNotifier
{
    private readonly ChannelManagerHolder _holder;
    private readonly ILogger<ChannelUserNotifier>? _logger;

    public ChannelUserNotifier(ChannelManagerHolder holder, ILogger<ChannelUserNotifier>? logger = null)
    {
        _holder = holder;
        _logger = logger;
    }

    public Task<int> NotifyAsync(string message, CancellationToken ct = default) =>
        NotifyAsync(message, topic: null, ct);

    public async Task<int> NotifyAsync(string message, string? topic, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return 0;

        var manager = _holder.Manager;
        if (manager is null)
        {
            _logger?.LogWarning(
                "IUserNotifier: channels are not ready yet; dropped a {Length}-char notification on '{Topic}'.",
                message.Length, topic ?? "(none)");
            return 0;
        }

        var sent = await manager.BroadcastAsync(message, topic);

        // ChannelManager already warns when a topic matched no subscription — that is a routing
        // problem. This one stays because a zero here also covers "no channels at all", which is a
        // different thing for a caller deciding whether its alert reached anyone.
        if (sent == 0)
            _logger?.LogWarning(
                "IUserNotifier: no connected channel accepted a {Length}-char notification on '{Topic}'.",
                message.Length, topic ?? "(none)");
        else
            _logger?.LogInformation(
                "IUserNotifier: delivered a {Length}-char notification on '{Topic}' to {Count} channel(s).",
                message.Length, topic ?? "(none)", sent);

        return sent;
    }
}
