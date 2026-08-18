using AgentFox.Agents;
using AgentFox.Channels;
using AgentFox.Plugins.Channels;
using Microsoft.Extensions.Configuration;

namespace AgentFox.ChannelTests;

/// <summary>
/// End-to-end routing through <see cref="ChannelManager"/>: which channels a topic actually reaches,
/// what happens when it reaches none, and that channel identity survives duplicates well enough for
/// subscriptions to be stored against it.
/// </summary>
[TestClass]
public class ChannelSubscriptionRoutingTests
{
    [TestMethod]
    public async Task Broadcast_WithoutTopic_StillReachesEveryConnectedChannel()
    {
        // The compatibility guarantee: every publisher predating topics sends none, and none of
        // them may lose delivery to a channel that has narrowed its subscriptions.
        var manager = new ChannelManager(() => null);
        var narrow = AddChannel(manager, "telegram", "tg", subscribe: "trading.order.>");
        var wide = AddChannel(manager, "discord", "dc", subscribe: null);

        var sent = await manager.BroadcastAsync("hello");

        Assert.AreEqual(2, sent);
        Assert.AreEqual(1, narrow.Sent.Count);
        Assert.AreEqual(1, wide.Sent.Count);
    }

    [TestMethod]
    public async Task Broadcast_WithTopic_ReachesOnlySubscribers()
    {
        var manager = new ChannelManager(() => null);
        var orders = AddChannel(manager, "telegram", "tg-orders", subscribe: "trading.order.>");
        var ops = AddChannel(manager, "telegram", "tg-ops", subscribe: "hitl.>, agent.>");
        var everything = AddChannel(manager, "discord", "dc", subscribe: ">");

        var sent = await manager.BroadcastAsync("filled", "trading.order.accepted");

        Assert.AreEqual(2, sent);
        Assert.AreEqual(1, orders.Sent.Count);
        Assert.AreEqual(0, ops.Sent.Count);
        Assert.AreEqual(1, everything.Sent.Count);
    }

    [TestMethod]
    public async Task Broadcast_OnStarFilter_MatchesOneSegmentOnly()
    {
        var manager = new ChannelManager(() => null);
        var oneDeep = AddChannel(manager, "telegram", "tg", subscribe: "trading.*");

        Assert.AreEqual(1, await manager.BroadcastAsync("m", "trading.order"));
        Assert.AreEqual(0, await manager.BroadcastAsync("m", "trading.order.accepted"));
        Assert.AreEqual(1, oneDeep.Sent.Count);
    }

    [TestMethod]
    public async Task Broadcast_OnCrossCuttingFilter_MatchesAcrossRoots()
    {
        // "*.order" — the case that motivated single-segment stars: every order topic regardless
        // of which subsystem publishes it.
        var manager = new ChannelManager(() => null);
        AddChannel(manager, "telegram", "tg", subscribe: "*.order");

        Assert.AreEqual(1, await manager.BroadcastAsync("m", "trading.order"));
        Assert.AreEqual(1, await manager.BroadcastAsync("m", "brokerage.order"));
        Assert.AreEqual(0, await manager.BroadcastAsync("m", "trading.stop"));
    }

    [TestMethod]
    public async Task UnmatchedTopic_IsDropped_AndReportsZero()
    {
        var manager = new ChannelManager(() => null);
        var channel = AddChannel(manager, "telegram", "tg", subscribe: "hitl.>");

        var sent = await manager.BroadcastAsync("filled", "trading.order.accepted");

        Assert.AreEqual(0, sent);
        Assert.AreEqual(0, channel.Sent.Count);
    }

    [TestMethod]
    public async Task UnmatchedMandatoryTopic_FallsBackToEveryChannel()
    {
        // A HITL prompt filtered to nothing does not lose a message, it deadlocks the turn that
        // asked — nobody is told, so nobody can approve. Mandatory topics ignore the filters
        // rather than let a config mistake do that.
        var manager = new ChannelManager(() => null);
        var a = AddChannel(manager, "telegram", "tg", subscribe: "trading.>");
        var b = AddChannel(manager, "discord", "dc", subscribe: "trading.>");

        var sent = await manager.BroadcastAsync("approve?", NotificationTopics.HitlApproval);

        Assert.AreEqual(2, sent);
        Assert.AreEqual(1, a.Sent.Count);
        Assert.AreEqual(1, b.Sent.Count);
    }

    [TestMethod]
    public async Task DisconnectedChannels_AreNeverRecipients()
    {
        var manager = new ChannelManager(() => null);
        var offline = AddChannel(manager, "telegram", "tg", subscribe: ">", connected: false);

        Assert.AreEqual(0, await manager.BroadcastAsync("m", "agent.notify"));
        Assert.AreEqual(0, offline.Sent.Count);
    }

    [TestMethod]
    public async Task OneFailingChannel_DoesNotSuppressTheOthers()
    {
        var manager = new ChannelManager(() => null);
        AddChannel(manager, "telegram", "tg", subscribe: ">", throws: true);
        var healthy = AddChannel(manager, "discord", "dc", subscribe: ">");

        var sent = await manager.BroadcastAsync("m", "agent.notify");

        Assert.AreEqual(1, sent);
        Assert.AreEqual(1, healthy.Sent.Count);
    }

