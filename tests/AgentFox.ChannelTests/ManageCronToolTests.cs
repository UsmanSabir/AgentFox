using AgentFox.Plugins.Interfaces;
using AgentFox.Runtime;
using AgentFox.Tools;

namespace AgentFox.ChannelTests;

/// <summary>
/// The tool only offered add / remove / list, so there was no way to change an existing job.
/// Faced with "already exists, remove it first" the model's usual next move is to retry under a
/// slightly different name — which is how the same work ended up scheduled twice under
/// "hourly-war-news-update" and "war-news-pkt-daytime-single", each firing its own copy.
/// </summary>
[TestClass]
public sealed class ManageCronToolTests
{
    private string _root = string.Empty;
    private CronScheduler _scheduler = null!;
    private ManageCronTool _tool = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox_cron_tool_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // The agent is only dereferenced when a job actually fires. Nothing here starts the timer,
        // so the scheduling surface under test never touches it.
        _scheduler = new CronScheduler(null!, jobsFilePath: Path.Combine(_root, "cron.md"));
        _tool = new ManageCronTool(() => _scheduler);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _scheduler.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private Task<ToolResult> Run(params (string key, object? value)[] args) =>
        _tool.ExecuteAsync(args.ToDictionary(a => a.key, a => a.value));

    private const string DailySummary = "0 6 * * 1-5";

    private async Task SeedPsxJob() =>
        await Run(
            ("operation", "add"),
            ("name", "psx-daily-summary"),
            ("cron", DailySummary),
            ("task", "Produce today's PSX summary and send it to the user once."));

    // ── Duplicate prevention ──────────────────────────────────────────────────

    [TestMethod]
    public async Task ExactDuplicateName_IsRejectedAndPointedAtUpdate()
    {
        await SeedPsxJob();

        var again = await Run(
            ("operation", "add"),
            ("name", "psx-daily-summary"),
            ("cron", DailySummary),
            ("task", "Produce today's PSX summary."));

        Assert.IsFalse(again.Success);
        StringAssert.Contains(again.Output + again.Error, "update",
            "A bare rejection is what pushed the caller into inventing a second name.");
        Assert.AreEqual(1, _scheduler.GetJobs().Count);
    }

    [TestMethod]
    public async Task NameDifferingOnlyByCaseAndPunctuation_IsRejected()
    {
        await SeedPsxJob();

        var similar = await Run(
            ("operation", "add"),
            ("name", "PSX Daily Summary"),
            ("cron", DailySummary),
            ("task", "Produce today's PSX summary."));

        Assert.IsFalse(similar.Success,
            "'PSX Daily Summary' would schedule the same report a second time.");
        StringAssert.Contains(similar.Output + similar.Error, "psx-daily-summary",
            "The rejection must name the job that already covers this.");
        Assert.AreEqual(1, _scheduler.GetJobs().Count);
    }

    [TestMethod]
    public async Task UnderscoreVariantIsAlsoRecognised()
    {
        await SeedPsxJob();

        var result = await Run(
            ("operation", "add"),
            ("name", "psx_daily_summary"),
            ("cron", DailySummary),
            ("task", "Produce today's PSX summary."));

        Assert.IsFalse(result.Success);
        Assert.AreEqual(1, _scheduler.GetJobs().Count);
    }

    [TestMethod]
    public async Task GenuinelyDifferentJob_IsStillAccepted()
    {
        await SeedPsxJob();

        var other = await Run(
            ("operation", "add"),
            ("name", "war-news-digest"),
            ("cron", "0 * * * *"),
            ("task", "Summarize the last hour of war news."));

        Assert.IsTrue(other.Success, "Name checking must not block an unrelated job.");
        Assert.AreEqual(2, _scheduler.GetJobs().Count);
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Update_ChangesScheduleWithoutLosingRunHistory()
    {
        await SeedPsxJob();

        // Pretend the job already ran today.
        var ranAt = new DateTime(2026, 8, 4, 6, 0, 0, DateTimeKind.Utc);
        _scheduler.GetJob("psx-daily-summary")!.LastExecuted = ranAt;

        var updated = await Run(
            ("operation", "update"),
            ("name", "psx-daily-summary"),
            ("cron", "30 6 * * 1-5"));

        Assert.IsTrue(updated.Success);

        var job = _scheduler.GetJob("psx-daily-summary")!;
        Assert.AreEqual("30 6 * * 1-5", job.CronExpression);
        Assert.AreEqual(ranAt, job.LastExecuted,
            "Changing the schedule must not discard the run history and re-run today.");
        StringAssert.Contains(job.Task, "send it to the user once",
            "Omitting 'task' must leave the existing task intact.");
    }

    [TestMethod]
    public async Task Update_CanChangeTaskAlone()
    {
        await SeedPsxJob();

        var updated = await Run(
            ("operation", "update"),
            ("name", "psx-daily-summary"),
            ("task", "Produce the summary, include market breadth, and send it once."));

        Assert.IsTrue(updated.Success);

        var job = _scheduler.GetJob("psx-daily-summary")!;
        Assert.AreEqual(DailySummary, job.CronExpression, "The schedule must be preserved.");
        StringAssert.Contains(job.Task, "market breadth");
    }

    [TestMethod]
    public async Task Update_ResolvesANearMissName()
    {
        await SeedPsxJob();

        var updated = await Run(
            ("operation", "update"),
            ("name", "PSX Daily Summary"),
            ("cron", "0 7 * * 1-5"));

        Assert.IsTrue(updated.Success,
            "A caller that remembers the job under a different spelling should still reach it.");
        Assert.AreEqual("0 7 * * 1-5", _scheduler.GetJob("psx-daily-summary")!.CronExpression);
        Assert.AreEqual(1, _scheduler.GetJobs().Count, "Update must not create a second job.");
    }

    [TestMethod]
    public async Task Update_RequiresSomethingToChange()
    {
        await SeedPsxJob();

        var result = await Run(("operation", "update"), ("name", "psx-daily-summary"));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Output + result.Error, "cron");
    }

    [TestMethod]
    public async Task Update_OnUnknownJob_SuggestsListAndAdd()
    {
        var result = await Run(
            ("operation", "update"), ("name", "not-a-job"), ("cron", "0 6 * * *"));

        Assert.IsFalse(result.Success);
        var text = result.Output + result.Error;
        StringAssert.Contains(text, "list");
        StringAssert.Contains(text, "add");
    }

    [TestMethod]
    public async Task JobLookupIsCaseInsensitive()
    {
        await SeedPsxJob();

        Assert.IsNotNull(_scheduler.GetJob("PSX-DAILY-SUMMARY"),
            "Case-sensitive keys let two jobs exist for the same report.");
    }
}
