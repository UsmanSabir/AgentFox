using System.Threading.Channels;
using AgentFox.Agents;

namespace AgentFox.ChannelTests;

/// <summary>
/// The bus carries live turns for conversations that have no HTTP request of their own — the
/// parent-session turn a finishing background sub-agent triggers. It sits on the agent's hot
/// path, so the properties that matter are that publishing never blocks the turn and that a
/// departed or wedged client cannot accumulate memory.
/// </summary>
[TestClass]
public sealed class ConversationEventBusTests
{
    private const string Conversation = "web_abc123";

    [TestMethod]
    public void Publish_ReachesEverySubscriberOfTheConversation()
    {
        var bus = new ConversationEventBus();
        using var first  = bus.Subscribe(Conversation);
        using var second = bus.Subscribe(Conversation);

        bus.Publish(Conversation, "background_token", new { token = "hi" });

        Assert.AreEqual(2, bus.SubscriberCount(Conversation));
        Assert.IsTrue(first.Reader.TryRead(out var a));
        Assert.IsTrue(second.Reader.TryRead(out var b));
        Assert.AreEqual("background_token", a!.Type);
        Assert.AreEqual("background_token", b!.Type);
    }

    [TestMethod]
    public void Publish_DoesNotLeakAcrossConversations()
    {
        var bus = new ConversationEventBus();
        using var mine = bus.Subscribe(Conversation);
        using var other = bus.Subscribe("web_other");

        bus.Publish(Conversation, "background_result", new { message = "done" });

        Assert.IsTrue(mine.Reader.TryRead(out _));
        Assert.IsFalse(other.Reader.TryRead(out _), "A turn must not stream into a different session.");
    }

    [TestMethod]
    public void Publish_WithNoSubscribers_IsANoOp()
    {
        var bus = new ConversationEventBus();

        // The common case: a sub-agent finishing while no browser is open. This must not throw
        // or retain anything — the durable copy goes to PendingNotificationStore instead.
        bus.Publish(Conversation, "background_turn_done", new { message = "result" });

        Assert.AreEqual(0, bus.SubscriberCount(Conversation));
    }

    [TestMethod]
    public void Dispose_UnregistersTheSubscriber()
    {
        var bus = new ConversationEventBus();
        var subscription = bus.Subscribe(Conversation);
        Assert.AreEqual(1, bus.SubscriberCount(Conversation));

        subscription.Dispose();

        Assert.AreEqual(0, bus.SubscriberCount(Conversation),
            "A closed connection must stop receiving, or every event leaks for the process lifetime.");

        bus.Publish(Conversation, "background_token", new { token = "late" });
        Assert.IsFalse(subscription.Reader.TryRead(out _));
    }

    [TestMethod]
    public void Dispose_IsIdempotent()
    {
        var bus = new ConversationEventBus();
        var subscription = bus.Subscribe(Conversation);

        subscription.Dispose();
        subscription.Dispose();

        Assert.AreEqual(0, bus.SubscriberCount(Conversation));
    }

    [TestMethod]
    public void Publish_ToAStalledSubscriber_DropsOldestAndKeepsGoing()
    {
        var bus = new ConversationEventBus();
        using var subscription = bus.Subscribe(Conversation);

        // Far past the 1024-slot buffer, with nobody reading: an agent turn streaming tokens
        // must never be slowed or blocked by a client that stopped draining.
        for (var i = 0; i < 3000; i++)
            bus.Publish(Conversation, "background_token", new { token = i.ToString() });

        var drained = 0;
        while (subscription.Reader.TryRead(out _)) drained++;

        Assert.IsTrue(drained > 0, "Something must survive for a client that resumes reading.");
        Assert.IsTrue(drained <= 1024, $"Buffer must stay bounded; drained {drained}.");
    }

    [TestMethod]
    public async Task Reader_ObservesEventsPublishedAfterItStartedWaiting()
    {
        var bus = new ConversationEventBus();
        using var subscription = bus.Subscribe(Conversation);

        // The realistic shape: the SSE endpoint is parked on ReadAllAsync long before the
        // sub-agent finishes.
        var waiting = subscription.Reader.ReadAsync().AsTask();
        Assert.IsFalse(waiting.IsCompleted);

        bus.Publish(Conversation, "background_turn_started", new { runKey = "run-1" });

        var received = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("background_turn_started", received.Type);
    }
}