    [TestMethod]
    public void DuplicateChannelIds_AreSuffixed_RatherThanOverwritingEachOther()
    {
        // Telegram hardcodes its id, so a second bot used to replace the first in the dictionary:
        // registered, connected, and unreachable by every send. Subscriptions are stored against
        // this id, so the collision has to resolve rather than silently drop a channel.
        var manager = new ChannelManager(() => null);
        var first = new RecordingChannel("telegram", "Telegram", "telegram", connected: true);
        var second = new RecordingChannel("telegram", "Telegram", "telegram", connected: true);

        manager.AddChannel(first);
        manager.AddChannel(second);

        Assert.AreEqual(2, manager.Channels.Count);
        Assert.AreEqual("telegram", first.ChannelId);
        Assert.AreEqual("telegram#2", second.ChannelId);
    }

    [TestMethod]
    public void GetChannelByName_ResolvesById_BeforeFallingBackToType()
    {
        var manager = new ChannelManager(() => null);
        var orders = AddChannel(manager, "telegram", "tg-orders", subscribe: null);
        var ops = AddChannel(manager, "telegram", "tg-ops", subscribe: null);

        Assert.AreSame(orders, manager.GetChannelByName("tg-orders"));
        Assert.AreSame(ops, manager.GetChannelByName("tg-ops"));
        Assert.IsNotNull(manager.GetChannelByName("telegram"));
    }

    // ── Config ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void Config_ReadsSubscribeAsAScalar()
    {
        var entry = ReadSingleEntry(new Dictionary<string, string?>
        {
            ["Channels:0:Type"] = "Telegram",
            ["Channels:0:Name"] = "tg-orders",
            ["Channels:0:Subscribe"] = "trading.order.>, hitl.>"
        });

        Assert.AreEqual("tg-orders", entry.Name);
        Assert.AreEqual("trading.order.>, hitl.>", entry.SubscribeSpec);
    }

    [TestMethod]
    public void Config_ReadsSubscribeAsAnArray()
    {
        // IConfiguration flattens an array into numerically-keyed children whose parent has a null
        // Value — the pass that builds the config dictionary drops exactly that shape, so without
        // explicit handling this silently parsed as "no filters", i.e. catch-all.
        var entry = ReadSingleEntry(new Dictionary<string, string?>
        {
            ["Channels:0:Type"] = "Telegram",
            ["Channels:0:Subscribe:0"] = "trading.order.>",
            ["Channels:0:Subscribe:1"] = "hitl.>"
        });

        Assert.AreEqual("trading.order.>, hitl.>", entry.SubscribeSpec);

        var subscription = TopicSubscription.Parse(entry.SubscribeSpec);
        Assert.IsFalse(subscription.IsCatchAll);
        Assert.IsTrue(subscription.Matches("trading.order.accepted"));
        Assert.IsFalse(subscription.Matches("agent.notify"));
    }

    [TestMethod]
    public void Config_WithoutSubscribe_MeansCatchAll()
    {
        var entry = ReadSingleEntry(new Dictionary<string, string?>
        {
            ["Channels:0:Type"] = "Telegram"
        });

        Assert.IsNull(entry.SubscribeSpec);
        Assert.IsTrue(TopicSubscription.Parse(entry.SubscribeSpec).IsCatchAll);
    }

    [TestMethod]
    public void Config_LegacyObjectShape_KeepsItsKeyAsTheName()
    {
        var entry = ReadSingleEntry(new Dictionary<string, string?>
        {
            ["Channels:telegram_main:BotToken"] = "token",
            ["Channels:telegram_main:Subscribe"] = "trading.>"
        });

        Assert.AreEqual("telegram", entry.Type);
        Assert.AreEqual("telegram_main", entry.Name);
        Assert.AreEqual("trading.>", entry.SubscribeSpec);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ChannelConfigurationEntry ReadSingleEntry(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var entries = ChannelConfiguration.ReadEntries(config);

        Assert.AreEqual(1, entries.Count);
        return entries[0];
    }

    private static RecordingChannel AddChannel(
        ChannelManager manager,
        string type,
        string channelId,
        string? subscribe,
        bool connected = true,
        bool throws = false)
    {
        var channel = new RecordingChannel(type, type, channelId, connected, throws)
        {
            Subscriptions = TopicSubscription.Parse(subscribe)
        };

        manager.AddChannel(channel);
        return channel;
    }

    private sealed class RecordingChannel : Channel
    {
        private readonly bool _throws;

        public RecordingChannel(string type, string name, string channelId, bool connected, bool throws = false)
        {
            Type = type;
            Name = name;
            ChannelId = channelId;
            IsConnected = connected;
            _throws = throws;
        }

        public List<string> Sent { get; } = [];

        public override Task<bool> ConnectAsync() => Task.FromResult(true);

        public override Task DisconnectAsync() => Task.CompletedTask;

        public override Task<ChannelMessage> SendMessageAsync(string content)
        {
            if (_throws) throw new InvalidOperationException("transport down");

            Sent.Add(content);
            return Task.FromResult(new ChannelMessage { ChannelId = ChannelId, Content = content });
        }

        public override Task<List<ChannelMessage>> ReceiveMessagesAsync() =>
            Task.FromResult(new List<ChannelMessage>());
    }
}
