using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
// The plugin has its own TradingAgent.Channel namespace (the WhatsApp bridge), which shadows the
// static System.Threading.Channels.Channel factory. Aliased rather than fully qualified everywhere.
using ChannelFactory = System.Threading.Channels.Channel;

namespace TradingAgent.Watchlist;

/// <summary>
/// Fans new alerts out to connected UI clients over Server-Sent Events.
///
/// <para>
/// Best-effort by design, and deliberately NOT the durable path: SQLite is. A subscriber that cannot
/// keep up has its oldest events dropped rather than being allowed to block the monitor pass, and a
/// client that misses events while disconnected simply re-reads <c>GET /trading/alerts</c> on load.
/// Making the stream reliable would mean the monitor waits on the slowest browser tab, which is the
/// wrong trade for something whose job is to notice a market moving.
/// </para>
/// </summary>
public sealed class AlertBroadcaster
{
    /// <summary>Events buffered per subscriber before the oldest are dropped.</summary>
    private const int BufferPerSubscriber = 64;

    private readonly ConcurrentDictionary<Guid, Channel<AlertRecord>> _subscribers = new();
    private readonly ILogger<AlertBroadcaster> _logger;

    public AlertBroadcaster(ILogger<AlertBroadcaster> logger) => _logger = logger;

    public int SubscriberCount => _subscribers.Count;

    /// <summary>Publishes to every subscriber. Never throws and never blocks the caller.</summary>
    public void Publish(AlertRecord alert)
    {
        foreach (var (_, channel) in _subscribers)
        {
            // DropOldest, so a full buffer costs the stalest event rather than the newest one.
            channel.Writer.TryWrite(alert);
        }
    }

    /// <summary>
    /// Subscribes for the lifetime of <paramref name="ct"/>. The returned sequence completes when the
    /// caller disconnects; the subscription is always removed, including on an aborted request.
    /// </summary>
    public async IAsyncEnumerable<AlertRecord> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = ChannelFactory.CreateBounded<AlertRecord>(new BoundedChannelOptions(BufferPerSubscriber)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _subscribers[id] = channel;
        _logger.LogDebug("[Alerts] SSE subscriber {Id} connected ({Count} total).", id, _subscribers.Count);

        try
        {
            await foreach (var alert in channel.Reader.ReadAllAsync(ct))
                yield return alert;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
            _logger.LogDebug("[Alerts] SSE subscriber {Id} disconnected ({Count} left).", id, _subscribers.Count);
        }
    }
}
