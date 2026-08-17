using Microsoft.Extensions.Logging;

namespace AgentFox.Plugins.Channels;

/// <summary>One message a channel was asked to deliver, kept for inspection.</summary>
/// <param name="Sequence">Monotonic per channel, so a poller can tell new from re-read.</param>
/// <param name="At">UTC timestamp of the send.</param>
/// <param name="TargetId">The destination within the channel, blank for a broadcast.</param>
/// <param name="Content">The message body as it would have gone out.</param>
/// <param name="Actions">Labels of any interactive actions offered alongside it.</param>
public sealed record ChannelOutboxEntry(
    long Sequence,
    DateTime At,
    string TargetId,
    string Content,
    IReadOnlyList<string> Actions);

/// <summary>
/// A channel that can be asked what it received. Implemented by <see cref="DummyChannel"/>; the
/// interface exists so the host's inspection endpoint stays generic rather than type-testing for
/// one class.
/// </summary>
public interface IInspectableChannel
{
    /// <summary>Most recent first, capped at the channel's configured capacity.</summary>
    IReadOnlyList<ChannelOutboxEntry> RecentMessages { get; }

    /// <summary>Forgets everything received so far.</summary>
    void ClearOutbox();
}

/// <summary>
/// A channel that delivers nowhere and records everything, for exercising notification routing
/// without a real transport.
///
/// <para>
/// Subscription filters are the kind of config whose mistakes are invisible: a filter one character
/// off from the topic drops messages with no error anywhere, and the only way to notice is that
/// nothing arrives. Verifying one against a live Telegram bot means credentials, a chat, and
/// reading a phone. This channel makes the same check a matter of adding it with a filter, causing
/// the event, and reading its outbox back.
/// </para>
/// <para>
/// It connects instantly, never fails, and needs no credentials — so it is also what to reach for
/// when reproducing a delivery problem without wanting a real notification to fire.
/// </para>
/// </summary>
public sealed class DummyChannel : Channel, IInspectableChannel
{
    /// <summary>Messages retained before the oldest are discarded.</summary>
    public const int DefaultCapacity = 50;

    private readonly int _capacity;
    private readonly ILogger<DummyChannel>? _logger;
    private readonly object _gate = new();
    private readonly LinkedList<ChannelOutboxEntry> _outbox = new();
    private long _sequence;

    public DummyChannel(string name = "dummy", int capacity = DefaultCapacity, ILogger<DummyChannel>? logger = null)
    {
        Type = "dummy";
        Name = string.IsNullOrWhiteSpace(name) ? "dummy" : name.Trim();
        ChannelId = Name;
        _capacity = capacity > 0 ? capacity : DefaultCapacity;
        _logger = logger;
    }

    /// <summary>Total messages ever received, including any already evicted.</summary>
    public long TotalReceived
    {
        get { lock (_gate) return _sequence; }
    }

    public IReadOnlyList<ChannelOutboxEntry> RecentMessages
    {
        get { lock (_gate) return [.. _outbox]; }
    }

    public void ClearOutbox()
    {
        lock (_gate)
        {
            _outbox.Clear();
            // Sequence deliberately keeps counting: a client that cleared and polled again should
            // not see fresh messages reuse numbers it has already seen.
        }
    }

    public override Task<bool> ConnectAsync()
    {
        IsConnected = true;
        _logger?.LogInformation("[{ChannelId}] Dummy channel connected; messages are recorded, not sent.", ChannelId);
        return Task.FromResult(true);
    }

    public override Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public override Task<ChannelMessage> SendMessageAsync(string content) =>
        Task.FromResult(Record(string.Empty, content, []));

    public override Task SendToTargetAsync(string targetId, string content)
    {
        Record(targetId ?? string.Empty, content, []);
        return Task.CompletedTask;
    }

    public override Task SendActionableAsync(string content, IReadOnlyList<ChannelAction> actions)
    {
        Record(string.Empty, content, [.. actions.Select(a => a.Label)]);
        return Task.CompletedTask;
    }

    public override Task SendReplyAsync(ChannelMessage originalMessage, string content)
    {
        Record(originalMessage.ChannelId ?? string.Empty, content, []);
        return Task.CompletedTask;
    }

    private ChannelMessage Record(string targetId, string content, IReadOnlyList<string> actions)
    {
        ChannelOutboxEntry entry;

        lock (_gate)
        {
            entry = new ChannelOutboxEntry(++_sequence, DateTime.UtcNow, targetId, content, actions);
            _outbox.AddFirst(entry);

            while (_outbox.Count > _capacity)
                _outbox.RemoveLast();
        }

        _logger?.LogInformation(
            "[{ChannelId}] #{Sequence} received {Length} chars.", ChannelId, entry.Sequence, content.Length);

        return new ChannelMessage { ChannelId = ChannelId, Content = content };
    }

    public override Task<List<ChannelMessage>> ReceiveMessagesAsync() =>
        Task.FromResult(new List<ChannelMessage>());
}

/// <summary>
/// Provider for <see cref="DummyChannel"/>. Takes no credentials, so it can be added at runtime
/// with <c>manage_channel</c> and an empty config.
/// </summary>
public sealed class DummyChannelProvider : IChannelProvider
{
    public string ChannelType => "dummy";
    public string DisplayName => "Dummy (test)";

    public IReadOnlyDictionary<string, ChannelConfigField> GetConfigSchema() =>
        new Dictionary<string, ChannelConfigField>
        {
            ["Name"] = new() { Description = "Stable id for this channel; defaults to 'dummy'", Required = false },
            ["Capacity"] = new() { Description = $"Messages retained for inspection (default {DummyChannel.DefaultCapacity})", Required = false }
        };

    public (Channel? Channel, string? Error) Create(Dictionary<string, string> config, ChannelCreationContext context)
    {
        var name = config.TryGetValue("Name", out var configured) && !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : "dummy";

        var capacity = config.TryGetValue("Capacity", out var rawCapacity)
            && int.TryParse(rawCapacity, out var parsed) && parsed > 0
                ? parsed
                : DummyChannel.DefaultCapacity;

        return (new DummyChannel(name, capacity, context.LoggerFactory.CreateLogger<DummyChannel>()), null);
    }
}
