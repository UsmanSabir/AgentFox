using AgentFox.Memory;
using AgentFox.Sessions;
using AgentFox.Tools;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class SessionForkTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "agentfox-session-fork-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort: a failed assertion should remain the useful test failure.
        }
    }

    [TestMethod]
    public void ForkWebSession_CopiesRawPrefixAndLeavesSourceUnchanged()
    {
        using var manager = CreateManager();
        const string sourceId = "web_fork_source";
        manager.GetOrCreateWebSession("main", sourceId);
        manager.RenameSession(sourceId, "Planning");
        manager.SetSessionMemoryEnabled(sourceId, false);

        var sourcePath = manager.ConversationFilePath(sourceId);
        File.WriteAllText(sourcePath, FullTranscript(sourceId));
        var original = File.ReadAllText(sourcePath);

        var forkId = manager.ForkWebSession(sourceId, assistantIndex: 1);
        var forkPath = manager.ConversationFilePath(forkId);
        var forked = File.ReadAllText(forkPath);

        Assert.AreNotEqual(sourceId, forkId);
        Assert.AreEqual(original, File.ReadAllText(sourcePath), "The source transcript changed.");
        StringAssert.Contains(forked, "first answer");
        StringAssert.Contains(forked, "[tool_call]");
        StringAssert.Contains(forked, "[tool_result]");
        StringAssert.Contains(forked, "The answer is 4.");
        Assert.IsFalse(forked.Contains("later question"));
        Assert.IsFalse(forked.Contains("later answer"));
        StringAssert.Contains(forked, $"sessionId: {forkId}");

        var info = manager.GetSession(forkId);
        Assert.IsNotNull(info);
        Assert.AreEqual(SessionOrigin.Web, info.Origin);
        Assert.AreEqual(SessionStatus.Idle, info.Status);
        Assert.AreEqual("main", info.AgentId);
        Assert.AreEqual(false, info.MemoryEnabled);
        Assert.AreEqual("Fork of Planning", info.Title);
        Assert.AreEqual(sourceId, info.ForkedFromSessionId);
        Assert.AreEqual(1, info.ForkedAtAssistantIndex);

        var messages = new MarkdownSessionStore(manager.SessionDirectory)
            .GetConversationMessages(forkId);
        CollectionAssert.AreEqual(
            new[] { "first question", "first answer", "calculate", "The answer is 4." },
            messages.Select(message => message.Content).ToArray());
        CollectionAssert.AreEqual(
            new int?[] { null, 0, null, 1 },
            messages.Select(message => message.AssistantIndex).ToArray());
    }

    [TestMethod]
    public void ForkWebSession_CopiesOnlyReferencesWithinCutoffAndNoProviderState()
    {
        using var manager = CreateManager();
        const string sourceId = "web_fork_sidecars";
        manager.GetOrCreateWebSession("main", sourceId);
        var sourcePath = manager.ConversationFilePath(sourceId);
        File.WriteAllText(sourcePath, FullTranscript(sourceId));
        File.WriteAllText(
            sourcePath + MarkdownSessionStore.ReferencesSidecarSuffix,
            """
            {"i":0,"items":[{"title":"one","url":"https://example.com/one"}]}
            {"i":1,"items":[{"title":"two","url":"https://example.com/two"}]}
            {"i":2,"items":[{"title":"three","url":"https://example.com/three"}]}
            """ + "\n");
        File.WriteAllText(
            sourcePath + MarkdownSessionStore.StateSidecarSuffix,
            """{"savedAt":"2026-07-23T00:00:00Z","stateBag":{"TodoProvider":{"items":[]}}}""");

        var forkId = manager.ForkWebSession(sourceId, assistantIndex: 1);
        var forkPath = manager.ConversationFilePath(forkId);
        var references = File.ReadAllText(
            forkPath + MarkdownSessionStore.ReferencesSidecarSuffix);

        StringAssert.Contains(references, "https://example.com/one");
        StringAssert.Contains(references, "https://example.com/two");
        Assert.IsFalse(references.Contains("https://example.com/three"));
        Assert.IsFalse(File.Exists(forkPath + MarkdownSessionStore.StateSidecarSuffix));
        Assert.IsFalse(File.Exists(forkPath + ".pending"));
    }

    [TestMethod]
    public void ForkWebSession_RejectsBusyAndUnknownForkPoints()
    {
        using var manager = CreateManager();
        const string sourceId = "web_fork_busy";
        manager.GetOrCreateWebSession("main", sourceId);
        var sourcePath = manager.ConversationFilePath(sourceId);
        File.WriteAllText(sourcePath, FullTranscript(sourceId));
        File.WriteAllText(sourcePath + ".pending", "still running");

        var busy = Assert.ThrowsExactly<InvalidOperationException>(
            () => manager.ForkWebSession(sourceId, assistantIndex: 0));
        Assert.AreEqual("session_busy", busy.Message);

        File.Delete(sourcePath + ".pending");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => manager.ForkWebSession(sourceId, assistantIndex: 99));
        Assert.ThrowsExactly<KeyNotFoundException>(
            () => manager.ForkWebSession("web_missing", assistantIndex: 0));
    }

    [TestMethod]
    public void SpecialistFork_PreservesNamespaceAndLineageAcrossRestart()
    {
        const string sourceId = "specialist/trading-agent/web_source";
        string forkId;

        using (var manager = CreateManager())
        {
            manager.GetOrCreateWebSession("trading-agent", sourceId);
            var sourcePath = manager.ConversationFilePath(sourceId);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, FullTranscript(sourceId));
            forkId = manager.ForkWebSession(sourceId, assistantIndex: 0);

            StringAssert.StartsWith(
                forkId, "specialist/trading-agent/web_",
                "A specialist fork must remain routable through the specialist endpoint.");
            Assert.AreEqual("trading-agent", manager.GetSession(forkId)!.AgentId);
        }

        using var reloaded = CreateManager();
        var persisted = reloaded.GetSession(forkId);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(sourceId, persisted.ForkedFromSessionId);
        Assert.AreEqual(0, persisted.ForkedAtAssistantIndex);
    }

    private SessionManager CreateManager()
    {
        var workspace = new WorkspaceManager([_root], restrictToWorkspace: false);
        return new SessionManager(new SessionConfig
        {
            SessionDirectory = "sessions",
            ArchiveDirectory = "archive/sessions",
            BackgroundCheckIntervalSeconds = 3600
        }, workspace);
    }

    private static string FullTranscript(string sessionId) =>
        """
        ---
        sessionId: SOURCE_SESSION
        createdAt: 2026-07-23T00:00:00Z
        ---

        # Chat Log

        ### user

        first question

        ### assistant

        first answer

        ### user

        calculate

        ### assistant

        [tool_call] {"callId":"c1","name":"calculator","arguments":{"expression":"2+2"}}

        ### tool

        [tool_result] {"callId":"c1","result":"4"}

        ### assistant

        The answer is 4.

        ### user

        later question

        ### assistant

        later answer
        """.Replace("SOURCE_SESSION", sessionId, StringComparison.Ordinal);
}
