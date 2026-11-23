using AgentFox.Agents;
using AgentFox.Planning;
using AgentFox.Plugins.Interfaces;
using AgentFox.Tools;

namespace AgentFox.ChannelTests;

/// <summary>
/// The contract behind <see cref="IHumanApprovedTool"/>: such a tool is put to a person whatever the watched list and the bypass
/// rules say, plan enforcement still comes first, and the approval is in scope for exactly the one execution it approved.
/// </summary>
[TestClass]
public sealed class HumanApprovedToolTests
{
    private static readonly HashSet<string> None = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Mutating = new(StringComparer.OrdinalIgnoreCase) { "arm_it" };

    [TestMethod]
    public void A_human_approved_tool_is_asked_about_even_when_unwatched_or_bypassed()
    {
        Assert.AreEqual(ToolGateOutcome.RequiresHumanApproval,
            ToolGate.Decide("arm_it", None, None, PlanPhase.Execute, bypassed: false, requiresHuman: true));
        Assert.AreEqual(ToolGateOutcome.RequiresHumanApproval,
            ToolGate.Decide("arm_it", None, None, PlanPhase.Execute, bypassed: true, requiresHuman: true),
            "no bypass waives it");
        Assert.AreEqual(ToolGateOutcome.Allowed,
            ToolGate.Decide("arm_it", None, None, PlanPhase.Execute, bypassed: false),
            "an ordinary unwatched tool is unchanged");
    }

    [TestMethod]
    public void Plan_enforcement_still_comes_first()
    {
        Assert.AreEqual(ToolGateOutcome.RefusedPendingPlan,
            ToolGate.Decide("arm_it", Mutating, None, PlanPhase.Research, bypassed: false, requiresHuman: true));
    }

    [TestMethod]
    public async Task The_approval_is_in_scope_for_the_approved_execution_only()
    {
        var registry = new ToolRegistry();
        var tool = new ScopeRecordingTool();
        registry.Register(tool);
        var approval = new HumanToolApproval(tool.Name, "ABC12345", "s1", "Web", null, DateTime.UtcNow);
        var builder = new AgentBuilder(registry)
            .WithToolApprovalGate((t, _, _) => Task.FromResult(ToolApprovalVerdict.Allow(approval)));

        var result = await builder.ExecuteThroughGatewayAsync(tool.Name, new Dictionary<string, object?>());

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(approval, tool.SeenApproval);
        Assert.IsNull(ToolApprovals.Current, "cleared after the execution");
    }

    [TestMethod]
    public async Task A_blocked_call_carries_the_gate_s_reason_and_does_not_run()
    {
        var registry = new ToolRegistry();
        var tool = new ScopeRecordingTool();
        registry.Register(tool);
        var builder = new AgentBuilder(registry)
            .WithToolApprovalGate((_, _, _) => Task.FromResult(ToolApprovalVerdict.Block("Not armed: web chat only.")));

        var result = await builder.ExecuteThroughGatewayAsync(tool.Name, new Dictionary<string, object?>());

        Assert.IsFalse(result.Success);
        Assert.IsFalse(tool.Executed);
        Assert.AreEqual("Not armed: web chat only.", result.Error);
    }

    [TestMethod]
    public async Task The_old_boolean_gate_still_works_and_puts_no_approval_in_scope()
    {
        var registry = new ToolRegistry();
        var tool = new ScopeRecordingTool();
        registry.Register(tool);
        var builder = new AgentBuilder(registry).WithToolApprovalGate((_, _, _) => Task.FromResult(true));

        var result = await builder.ExecuteThroughGatewayAsync(tool.Name, new Dictionary<string, object?>());

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsTrue(tool.Executed);
        Assert.IsNull(tool.SeenApproval);
    }

    [TestMethod]
    public async Task A_delegated_runs_approval_is_found_under_the_chat_that_started_it()
    {
        var hitl = new AgentFox.Hitl.HitlManager();
        var request = new AgentFox.Hitl.HitlRequest("AB12CD34", "specialist/trading-agent/run_1", null,
            AgentFox.Hitl.HitlTrigger.Tool, "arm_rule_order", "Arm a rule order", RootSessionKey: "web_chat_1");
        var waiting = hitl.RequestApprovalAsync(request);

        Assert.AreEqual("AB12CD34", hitl.GetPendingForSession("web_chat_1")?.ApprovalId, "the chat that delegated sees it");
        Assert.AreEqual("AB12CD34", hitl.GetPendingForSession("specialist/trading-agent/run_1")?.ApprovalId);
        Assert.IsNull(hitl.GetPendingForSession("web_other"));

        Assert.IsTrue(hitl.Respond("AB12CD34", approved: true));
        Assert.IsTrue((await waiting).Approved);
    }

    [TestMethod]
    public void Approval_scopes_nest_and_restore()
    {
        var outer = new HumanToolApproval("a", "1", null, null, null, DateTime.UtcNow);
        var inner = new HumanToolApproval("b", "2", null, null, null, DateTime.UtcNow);
        using (ToolApprovals.Enter(outer))
        {
            Assert.IsTrue(ToolApprovals.IsApproved("A"));
            using (ToolApprovals.Enter(inner))
            {
                Assert.IsTrue(ToolApprovals.IsApproved("b"));
                Assert.IsFalse(ToolApprovals.IsApproved("a"));
            }
            Assert.AreEqual(outer, ToolApprovals.Current);
        }
        Assert.IsNull(ToolApprovals.Current);
    }

    private sealed class ScopeRecordingTool : IHumanApprovedTool
    {
        public bool Executed { get; private set; }
        public HumanToolApproval? SeenApproval { get; private set; }
        public string Name => "arm_it";
        public string Description => "Records the approval in scope when it runs.";
        public Dictionary<string, ToolParameter> Parameters => new();

        public Task<ToolApprovalPrompt> DescribeForApprovalAsync(
            Dictionary<string, object?> arguments, ToolApprovalContext context, CancellationToken ct) =>
            Task.FromResult(ToolApprovalPrompt.Ask("Arm it."));

        public Task<ToolResult> ExecuteAsync(Dictionary<string, object?> arguments)
        {
            Executed = true;
            SeenApproval = ToolApprovals.Current;
            return Task.FromResult(ToolResult.Ok("ran"));
        }
    }
}
