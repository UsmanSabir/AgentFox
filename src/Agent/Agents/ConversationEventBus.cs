using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AgentFox.Agents;

/// <summary>
/// Live event fan-out for turns that run against a conversation without an HTTP request of
/// their own — chiefly the parent-session turn a background sub-agent triggers when it reports
/// back. Those turns stream tokens and tool activity exactly like a user-initiated turn, but
/// there is no open <c>/chat/stream</c> response to write them to, so the web client used to see
/// nothing until the next 3s poll delivered one finished blob. Web clients hold a long-lived
/// subscription on <c>GET /chat/events/{conversationId}</c> and receive the same events live.
///
/// This is a best-effort transport, not a delivery guarantee: <see cref="PendingNotificationStore"/>
/// remains the durable path, so a browser that is closed or mid-reconnect still gets the result on
/// its next poll. Events carry the sub-agent run key so a client that rendered a turn live can
/// discard the polled duplicate.
/// </summary>
public sealed class ConversationEventBus
{
    /// <summary>
    /// Per-subscriber buffer. Deep enough to absorb a fast token stream against a slow client,
    /// bounded so a wedged reader costs a fixed amount of memory rather than growing forever.
    /// </summary>
    private const int SubscriberCapacity = 1024;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<ConversationEvent>>> _subscribers = new();

    /// <summary>Number of live subscribers for a conversation (0 when nobody is listening).</summary>
    public int SubscriberCount(string conversationId) =>
        _subscribers.TryGetValue(conversationId, out var set) ? set.Count : 0;

    /// <summary>
    /// Opens a subscription. Dispose it to unregister — an abandoned subscription would keep
    /// receiving (and dropping) every event for the conversation for the life of the process.
    /// </summary>
    public ConversationEventSubscription Subscribe(string conversationId)
    {
        var channel = Channel.CreateBounded<ConversationEvent>(new BoundedChannelOptions(SubscriberCapacity)
        {
            // A client too slow to keep up loses the oldest tokens rather than stalling the
            // agent turn that is publishing them. The durable result still arrives via polling.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var id  = Guid.NewGuid();
        var set = _subscribers.GetOrAdd(conversationId, _ => new ConcurrentDictionary<Guid, Channel<ConversationEvent>>());
        set[id] = channel;

        return new ConversationEventSubscription(this, conversationId, id, channel.Reader);
    }

    /// <summary>
    /// Publishes an event to every subscriber of the conversation. Never throws and never blocks:
    /// a publishing agent turn must not be affected by who happens to be watching.
    /// </summary>
    public void Publish(string conversationId, string type, object payload)
    {
        if (string.IsNullOrEmpty(conversationId)) return;
        if (!_subscribers.TryGetValue(conversationId, out var set)) return;

        var evt = new ConversationEvent(type, payload);
        foreach (var channel in set.Values)
            channel.Writer.TryWrite(evt);
    }

    internal void Unsubscribe(string conversationId, Guid id)
    {
        if (!_subscribers.TryGetValue(conversationId, out var set)) return;

        set.TryRemove(id, out _);

        // Drop the conversation entry once the last watcher leaves, so a long-running process
        // does not accumulate an empty dictionary per conversation it has ever served.
        if (set.IsEmpty)
            _subscribers.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Guid, Channel<ConversationEvent>>>(conversationId, set));
    }
}

/// <summary>One live event: an SSE event name plus the payload serialized as its data.</summary>
public sealed record ConversationEvent(string Type, object Payload);

/// <summary>A single client's registration on the bus. Dispose to unregister.</summary>
public sealed class ConversationEventSubscription : IDisposable
{
    private readonly ConversationEventBus _bus;
    private readonly string _conversationId;
    private readonly Guid _id;
    private bool _disposed;

    internal ConversationEventSubscription(
        ConversationEventBus bus,
        string conversationId,
        Guid id,
        ChannelReader<ConversationEvent> reader)
    {
        _bus            = bus;
        _conversationId = conversationId;
        _id             = id;
        Reader          = reader;
    }

    public ChannelReader<ConversationEvent> Reader { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bus.Unsubscribe(_conversationId, _id);
    }
}
