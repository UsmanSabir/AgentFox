using AgentFox.Memory;
using AgentFox.Plugins.Research;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class ReferencesSidecarTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reftests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Writes a minimal two-turn conversation (user/assistant x2) directly through the store's
    // append path so GetConversationMessages has assistant snapshots to attach references to.
    private static void SeedTwoTurns(MarkdownSessionStore store, string convId)
    {
        // Turn 1
        store.AppendForTest(convId, new ChatMessage(ChatRole.User, "research OGDC"));
        store.AppendForTest(convId, new ChatMessage(ChatRole.Assistant, "OGDC looks fine."));
        // Turn 2
        store.AppendForTest(convId, new ChatMessage(ChatRole.User, "and LUCK?"));
        store.AppendForTest(convId, new ChatMessage(ChatRole.Assistant, "LUCK looks fine too."));
    }

    [TestMethod]
    public void PersistAndRead_AttachesReferencesToCorrectAssistantSnapshot()
    {
        var dir = NewTempDir();
        var store = new MarkdownSessionStore(dir);
        const string conv = "main";
        SeedTwoTurns(store, conv);

        // Only the SECOND assistant reply (index 1) has references.
        store.PersistAssistantReferences(conv, new List<ResearchReference>
        {
            new("https://news.example.com/luck", "LUCK rallies", "Business Times")
        });

        var msgs = store.GetConversationMessages(conv);
        var assistants = msgs.Where(m => m.Role == "assistant").ToList();

        Assert.AreEqual(2, assistants.Count);
        Assert.AreEqual(0, assistants[0].References.Count);
        Assert.AreEqual(1, assistants[1].References.Count);
        Assert.AreEqual("https://news.example.com/luck", assistants[1].References[0].Url);
    }

    [TestMethod]
    public void PersistAssistantReferences_EmptyList_WritesNothing()
    {
        var dir = NewTempDir();
        var store = new MarkdownSessionStore(dir);
        const string conv = "main";
        SeedTwoTurns(store, conv);

        store.PersistAssistantReferences(conv, new List<ResearchReference>());

        var msgs = store.GetConversationMessages(conv);
        Assert.IsTrue(msgs.Where(m => m.Role == "assistant").All(a => a.References.Count == 0));
    }
}
