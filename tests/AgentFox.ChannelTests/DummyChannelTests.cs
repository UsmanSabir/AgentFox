using AgentFox.Agents;
using AgentFox.Channels;
using AgentFox.Plugins.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentFox.ChannelTests;

/// <summary>
/// The recording test channel, and the end-to-end check it exists to make possible: publish on a
/// topic, then read back which channels actually received it.
/// </summary>
[TestClass]
public class DummyChannelTests
{
    [TestMethod]
    public void Provider_CreatesWithoutCredentials()
    {
        var provider = new DummyChannelProvider();
        var (channel, error) = provider.Create([], CreationContext());

        Assert.IsNull(error);
        Assert.IsNotNull(channel);
        Assert.AreEqual("dummy", channel!.Type);
        Assert.AreEqual("dummy", channel.ChannelId);
        Assert.IsTrue(channel.Subscriptions.IsCatchAll);
    }

    [TestMethod]
    public void Provider_HonoursNameAndCapacity()
    {
        var (channel, _) = new DummyChannelProvider().Create(
            new Dictionary<string, string> { ["Name"] = "test", ["Capacity"] = "2" },
            CreationContext());

        Assert.AreEqual("test", channel!.ChannelId);

        var dummy = (DummyChannel)channel;
        for (var i = 1; i <= 5; i++)
            dummy.SendToTargetAsync(string.Empty, $"m{i}").GetAwaiter().GetResult();

        // Capacity bounds what is retained, not what is counted.
        Assert.AreEqual(2, dummy.RecentMessages.Count);
        Assert.AreEqual(5, dummy.TotalReceived);
        Assert.AreEqual("m5", dummy.RecentMessages[0].Content);
        Assert.AreEqual("m4", dummy.RecentMessages[1].Content);
    }

    [TestMethod]
    public async Task ConnectsInstantly_AndRecordsInsteadOfSending()
    {
        var channel = new DummyChannel("test", logger: NullLogger<DummyChannel>.Instance);

        Assert.IsFalse(channel.IsConnected);
        Assert.IsTrue(await channel.ConnectAsync());
        Assert.IsTrue(channel.IsConnected);

        await channel.SendToTargetAsync("chat-42", "hello");

        var entry = channel.RecentMessages.Single();
        Assert.AreEqual(1, entry.Sequence);
        Assert.AreEqual("chat-42", entry.TargetId);
        Assert.AreEqual("hello", entry.Content);
        Assert.AreEqual(0, entry.Actions.Count);
    }

    [TestMethod]
    public async Task RecordsInteractiveActionLabels()
    {
        var channel = new DummyChannel("test");
        await channel.ConnectAsync();

        await channel.SendActionableAsync("approve?", [
            new ChannelAction("✅ Approve", "/approve A1"),
            new ChannelAction("❌ Reject", "/reject A1")
        ]);

        CollectionAssert.AreEqual(
            new[] { "✅ Approve", "❌ Reject" },
            channel.RecentMessages.Single().Actions.ToArray());
    }

    [TestMethod]
    public async Task ClearOutbox_EmptiesHistoryButKeepsSequenceMovingForward()
    {
        var channel = new DummyChannel("test");
        await channel.ConnectAsync();
        await channel.SendToTargetAsync(string.Empty, "first");

        channel.ClearOutbox();
        Assert.AreEqual(0, channel.RecentMessages.Count);

        await channel.SendToTargetAsync(string.Empty, "second");

        // A client that cleared and polled again must not see a fresh message reuse a number it
        // already processed.
        Assert.AreEqual(2, channel.RecentMessages.Single().Sequence);
    }

    // ── What the channel exists for ──────────────────────────────────────────

    [TestMethod]
    public async Task TwoDummyChannels_ProveSubscriptionRoutingEndToEnd()
    {
        var manager = new ChannelManager(() => null);

        var orders = await AddDummy(manager, "tg-orders", "trading.order.>");
        var ops = await AddDummy(manager, "tg-ops", "hitl.>, agent.>");

        await manager.BroadcastAsync("BUY FFC filled", "trading.order.accepted");
        await manager.BroadcastAsync("plan?", NotificationTopics.HitlPlan);

        CollectionAssert.AreEqual(
            new[] { "BUY FFC filled" },
            orders.RecentMessages.Select(m => m.Content).ToArray());
        CollectionAssert.AreEqual(
            new[] { "plan?" },
            ops.RecentMessages.Select(m => m.Content).ToArray());
    }

    [TestMethod]
    public async Task DummyChannel_ShowsTheSilentDrop_WhenAFilterIsOneCharacterOff()
    {
        // The mistake this channel is for catching: "trading.orders" against a published
        // "trading.order.accepted". Nothing throws; the message simply never arrives.
        var manager = new ChannelManager(() => null);
        var typo = await AddDummy(manager, "typo", "trading.orders.>");

        var sent = await manager.BroadcastAsync("BUY FFC filled", "trading.order.accepted");

        Assert.AreEqual(0, sent);
        Assert.AreEqual(0, typo.RecentMessages.Count);
    }

    [TestMethod]
    public async Task DummyChannel_ReceivesMandatoryTrafficEvenWhenItDidNotSubscribe()
    {
        var manager = new ChannelManager(() => null);
        var narrow = await AddDummy(manager, "trading-only", "trading.>");

        await manager.BroadcastActionableAsync(
            "approve?", [new ChannelAction("✅ Approve", "/approve A1")], NotificationTopics.HitlApproval);

        Assert.AreEqual("approve?", narrow.RecentMessages.Single().Content);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<DummyChannel> AddDummy(ChannelManager manager, string name, string? subscribe)
    {
        var channel = new DummyChannel(name) { Subscriptions = TopicSubscription.Parse(subscribe) };
        await channel.ConnectAsync();
        manager.AddChannel(channel);
        return channel;
    }

    private static ChannelCreationContext CreationContext() => new()
    {
        LoggerFactory = NullLoggerFactory.Instance,
        Services = new ServiceCollection().BuildServiceProvider(),
        WorkspacePath = Path.GetTempPath()
    };
}
