using AgentFox.Learning;
using AgentFox.Plugins.Interfaces;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class ExperienceLearningTests
{
    private string _directory = null!;
    private string _path = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "agentfox-learning-tests", Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_directory, "experiences.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [TestMethod]
    public async Task FailureThenSuccess_IsPersistedAndRecalledByAnotherAgent()
    {
        var store = new JsonExperienceStore(_path);
        var learning = new ExperienceLearningService(store);
        var turn = learning.BeginTurn("authenticate broker API with bearer token", "trading-specialist");

        learning.RecordCurrent("broker_login", new() { ["token"] = "top-secret", ["mode"] = "query" }, ToolResult.Fail("401 unauthorized"));
        learning.RecordCurrent("broker_login", new() { ["token"] = "top-secret", ["mode"] = "header" }, ToolResult.Ok("authenticated"));
        await learning.CompleteAsync(turn, true);
        learning.EndTurn(turn);

        var all = await store.GetAllAsync();
        Assert.HasCount(1, all);
        Assert.AreEqual("[REDACTED]", all[0].Attempts[0].Arguments["token"]?.ToString());

        // The baseline is store-wide, not scoped to the source agent.
        var baseline = await learning.BuildBaselineAsync("use bearer token to authenticate broker API");
        StringAssert.Contains(baseline, "broker_login");
        StringAssert.Contains(baseline, "401 unauthorized");
        StringAssert.Contains(baseline, "source trading-specialist");
        Assert.IsFalse(baseline.Contains("top-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FailedOrUnverifiedTurn_IsNotLearned()
    {
        var store = new JsonExperienceStore(_path);
        var learning = new ExperienceLearningService(store);
        var turn = learning.BeginTurn("deploy service", "AgentFox");
        learning.RecordCurrent("deploy", new(), ToolResult.Fail("connection refused"));

        await learning.CompleteAsync(turn, false);

        Assert.IsEmpty(await store.GetAllAsync());
    }

    [TestMethod]
    public async Task RepeatedStrategy_IncreasesEvidenceAndConfidence()
    {
        var store = new JsonExperienceStore(_path);
        var learning = new ExperienceLearningService(store);

        for (var i = 0; i < 2; i++)
        {
            var turn = learning.BeginTurn("repair database migration", "AgentFox");
            learning.RecordCurrent("migrate", new() { ["mode"] = "force" }, ToolResult.Fail("locked"));
            learning.RecordCurrent("migrate", new() { ["mode"] = "retry" }, ToolResult.Ok("done"));
            await learning.CompleteAsync(turn, true);
            learning.EndTurn(turn);
        }

        var learned = (await store.GetAllAsync()).Single();
        Assert.AreEqual(2, learned.SuccessCount);
        Assert.IsGreaterThan(0.6, learned.Confidence);
    }

    [TestMethod]
    public async Task NestedAgentTurn_RestoresParentTrace()
    {
        var store = new JsonExperienceStore(_path);
        var learning = new ExperienceLearningService(store);
        var parent = learning.BeginTurn("repair parent workflow", "AgentFox");
        learning.RecordCurrent("parent_step", new(), ToolResult.Fail("first attempt failed"));

        var specialist = learning.BeginTurn("inspect specialist state", "specialist");
        learning.RecordCurrent("inspect", new(), ToolResult.Ok("inspected"));
        learning.EndTurn(specialist);

        learning.RecordCurrent("parent_step", new() { ["mode"] = "retry" }, ToolResult.Ok("repaired"));
        await learning.CompleteAsync(parent, true);
        learning.EndTurn(parent);

        var learned = (await store.GetAllAsync()).Single();
        Assert.AreEqual("AgentFox", learned.SourceAgent);
        Assert.HasCount(2, learned.Attempts);
        Assert.AreEqual("parent_step", learned.Attempts[1].ToolName);
    }
}
