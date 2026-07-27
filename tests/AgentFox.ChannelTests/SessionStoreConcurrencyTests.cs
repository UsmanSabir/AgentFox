using AgentFox.Memory;
using Microsoft.Extensions.AI;

namespace AgentFox.ChannelTests;

/// <summary>
/// Two turns can run against one conversation at the same time: a live web turn and the turn that
/// injects a background sub-agent's result into the same parent session. They share one
/// <c>List&lt;ChatMessage&gt;</c> and one written-count watermark, neither of which is safe for
/// concurrent use on its own. The failure modes are quiet — a "Collection was modified" throw
/// inside whichever turn happened to be enumerating, or a message that stays in memory and never
/// reaches the .md transcript because the other turn advanced the watermark past it.
/// </summary>
[TestClass]
public sealed class SessionStoreConcurrencyTests
{
    private string _root = null!;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-store-concurrency", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [TestMethod]
    public async Task ConcurrentAppends_PersistEveryMessage()
    {
        var store = new MarkdownSessionStore(_root);
        const string conversationId = "concurrent-appends";
        const int writers = 8;
        const int perWriter = 25;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++)
                store.AppendForTest(conversationId,
                    new ChatMessage(ChatRole.User, $"message w{w}-{i}"));
        })));

        var persisted = store.GetConversationMessages(conversationId);

        Assert.AreEqual(writers * perWriter, persisted.Count,
            "Every appended message must survive; a lost update means the watermark advanced " +
            "past a message that was never written to disk.");

        // The transcript on disk must agree with the in-memory projection — the watermark race
        // showed up as messages present in memory but missing from the .md file.
        var reloaded = new MarkdownSessionStore(_root).GetConversationMessages(conversationId);
        Assert.AreEqual(persisted.Count, reloaded.Count,
            "The .md transcript must contain the same messages as the live session.");
    }

    [TestMethod]
    public async Task ReadingWhileAppending_DoesNotThrow()
    {
        var store = new MarkdownSessionStore(_root);
        const string conversationId = "read-while-writing";

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
                store.AppendForTest(conversationId, new ChatMessage(ChatRole.User, $"msg {i++}"));
        });

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                // Both projections enumerate the shared message list.
                _ = store.GetConversationMessages(conversationId);
                _ = store.GetConversationToolActivities(conversationId);
                _ = store.GetLatestAssistantIndex(conversationId);
            }
        });

        // An unsynchronized reader surfaces as InvalidOperationException from either task.
        await Task.WhenAll(writer, reader);

        Assert.IsTrue(store.GetConversationMessages(conversationId).Count > 0);
    }

    [TestMethod]
    public void RepeatedSaves_DoNotDuplicateMessagesOnDisk()
    {
        var store = new MarkdownSessionStore(_root);
        const string conversationId = "no-duplicates";

        store.AppendForTest(conversationId, new ChatMessage(ChatRole.User, "only once"));
        store.AppendForTest(conversationId, new ChatMessage(ChatRole.Assistant, "answered once"));

        var reloaded = new MarkdownSessionStore(_root).GetConversationMessages(conversationId);

        Assert.AreEqual(2, reloaded.Count);
        Assert.AreEqual(1, reloaded.Count(m => m.Content == "only once"));
    }
}
