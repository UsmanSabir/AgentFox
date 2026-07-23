using AgentFox.Memory;
using Microsoft.Extensions.AI;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class ToolActivityProjectionTests
{
    [TestMethod]
    public void ReasoningContent_IsNotPersistedInTranscript()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reasoning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new MarkdownSessionStore(dir);
            store.AppendForTest("main", new ChatMessage(ChatRole.Assistant,
            [
                new TextReasoningContent("private chain of thought"),
                new TextContent("safe final answer")
            ]));

            var transcript = File.ReadAllText(Path.Combine(dir, "main.md"));
            Assert.IsFalse(transcript.Contains("private chain of thought"));
            StringAssert.Contains(transcript, "safe final answer");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void SessionMessagesHideToolDetails_WhileActivityProjectionCanRetrieveThem()
    {
        var dir = Path.Combine(Path.GetTempPath(), "toolactivity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new MarkdownSessionStore(dir);
            const string conversationId = "main";

            store.AppendForTest(conversationId, new ChatMessage(ChatRole.User, "calculate"));
            store.AppendForTest(conversationId, new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "call-1",
                    "calculator",
                    new Dictionary<string, object?> { ["expression"] = "2+2", ["apiKey"] = "secret" })
            ]));
            store.AppendForTest(conversationId, new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("call-1", """{"answer":4,"token":"secret"}""")
            ]));
            store.AppendForTest(conversationId, new ChatMessage(ChatRole.Assistant, "The answer is 4."));

            var messages = store.GetConversationMessages(conversationId);
            var activity = store.GetConversationToolActivities(conversationId);

            Assert.AreEqual(2, messages.Count);
            Assert.IsTrue(messages.All(message => !message.Content.Contains("calculator")));
            Assert.IsTrue(messages.All(message => !message.Content.Contains("secret")));
            Assert.AreEqual(1, activity.Count);
            Assert.AreEqual("calculator", activity[0].ToolName);
            Assert.AreEqual("completed", activity[0].Status);
            Assert.IsNotNull(activity[0].Arguments);
            StringAssert.Contains(activity[0].Result, "\"answer\":4");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
