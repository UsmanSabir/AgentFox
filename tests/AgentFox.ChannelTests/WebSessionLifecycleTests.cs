using AgentFox.Memory;
using AgentFox.Sessions;
using AgentFox.Tools;
using AgentFox.Agents;
using AgentFox.Plugins.Interfaces;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class WebSessionLifecycleTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-web-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void WebSession_IsRegisteredAndRejectsTraversal()
    {
        using var manager = CreateManager();

        var id = manager.GetOrCreateWebSession("main");
        var session = manager.GetSession(id);

        Assert.IsNotNull(session);
        Assert.AreEqual(SessionOrigin.Web, session.Origin);
        Assert.AreEqual(SessionStatus.Active, session.Status);
        Assert.ThrowsExactly<ArgumentException>(() =>
            manager.GetOrCreateWebSession("main", "../../outside"));
    }

    [TestMethod]
    public void ArchivedWebSession_CanBeResumedWithHistory()
    {
        using var manager = CreateManager();
        const string id = "web_resume_test";
        manager.GetOrCreateWebSession("main", id);
        File.WriteAllText(manager.ConversationFilePath(id), """
            ---
            sessionId: web_resume_test
            createdAt: 2026-01-01T00:00:00Z
            ---

            # Chat Log

            ### user

            hello

            ### assistant

            welcome back
            """);

        manager.ArchiveSession(id);
        Assert.AreEqual(SessionStatus.Archived, manager.GetSession(id)!.Status);
        Assert.IsTrue(manager.ResumeSession(id));

        var store = new MarkdownSessionStore(manager.SessionDirectory);
        var messages = store.GetConversationMessages(id);
        Assert.AreEqual(2, messages.Count);
        Assert.AreEqual("hello", messages[0].Content);
        Assert.AreEqual("welcome back", messages[1].Content);
    }

    [TestMethod]
    public void ExistingUnindexedMarkdown_IsDiscoveredAsWebSession()
    {
        var sessionDir = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(Path.Combine(sessionDir, "legacy-web.md"), "# Chat Log");

        using var manager = CreateManager();
        var session = manager.GetSession("legacy-web");

        Assert.IsNotNull(session);
        Assert.AreEqual(SessionOrigin.Web, session.Origin);
        Assert.AreEqual(SessionStatus.Idle, session.Status);
    }

    [TestMethod]
    public void StrongRouteHint_ForcesOnlyExactSpecialistHint()
    {
        var registry = new SpecialistAgentRegistry();
        registry.Register(new SpecialistAgentDescriptor
        {
            Id = "trading-agent",
            Name = "Trading",
            SystemPrompt = "test",
            RouteHints = ["stock", "market"],
            StrongRouteHints = ["PSX"]
        });
        var tool = new DelegateToAgentTool(registry);

        Assert.IsTrue(tool.ShouldRequireDelegation("check if PSX is open?"));
        Assert.IsFalse(tool.ShouldRequireDelegation("summarize general market trends"));
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
}
