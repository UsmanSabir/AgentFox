using AgentFox.Runtime;

namespace AgentFox.ChannelTests;

/// <summary>
/// A cron job's task is a full agent turn — searches, sub-agents, channel sends — and routinely
/// runs for minutes, while the scheduler's timer keeps ticking every 60 seconds throughout.
///
/// The schedule used to be advanced only *after* the run finished, so every tick in between still
/// saw the job as due and started another complete, independent run of it. In production a job
/// configured as "0 6 * * 1-5" fired once a minute until the first run happened to finish: 3 runs
/// one day, 10 another, and 81 consecutive runs for one job that stalled — each copy doing its own
/// research and its own delivery to the user's channels.
///
/// These tests pin the claim-before-run policy that stops it, plus the persistence round-trip that
/// used to silently mangle model-authored task strings.
/// </summary>
[TestClass]
public sealed class CronSchedulerRunawayTests
{
    private static readonly DateTime Base = new(2026, 8, 4, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>Stand-in for Cronos: a daily job, next occurrence 24h out.</summary>
    private static Func<string, DateTime> DailyFrom(DateTime now) => _ => now.AddDays(1);

    private static CronJob Job(string name, DateTime nextExecution, string cron = "0 6 * * 1-5") =>
        new()
        {
            Name = name,
            CronExpression = cron,
            Task = "Produce the daily summary and send it to the user.",
            LastExecuted = DateTime.MinValue,
            NextExecution = nextExecution
        };

    // ── The runaway ───────────────────────────────────────────────────────────

    [TestMethod]
    public void DueJob_IsClaimedOnlyOnce_AcrossRepeatedTicks()
    {
        var job = Job("psx-daily-summary", Base);
        var jobs = new[] { job };

        // Tick 1: due, so it is claimed and dispatched.
        var first = CronScheduler.ClaimDueJobs(jobs, Base, DailyFrom(Base));
        Assert.AreEqual(1, first.Count, "The job was due and should have been claimed.");

        // Ticks 2..60: the run is still in flight. Not one of them may claim it again.
        for (var minute = 1; minute <= 60; minute++)
        {
            var now = Base.AddMinutes(minute);
            var again = CronScheduler.ClaimDueJobs(jobs, now, DailyFrom(now));
            Assert.AreEqual(0, again.Count,
                $"Tick at +{minute}min started a second concurrent run of the same job. "
                + "This is the defect that sent the user one summary per minute.");
        }
    }

    [TestMethod]
    public void ClaimAdvancesScheduleImmediately_NotAfterTheRun()
    {
        var job = Job("psx-daily-summary", Base);

        CronScheduler.ClaimDueJobs([job], Base, DailyFrom(Base));

        Assert.IsTrue(job.IsRunning, "A claimed job must be marked in-flight.");
        Assert.AreEqual(Base.AddDays(1), job.NextExecution,
            "The schedule must move forward at claim time; deferring it to after the run is "
            + "exactly what let intervening ticks re-fire the job.");
        Assert.AreEqual(Base, job.LastExecuted, "Claiming stamps the run's start time.");
    }

    [TestMethod]
    public void ReleasingTheClaim_AllowsTheNextScheduledOccurrence()
    {
        var job = Job("psx-daily-summary", Base);
        CronScheduler.ClaimDueJobs([job], Base, DailyFrom(Base));

        job.IsRunning = false;   // run completed

        // Still inside the same day: the advanced schedule keeps it from re-firing.
        var sameDay = Base.AddHours(1);
        Assert.AreEqual(0, CronScheduler.ClaimDueJobs([job], sameDay, DailyFrom(sameDay)).Count,
            "A finished run must not re-fire before its next scheduled occurrence.");

        // Tomorrow: due again.
        var tomorrow = Base.AddDays(1);
        Assert.AreEqual(1, CronScheduler.ClaimDueJobs([job], tomorrow, DailyFrom(tomorrow)).Count,
            "The job must resume firing on its next occurrence.");
    }

    [TestMethod]
    public void AnOverrunningJob_DoesNotImmediatelyRefire_OnRelease()
    {
        // A job whose run takes longer than its own interval — the every-minute case.
        var job = Job("noisy", Base, cron: "* * * * *");
        Func<string, DateTime> everyMinute = _ => Base.AddMinutes(1);

        CronScheduler.ClaimDueJobs([job], Base, everyMinute);
        Assert.AreEqual(Base.AddMinutes(1), job.NextExecution);

        // The run took ten minutes. On release the schedule is already past, so it fires once
        // more — not once per minute of the overrun.
        job.IsRunning = false;
        var after = Base.AddMinutes(10);
        Assert.AreEqual(1, CronScheduler.ClaimDueJobs([job], after, _ => after.AddMinutes(1)).Count);
        Assert.AreEqual(0, CronScheduler.ClaimDueJobs([job], after, _ => after.AddMinutes(1)).Count,
            "A single catch-up run, not one per missed tick.");
    }

    [TestMethod]
    public void OneWedgedJob_DoesNotBlockOthersFromBeingClaimed()
    {
        var wedged = Job("wedged", Base);
        var healthy = Job("healthy", Base);

        CronScheduler.ClaimDueJobs([wedged, healthy], Base, DailyFrom(Base));
        healthy.IsRunning = false;             // finished normally
                                               // wedged stays IsRunning — never returned

        var tomorrow = Base.AddDays(1);
        var due = CronScheduler.ClaimDueJobs([wedged, healthy], tomorrow, DailyFrom(tomorrow));

        Assert.AreEqual(1, due.Count);
        Assert.AreEqual("healthy", due[0].Name,
            "A wedged job must go quiet, not starve the others and not re-fire itself.");
    }

    [TestMethod]
    public void JobsNotYetDue_AreLeftAlone()
    {
        var future = Job("later", Base.AddHours(5));

        Assert.AreEqual(0, CronScheduler.ClaimDueJobs([future], Base, DailyFrom(Base)).Count);
        Assert.IsFalse(future.IsRunning);
        Assert.AreEqual(Base.AddHours(5), future.NextExecution, "An untouched job keeps its schedule.");
    }

    // ── Persistence round-trip ────────────────────────────────────────────────

    [TestMethod]
    public void MultiLineTask_SurvivesTheRoundTrip()
    {
        // Model-authored tasks routinely look like this. Written raw into a markdown table they
        // broke the row, and the reader then truncated the task at the first newline.
        var task =
            "Produce today's PSX summary:\n"
            + "1. Fetch the KSE-100 close\n"
            + "2. Compare against the portfolio\n"
            + "3. Send it to the user once";

        var original = new CronJob
        {
            Name = "psx-daily-summary",
            CronExpression = "0 6 * * 1-5",
            Task = task,
            LastExecuted = Base,
            NextExecution = Base.AddDays(1)
        };

        var lines = CronScheduler.SerializeJobs([original]).Split('\n');
        var restored = CronScheduler.DeserializeJobs(lines, Base, DailyFrom(Base));

        Assert.AreEqual(1, restored.Count, "The job must survive a save/load cycle.");
        Assert.AreEqual(task, restored[0].Task, "The task must round-trip byte for byte.");
        Assert.AreEqual("0 6 * * 1-5", restored[0].CronExpression);
        Assert.AreEqual("psx-daily-summary", restored[0].Name);
    }

    [TestMethod]
    public void TaskContainingPipesAndBackslashes_SurvivesTheRoundTrip()
    {
        var task = @"Run `ps | grep agent` and report C:\logs\out.txt | tee summary.txt";
        var original = new CronJob
        {
            Name = "pipes",
            CronExpression = "0 * * * *",
            Task = task,
            NextExecution = Base.AddDays(1)
        };

        var lines = CronScheduler.SerializeJobs([original]).Split('\n');
        var restored = CronScheduler.DeserializeJobs(lines, Base, DailyFrom(Base));

        Assert.AreEqual(1, restored.Count,
            "A pipe inside a task used to split the row and invent extra columns.");
        Assert.AreEqual(task, restored[0].Task);
    }

    [TestMethod]
    public void TaskMentioningTheWordName_IsNotDroppedOnLoad()
    {
        var original = new CronJob
        {
            Name = "report",
            CronExpression = "0 6 * * *",
            Task = "Look up the Name field on each holding and report it.",
            NextExecution = Base.AddDays(1)
        };

        var lines = CronScheduler.SerializeJobs([original]).Split('\n');
        var restored = CronScheduler.DeserializeJobs(lines, Base, DailyFrom(Base));

        Assert.AreEqual(1, restored.Count,
            "Header detection must test the first cell, not scan the whole line for 'Name'.");
    }

    [TestMethod]
    public void EmptyScheduleRoundTripsToNoJobs()
    {
        var lines = CronScheduler.SerializeJobs([]).Split('\n');
        Assert.AreEqual(0, CronScheduler.DeserializeJobs(lines, Base, DailyFrom(Base)).Count,
            "The '(none configured)' placeholder row must not load as a job.");
    }

    [TestMethod]
    public void PersistedFutureOccurrence_IsHonouredAcrossRestart()
    {
        // The job already ran today; the file records tomorrow as next.
        var job = new CronJob
        {
            Name = "psx-daily-summary",
            CronExpression = "0 6 * * 1-5",
            Task = "Send the summary.",
            LastExecuted = Base,
            NextExecution = Base.AddDays(1)
        };

        var lines = CronScheduler.SerializeJobs([job]).Split('\n');

        // Restart a minute later. A recomputed-from-now schedule would make it due again.
        var restartedAt = Base.AddMinutes(1);
        var restored = CronScheduler.DeserializeJobs(lines, restartedAt, DailyFrom(restartedAt));

        Assert.AreEqual(1, restored.Count);
        Assert.AreEqual(Base.AddDays(1), restored[0].NextExecution,
            "A restart must not re-run an occurrence that already fired.");
        Assert.AreEqual(0, CronScheduler.ClaimDueJobs(restored, restartedAt, DailyFrom(restartedAt)).Count);
    }

    [TestMethod]
    public void PastOccurrence_IsSkippedRatherThanFiredOnStartup()
    {
        var job = new CronJob
        {
            Name = "missed",
            CronExpression = "0 6 * * 1-5",
            Task = "Send the summary.",
            LastExecuted = Base.AddDays(-1),
            NextExecution = Base            // was due while the process was down
        };

        var lines = CronScheduler.SerializeJobs([job]).Split('\n');

        var restartedAt = Base.AddHours(6);
        var restored = CronScheduler.DeserializeJobs(lines, restartedAt, DailyFrom(restartedAt));

        Assert.AreEqual(restartedAt.AddDays(1), restored[0].NextExecution,
            "A missed occurrence is skipped, not fired the instant the process comes back.");
    }

    [TestMethod]
    public void LegacyThreeColumnFile_StillLoads()
    {
        string[] legacy =
        [
            "| Name | Cron | Task |",
            "|------|------|------|",
            "| psx-daily-summary | 0 6 * * 1-5 | Send the summary. |"
        ];

        var restored = CronScheduler.DeserializeJobs(legacy, Base, DailyFrom(Base));

        Assert.AreEqual(1, restored.Count, "Files written before the timing columns must still load.");
        Assert.AreEqual("Send the summary.", restored[0].Task);
        Assert.AreEqual(DateTime.MinValue, restored[0].LastExecuted);
        Assert.AreEqual(Base.AddDays(1), restored[0].NextExecution,
            "With no persisted stamp the schedule is computed fresh.");
    }
}
