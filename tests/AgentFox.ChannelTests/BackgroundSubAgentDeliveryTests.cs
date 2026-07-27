using AgentFox.Agents;
using AgentFox.Models;
using AgentFox.Tools;

namespace AgentFox.ChannelTests;

/// <summary>
/// A background sub-agent's result reaches the user through exactly one route: completion invokes
/// the registered callback, which enqueues a <see cref="ResultAnnouncementCommand"/> on the Main
/// lane. Every silent gap in that chain looks identical to the user — a background task that
/// finished but never reported anything, with no error to point at. These tests pin the chain
/// down at each point where it previously could break without a trace.
/// </summary>
[TestClass]
public sealed class BackgroundSubAgentDeliveryTests
{
    private const string ParentAgent   = "parent-agent";
    private const string ParentSession = "parent-session";

    private static SubAgentManager NewManager(
        ICommandQueue queue,
        bool autoCleanup = false,
        int cleanupDelayMs = 0)
        => new(
            queue,
            new StubAgentRuntime(),
            new SubAgentConfiguration
            {
                AutoCleanupCompleted = autoCleanup,
                CleanupDelayMilliseconds = cleanupDelayMs
            });

    /// <summary>
    /// Registers the same announcement routing the orchestrator uses for a parent-session result,
    /// and returns a task that completes once an announcement has actually been enqueued.
    /// </summary>
    private static Task<ResultAnnouncementCommand> CaptureAnnouncement(
        SubAgentManager manager, CommandQueue queue)
    {
        var captured = new TaskCompletionSource<ResultAnnouncementCommand>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        manager.RegisterResultCallback((task, result) =>
        {
            var announcement = ResultAnnouncementCommand.CreateParentAgentAnnouncement(
                result, task.CorrelationId, task.ParentSessionKey, task.SessionKey);

            // The manager enqueues whatever the callback returns; watch for it landing.
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < 200 && !captured.Task.IsCompleted; i++)
                {
                    if (queue.GetQueueCount(CommandLane.Main) > 0 &&
                        queue.TryDequeue(CommandLane.Main, out var cmd) &&
                        cmd is ResultAnnouncementCommand ann)
                    {
                        captured.TrySetResult(ann);
                        return;
                    }
                    await Task.Delay(10);
                }
                captured.TrySetException(new TimeoutException(
                    "No ResultAnnouncementCommand was enqueued on the Main lane."));
            });

