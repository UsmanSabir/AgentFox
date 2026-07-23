using AgentFox.Sessions;
using AgentFox.Tools;

namespace AgentFox.ChannelTests;

/// <summary>
/// A session is not one file. Alongside <c>{id}.md</c> sit the crash-recovery pending message,
/// the research references, and the persisted todo list (<c>.state.json</c>). Archive, resume,
/// and delete must treat the whole set as one unit — a <c>.state.json</c> left behind in the
/// active directory would hand its stale todo list to the next session at that path.
/// </summary>
[TestClass]
public sealed class SessionSidecarLifecycleTests
{
    private string _root = string.Empty;

    private static readonly string[] Sidecars = [".pending", ".refs.jsonl", ".state.json"];

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox_sidecar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [TestMethod]
    public void ArchiveThenResume_CarriesTheTodoListWithTheTranscript()
    {
        using var manager = CreateManager();
        var id = manager.GetOrCreateWebSession("main", "web_archive_probe");
        var mdPath = SeedSession(manager, id);

        manager.ArchiveSession(id);

        Assert.IsFalse(File.Exists(mdPath), "transcript should have moved to the archive");
        foreach (var suffix in Sidecars)
            Assert.IsFalse(File.Exists(mdPath + suffix),
                $"{suffix} was orphaned in the active directory instead of following the transcript.");

        Assert.IsTrue(manager.ResumeSession(id));

        Assert.IsTrue(File.Exists(mdPath));
        foreach (var suffix in Sidecars)
            Assert.IsTrue(File.Exists(mdPath + suffix), $"{suffix} did not come back on resume.");

        StringAssert.Contains(File.ReadAllText(mdPath + ".state.json"), "\"TodoProvider\"",
            "The restored todo list must be the one archived with this session.");
    }

    [TestMethod]
    public void DeleteSession_RemovesEverySidecar()
    {
        using var manager = CreateManager();
        var id = manager.GetOrCreateWebSession("main", "web_delete_probe");
        var mdPath = SeedSession(manager, id);

        Assert.IsTrue(manager.DeleteSession(id));

        Assert.IsFalse(File.Exists(mdPath));
        foreach (var suffix in Sidecars)
            Assert.IsFalse(File.Exists(mdPath + suffix),
                $"{suffix} survived a session delete and would leak into a session reusing this ID.");
    }

    [TestMethod]
    public void DeleteArchivedSession_RemovesSidecarsFromTheArchiveToo()
    {
        using var manager = CreateManager();
        var id = manager.GetOrCreateWebSession("main", "web_archived_delete");
        SeedSession(manager, id);

        manager.ArchiveSession(id);
        var archived = Directory.EnumerateFiles(
            Path.Combine(_root, "archive", "sessions"), "*", SearchOption.AllDirectories).ToList();
        Assert.IsTrue(archived.Count > 1, "archive should hold the transcript plus its sidecars");

        Assert.IsTrue(manager.DeleteSession(id));

        var leftovers = Directory.EnumerateFiles(
            Path.Combine(_root, "archive", "sessions"), "*", SearchOption.AllDirectories).ToList();
        Assert.AreEqual(0, leftovers.Count,
            "Deleting a session must clear its archived transcript and sidecars: " +
            string.Join(", ", leftovers));
    }

    [TestMethod]
    public void FreshSessionAtAReusedPath_DoesNotInheritAnOldTodoList()
    {
        using var manager = CreateManager();
        var id = manager.GetOrCreateWebSession("main", "web_reuse_probe");
        var mdPath = SeedSession(manager, id);

        manager.DeleteSession(id);
        manager.GetOrCreateWebSession("main", "web_reuse_probe");

        Assert.IsFalse(File.Exists(mdPath + ".state.json"),
            "A recreated session must start with no todo list, not inherit the deleted one.");
    }

    /// <summary>Writes a transcript plus one of every sidecar, mirroring a live session.</summary>
    private string SeedSession(SessionManager manager, string sessionId)
    {
        var mdPath = manager.ConversationFilePath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(mdPath)!);

        File.WriteAllText(mdPath, "---\nsessionId: " + sessionId + "\n---\n\n# Chat Log\n");
        File.WriteAllText(mdPath + ".pending", "an unanswered question");
        File.WriteAllText(mdPath + ".refs.jsonl", "{\"i\":0,\"items\":[]}\n");
        File.WriteAllText(mdPath + ".state.json",
            "{\"savedAt\":\"2026-07-23T00:00:00+00:00\",\"stateBag\":{\"TodoProvider\":{\"items\":[],\"nextId\":1}}}");

        return mdPath;
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
