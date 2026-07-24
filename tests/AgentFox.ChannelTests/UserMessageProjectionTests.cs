using AgentFox.Memory;
using Microsoft.Extensions.AI;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class UserMessageProjectionTests
{
    private string _root = null!;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-user-projection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void LearnedBaseline_IsSeparatedFromOriginalUserMessage()
    {
        var store = new MarkdownSessionStore(_root);
        const string conversationId = "baseline";
        store.AppendForTest(conversationId, new ChatMessage(ChatRole.User,
            """
            [Shared learned baseline from previously verified attempts:]
            - Strategy: search(query=today)
              Evidence: 1 successful run(s), confidence 0.60, source AgentFox.
            Use this only when current preconditions match, and verify the result again.

            Search the web for today's news.
            """));

        var message = store.GetConversationMessages(conversationId).Single();

        Assert.AreEqual("Search the web for today's news.", message.Content);
        StringAssert.StartsWith(message.AgentAddition, "[Shared learned baseline");
        Assert.IsFalse(message.Content.Contains("Strategy:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MemoryAndBaseline_AreCombinedAsAgentAddition()
    {
        var store = new MarkdownSessionStore(_root);
        const string conversationId = "memory-and-baseline";
        store.AppendForTest(conversationId, new ChatMessage(ChatRole.User,
            """
            [Relevant context from long-term memory:]
            - [Fact] The user's name is Usman.

            [Shared learned baseline from previously verified attempts:]
            - Strategy: add_memory(content=Usman)
              Evidence: 1 successful run(s), confidence 0.60, source AgentFox.
            Use this only when current preconditions match, and verify the result again.

            What is my name?
            """));

        var message = store.GetConversationMessages(conversationId).Single();

        Assert.AreEqual("What is my name?", message.Content);
        StringAssert.Contains(message.AgentAddition, "[Relevant context from long-term memory:]");
        StringAssert.Contains(message.AgentAddition, "[Shared learned baseline");
    }

    [TestMethod]
    public void OrdinaryUserMessage_IsNotChanged()
    {
        var store = new MarkdownSessionStore(_root);
        const string conversationId = "ordinary";
        const string content = "Please explain [Relevant context from long-term memory:] as plain text.";
        store.AppendForTest(conversationId, new ChatMessage(ChatRole.User, content));

        var message = store.GetConversationMessages(conversationId).Single();

        Assert.AreEqual(content, message.Content);
        Assert.IsNull(message.AgentAddition);
    }
}
