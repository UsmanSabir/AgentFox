using AgentFox.Channels;

namespace AgentFox.Agents;

/// <summary>
/// Singleton that holds the <see cref="ChannelManager"/> once it has been created by
/// <see cref="AgentOrchestrator"/> during startup.
/// <para>
/// Modules that need to interact with channels (e.g. <c>WebhookModule</c>) await
/// <see cref="WaitAsync"/> so they can handle requests even before channels are loaded.
/// </para>
/// </summary>
public sealed class ChannelManagerHolder
{
    private readonly TaskCompletionSource<ChannelManager> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Called once by <see cref="AgentOrchestrator"/> after channels are fully configured.
    /// Subsequent calls are no-ops.
    /// </summary>
    public void Publish(ChannelManager manager) => _tcs.TrySetResult(manager);

    /// <summary>The manager if already published, otherwise null.</summary>
    public ChannelManager? Manager => _tcs.Task.IsCompletedSuccessfully ? _tcs.Task.Result : null;

    /// <summary>Awaitable that completes once <see cref="Publish"/> is called.</summary>
    public Task<ChannelManager> WaitAsync(CancellationToken ct = default) =>
        _tcs.Task.WaitAsync(ct);
}