            return Task.FromResult<ResultAnnouncementCommand?>(announcement);
        });

        return captured.Task;
    }

    private static async Task<SubAgentSpawnResult> SpawnAsync(SubAgentManager manager)
    {
        var spawn = await manager.SpawnSubAgentAsync(
            parentSessionKey: ParentSession,
            parentAgentId: ParentAgent,
            taskMessage: "Summarize the inbox.");

        Assert.IsTrue(spawn.Success, spawn.Error);
        return spawn;
    }

    [TestMethod]
    public async Task CompletedSubAgent_EnqueuesAnnouncementCarryingItsOutput()
    {
        var queue   = new CommandQueue();
        var manager = NewManager(queue);
        var spawn   = await SpawnAsync(manager);

        var announcementTask = CaptureAnnouncement(manager, queue);

        manager.OnSubAgentCompleted(spawn.RunId!, SubAgentCompletionResult.Success("3 unread emails."));

        var announcement = await announcementTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(CommandLane.Main, announcement.Lane);
        Assert.AreEqual(ParentSession, announcement.ParentSessionKey);
        StringAssert.Contains(announcement.FormatMessage(), "3 unread emails.");
    }

    /// <summary>
    /// A timeout or cancel racing the natural finish used to hit <c>TaskCompletionSource.SetResult</c>
    /// twice. The second call threw before the result callbacks ran, so the announcement for the
    /// first — genuinely successful — completion was never enqueued and the result vanished.
    /// </summary>
    [TestMethod]
    public async Task DuplicateCompletion_DoesNotThrowAndDoesNotAnnounceTwice()
    {
        var queue   = new CommandQueue();
        var manager = NewManager(queue);
        var spawn   = await SpawnAsync(manager);

        var announcements = 0;
        manager.RegisterResultCallback((task, result) =>
        {
            Interlocked.Increment(ref announcements);
            return Task.FromResult<ResultAnnouncementCommand?>(null);
        });

        manager.OnSubAgentCompleted(spawn.RunId!, SubAgentCompletionResult.Success("first"));
        manager.OnSubAgentCompleted(spawn.RunId!, SubAgentCompletionResult.Cancelled());

        await WaitForAsync(() => Volatile.Read(ref announcements) >= 1);
        await Task.Delay(150); // give a second (incorrect) announcement time to appear

        Assert.AreEqual(1, Volatile.Read(ref announcements),
            "A duplicate completion must not produce a second announcement.");

        var record = manager.GetRecentCompletions().Single(r => r.RunId == spawn.RunId);
        Assert.AreEqual(SubAgentState.Completed, record.Status,
            "The first completion wins; the duplicate must not overwrite it.");
    }

    [TestMethod]
    public void CompletionForUnknownRunId_IsIgnoredWithoutThrowing()
    {
        var manager = NewManager(new CommandQueue());

        // No task record exists — this must be reported, not crash the lane handler.
        manager.OnSubAgentCompleted("never-spawned", SubAgentCompletionResult.Success("orphan"));

        Assert.AreEqual(0, manager.GetRecentCompletions().Count);
    }

    /// <summary>
    /// Live task records are purged shortly after completion, so the completion history is the
    /// only thing that can answer "what happened to that background task?" once the announcement
    /// has gone missing.
    /// </summary>
    [TestMethod]
    public async Task CompletionRecord_OutlivesTheCleanedUpTaskRecord()
    {
        var queue   = new CommandQueue();
        var manager = NewManager(queue, autoCleanup: true, cleanupDelayMs: 10);
        var spawn   = await SpawnAsync(manager);

        manager.OnSubAgentCompleted(spawn.RunId!, SubAgentCompletionResult.Success("inbox digest"));

        await WaitForAsync(() => !manager.GetActiveSubAgents().Any(t => t.RunId == spawn.RunId));

        var record = manager.GetRecentCompletions().Single(r => r.RunId == spawn.RunId);
        Assert.AreEqual(SubAgentState.Completed, record.Status);
        Assert.AreEqual("inbox digest", record.Output);
        Assert.AreEqual(ParentSession, record.ParentSessionKey);
    }

    [TestMethod]
    public async Task FailedSubAgent_StillRecordsItsErrorForLaterInspection()
    {
        var manager = NewManager(new CommandQueue());
        var spawn   = await SpawnAsync(manager);

        manager.OnSubAgentCompleted(spawn.RunId!, SubAgentCompletionResult.Failure("gmail 400"));

        var record = manager.GetRecentCompletions().Single(r => r.RunId == spawn.RunId);
        Assert.AreEqual(SubAgentState.Failed, record.Status);
        Assert.AreEqual("gmail 400", record.Error);
    }

    /// <summary>
    /// The child counter gates spawning via MaxChildrenPerAgent. It used to be decremented with a
    /// read-then-write pair, so concurrent completions could both write the same value and leak a
    /// slot — enough leaked slots and the parent can no longer spawn anything at all.
    /// </summary>
    [TestMethod]
    public async Task ConcurrentCompletions_DoNotLeakChildSlots()
    {
        var manager = NewManager(new CommandQueue());

        var spawns = new List<SubAgentSpawnResult>();
        for (var i = 0; i < 5; i++)                     // MaxChildrenPerAgent default is 5
            spawns.Add(await SpawnAsync(manager));

        Assert.AreEqual(5, manager.GetActiveChildCount(ParentAgent));

        await Task.WhenAll(spawns.Select(s => Task.Run(() =>
            manager.OnSubAgentCompleted(s.RunId!, SubAgentCompletionResult.Success("done")))));

        Assert.AreEqual(0, manager.GetActiveChildCount(ParentAgent),
            "Every completion must release exactly one child slot.");

        // The parent must be able to spawn again rather than being permanently wedged.
        var again = await manager.SpawnSubAgentAsync(ParentSession, ParentAgent, "another task");
        Assert.IsTrue(again.Success, again.Error);
    }

    [TestMethod]
    public async Task StatusTool_ReportsAFinishedSubAgentsOutput()
    {
        var manager = NewManager(new CommandQueue(), autoCleanup: true, cleanupDelayMs: 10);
        var spawn   = await SpawnAsync(manager);

        manager.OnSubAgentCompleted(spawn.RunId!, SubAgentCompletionResult.Success("42 emails triaged"));
        await WaitForAsync(() => !manager.GetActiveSubAgents().Any(t => t.RunId == spawn.RunId));

        var result = await new CheckSubAgentStatusTool(manager).ExecuteAsync([]);

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, spawn.RunId!);
        StringAssert.Contains(result.Output, "42 emails triaged");
    }

    [TestMethod]
    public async Task StatusTool_ReportsARunningSubAgent()
    {
        var manager = NewManager(new CommandQueue());
        var spawn   = await SpawnAsync(manager);

        var result = await new CheckSubAgentStatusTool(manager)
            .ExecuteAsync(new Dictionary<string, object?> { ["run_id"] = spawn.RunId });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, spawn.RunId!);
        StringAssert.Contains(result.Output, "Pending");
    }

    [TestMethod]
    public async Task StatusTool_SaysSoWhenAskedAboutAnUnknownRun()
    {
        var result = await new CheckSubAgentStatusTool(NewManager(new CommandQueue()))
            .ExecuteAsync(new Dictionary<string, object?> { ["run_id"] = "does-not-exist" });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "No sub-agent found");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail("Condition was not met within the timeout.");
    }

    /// <summary>
    /// The manager never invokes the runtime on the paths under test — it only enqueues commands
    /// for a lane handler that does not exist here.
    /// </summary>
    private sealed class StubAgentRuntime : IAgentRuntime
    {
        public ToolRegistry ToolRegistry { get; } = new();
        public Microsoft.Extensions.Logging.ILogger? Logger { get; set; }

        public Task<AgentResult> ExecuteAsync(AgentCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException("Execution is not exercised by these tests.");

        public Agent SpawnSubAgent(Agent parent, AgentConfig config) =>
            throw new NotSupportedException("Execution is not exercised by these tests.");

        public void SetExecutor(IAgentExecutor executor) { }
    }
}
