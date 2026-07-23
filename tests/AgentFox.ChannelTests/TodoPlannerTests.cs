using System.Text.Json;
using AgentFox.Agents;
using AgentFox.Memory;
using AgentFox.Planning;
using AgentFox.Tools;
using Microsoft.Extensions.Configuration;

namespace AgentFox.ChannelTests;

/// <summary>
/// The todo planner wires the Agent Framework's <c>TodoProvider</c> into FoxAgent's own context
/// provider pipeline. Two properties matter and are covered here:
///  - it is configurable off, and its prompt guidance never advertises tools that will not exist;
///  - it never issues unrequested extra LLM turns by default (MaxContinuations = 0), which is the
///    safe replacement for the still-experimental TodoCompletionLoopEvaluator.
/// </summary>
[TestClass]
public sealed class TodoPlannerTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [TestMethod]
    public void Planner_IsOnByDefault_WithNoAutoContinuations()
    {
        var config = new TodoPlannerConfig();

        Assert.IsTrue(config.Enabled);
        Assert.AreEqual(0, config.MaxContinuations,
            "Auto-continuation must default to off so a turn never spends extra tokens unasked.");
        Assert.IsFalse(config.SuppressTodoListMessage,
            "The injected todo message is what makes the list survive compaction; it must stay on.");
    }

    [TestMethod]
    public void Builder_EnablesPlannerWhenConfigSectionIsAbsent()
    {
        var builder = new AgentBuilder(new ToolRegistry())
            .WithTodoPlannerFromConfig(Config(new Dictionary<string, string?>()));

        Assert.IsTrue(builder.IsTodoPlannerEnabled);
    }

    [TestMethod]
    public void Builder_RespectsExplicitDisable()
    {
        var builder = new AgentBuilder(new ToolRegistry())
            .WithTodoPlannerFromConfig(Config(new Dictionary<string, string?>
            {
                ["TodoPlanner:Enabled"] = "false"
            }));

        Assert.IsFalse(builder.IsTodoPlannerEnabled);
    }

    [TestMethod]
    public void Builder_BindsContinuationBudgetFromConfig()
    {
        var builder = new AgentBuilder(new ToolRegistry())
            .WithTodoPlannerFromConfig(Config(new Dictionary<string, string?>
            {
                ["TodoPlanner:MaxContinuations"] = "2"
            }));

        Assert.IsTrue(builder.IsTodoPlannerEnabled);
    }

    [TestMethod]
    public void Builder_TreatsNullConfigAsDisabled()
    {
        var builder = new AgentBuilder(new ToolRegistry()).WithTodoPlanner(null);

        Assert.IsFalse(builder.IsTodoPlannerEnabled);
    }

    // ── Prompt guidance ───────────────────────────────────────────────────────

    [TestMethod]
    public void Contributor_WithoutPlanGate_AlwaysDescribesTheTodoTools()
    {
        var fragment = new TodoPlannerContributor().GetFragment();

        StringAssert.Contains(fragment, "todos_add");
        StringAssert.Contains(fragment, "todos_complete");
    }

    [TestMethod]
    public void TodoMessageFormatter_OmitsEmptyAndCompletedItems()
    {
        Assert.AreEqual(
            string.Empty,
            TodoListMessageFormatter.Build(
            [
                new Microsoft.Agents.AI.TodoItem { Id = 1, Title = "done", IsComplete = true }
            ]));
        Assert.AreEqual(
            string.Empty,
            TodoListMessageFormatter.Build([]));
    }

    [TestMethod]
    public void TodoMessageFormatter_EmitsOnlyOutstandingItems()
    {
        var message = TodoListMessageFormatter.Build(
        [
            new Microsoft.Agents.AI.TodoItem { Id = 1, Title = "done", IsComplete = true },
            new Microsoft.Agents.AI.TodoItem
            {
                Id = 2,
                Title = "execute next",
                Description = "keep going",
                IsComplete = false
            }
        ]);

        StringAssert.Contains(message, "### Current todo list");
        StringAssert.Contains(message, "- 2 execute next: keep going");
        Assert.IsFalse(message.Contains("done"));
    }

    [TestMethod]
    public void Contributor_InResearchPhase_DefersToSubmitPlanInsteadOfTodos()
    {
        var store = new PlanStateStore();
        var session = nameof(Contributor_InResearchPhase_DefersToSubmitPlanInsteadOfTodos);
        store.For(session).Phase = PlanPhase.Research;

        var fragment = RunInSession(session, () => new TodoPlannerContributor(store).GetFragment());

        Assert.IsNotNull(fragment);
        StringAssert.Contains(fragment, "submit_plan");
        Assert.IsFalse(fragment!.Contains("todos_add"),
            "Research phase must not tell the model to build a todo list — the plan is the deliverable.");
    }

    [TestMethod]
    public void Contributor_InExecutePhase_DrivesTheTodoListToCompletion()
    {
        var store = new PlanStateStore();
        var session = nameof(Contributor_InExecutePhase_DrivesTheTodoListToCompletion);
        store.For(session).Phase = PlanPhase.Execute;

        var fragment = RunInSession(session, () => new TodoPlannerContributor(store).GetFragment());

        Assert.IsNotNull(fragment);
        StringAssert.Contains(fragment, "todos_add");
        StringAssert.Contains(fragment, "Do not end your turn while items remain incomplete");
    }

    [TestMethod]
    public void Contributor_WhileAwaitingApproval_StaysSilent()
    {
        var store = new PlanStateStore();
        var session = nameof(Contributor_WhileAwaitingApproval_StaysSilent);
        store.For(session).Phase = PlanPhase.AwaitingApproval;

        var fragment = RunInSession(session, () => new TodoPlannerContributor(store).GetFragment());

        Assert.IsNull(fragment, "Nothing should steer the model while a human is deciding.");
    }

    // ── Restore-after-restart notice ──────────────────────────────────────────

    [TestMethod]
    public void Contributor_AfterFreshRestore_TellsTheAgentToResume()
    {
        var tracker = new TodoRestoreTracker();
        var session = nameof(Contributor_AfterFreshRestore_TellsTheAgentToResume);
        tracker.Record(session, DateTimeOffset.UtcNow.AddMinutes(-5), outstandingCount: 3);

        var fragment = RunInSession(session, () =>
            new TodoPlannerContributor(null, tracker, TimeSpan.FromHours(1)).GetFragment());

        Assert.IsNotNull(fragment);
        StringAssert.Contains(fragment, "3 unfinished todo items");
        StringAssert.Contains(fragment, "Pick up where you left off");
        Assert.IsFalse(fragment!.Contains("confirm before resuming"),
            "A five-minute-old list is not stale and should resume without interrogating the user.");
    }

    [TestMethod]
    public void Contributor_AfterStaleRestore_RequiresTheUserToDecide()
    {
        var tracker = new TodoRestoreTracker();
        var session = nameof(Contributor_AfterStaleRestore_RequiresTheUserToDecide);
        tracker.Record(session, DateTimeOffset.UtcNow.AddDays(-3), outstandingCount: 1);

        var fragment = RunInSession(session, () =>
            new TodoPlannerContributor(null, tracker, TimeSpan.FromHours(1)).GetFragment());

        Assert.IsNotNull(fragment);
        StringAssert.Contains(fragment, "1 unfinished todo item");
        StringAssert.Contains(fragment, "3 days ago");
        StringAssert.Contains(fragment, "Do NOT resume this work automatically");
        StringAssert.Contains(fragment, "ask whether to continue");
    }

    [TestMethod]
    public void RestoreNotice_IsSurfacedOnlyOnce()
    {
        var tracker = new TodoRestoreTracker();
        var session = nameof(RestoreNotice_IsSurfacedOnlyOnce);
        tracker.Record(session, DateTimeOffset.UtcNow.AddDays(-2), outstandingCount: 2);
        var contributor = new TodoPlannerContributor(null, tracker, TimeSpan.FromHours(1));

        var first = RunInSession(session, contributor.GetFragment);
        var second = RunInSession(session, contributor.GetFragment);

        StringAssert.Contains(first, "Recovered work");
        Assert.IsFalse(second!.Contains("Recovered work"),
            "The recovery prompt must not repeat on every turn for the rest of the conversation.");
    }

    [TestMethod]
    public void RestoreTracker_IgnoresSessionsThatWereNotRestored()
    {
        var tracker = new TodoRestoreTracker();

        Assert.IsNull(tracker.Consume("never-restored"));
        Assert.IsNull(tracker.Consume(null));
    }

    [TestMethod]
    public void RestoredState_DescribesAgeInHumanTerms()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.AreEqual("1 hour", new RestoredTodoState(now.AddHours(-1), 1).DescribeAge());
        Assert.AreEqual("5 hours", new RestoredTodoState(now.AddHours(-5), 1).DescribeAge());
        Assert.AreEqual("1 day", new RestoredTodoState(now.AddDays(-1), 1).DescribeAge());
        Assert.AreEqual("2 minutes", new RestoredTodoState(now.AddMinutes(-2), 1).DescribeAge());
    }

    // ── Sidecar persistence ───────────────────────────────────────────────────

    [TestMethod]
    public void SessionState_RoundTripsThroughTheSidecar()
    {
        var dir = NewTempDir();
        try
        {
            var store = new MarkdownSessionStore(dir);
            // Bare state-bag contents — NOT a {"stateBag":...} envelope. The store adds the one
            // and only wrapper, so the sidecar stays single-nested and portable.
            using var doc = JsonDocument.Parse(
                """{"TodoProvider":{"items":[{"id":1,"title":"x","isComplete":false}],"nextId":2}}""");

            store.PersistSessionState("main", doc.RootElement);
            var read = store.ReadSessionState("main");

            Assert.IsNotNull(read);
            StringAssert.Contains(read!.StateBag.GetRawText(), "\"title\":\"x\"");
            Assert.IsTrue(read.StateBag.TryGetProperty("TodoProvider", out _),
                "ReadSessionState must return bag contents directly, with no redundant nesting.");
            Assert.IsTrue((DateTimeOffset.UtcNow - read.SavedAt) < TimeSpan.FromMinutes(1),
                "SavedAt must reflect write time — it is what makes staleness detectable.");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void SessionState_SidecarNeverContainsTranscriptMessages()
    {
        var dir = NewTempDir();
        try
        {
            var store = new MarkdownSessionStore(dir);
            using var doc = JsonDocument.Parse(
                """{"stateBag":{"TodoProvider":{"items":[],"nextId":1}}}""");
            store.PersistSessionState("main", doc.RootElement);

            var onDisk = File.ReadAllText(Path.Combine(dir, "main.md.state.json"));

            Assert.IsFalse(onDisk.Contains("ChatHistoryProvider"),
                "Persisting history here would duplicate every message against the .md transcript.");
            Assert.IsFalse(onDisk.Contains("\"messages\""));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void SessionState_MissingOrCorruptSidecarReadsAsAbsent()
    {
        var dir = NewTempDir();
        try
        {
            var store = new MarkdownSessionStore(dir);
            Assert.IsNull(store.ReadSessionState("never-saved"));

            File.WriteAllText(Path.Combine(dir, "broken.md.state.json"), "{ not json ");
            Assert.IsNull(store.ReadSessionState("broken"),
                "A corrupt planner sidecar must never block a conversation from starting.");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void SessionState_IsClearedWithTheConversation()
    {
        var dir = NewTempDir();
        try
        {
            var store = new MarkdownSessionStore(dir);
            using var doc = JsonDocument.Parse("""{"stateBag":{"TodoProvider":{"items":[],"nextId":1}}}""");
            store.PersistSessionState("main", doc.RootElement);

            store.DeleteSession("main");

            Assert.IsNull(store.ReadSessionState("main"));
            Assert.IsFalse(File.Exists(Path.Combine(dir, "main.md.state.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentfox_todo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// The contributor reads the ambient session key that FoxAgent sets for the current turn.
    /// </summary>
    private static string? RunInSession(string sessionKey, Func<string?> action)
    {
        var previous = FoxAgent.CurrentSessionKey.Value;
        FoxAgent.CurrentSessionKey.Value = sessionKey;
        try
        {
            return action();
        }
        finally
        {
            FoxAgent.CurrentSessionKey.Value = previous;
        }
    }
}
