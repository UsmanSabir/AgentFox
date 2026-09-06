using AgentFox.Planning;

namespace AgentFox.ChannelTests.Evals;

/// <summary>
/// The tool gate's decision table, case by case.
///
/// This is the highest-value suite of the four, because the gate is the thing standing between a
/// model and a side effect, its logic is four lines, and those four lines contain an ordering that
/// looks arbitrary and is not (plan enforcement before bypass — see <see cref="ToolGate"/>).
/// </summary>
[TestClass]
public sealed class PlanGateEvals
{
    private static readonly string[] Mutating = ["place_order", "write_file", "shell"];
    private static readonly string[] Watched = ["place_order", "delete"];
    private static readonly string[] None = [];

    [TestMethod]
    public void PlanGateDecisionTable()
    {
        EvalSuite.Run("plan-gate", [
            EvalSuite.Case("readonly_tool_is_never_gated",
                () => ToolGate.Decide("read_file", Mutating, Watched, PlanPhase.Research, bypassed: false)
                      == ToolGateOutcome.Allowed,
                "A tool that is neither mutating nor watched must pass straight through — "
                + "research has to be able to proceed freely."),

            EvalSuite.Case("mutating_tool_refused_during_research",
                () => ToolGate.Decide("write_file", Mutating, Watched, PlanPhase.Research, bypassed: false)
                      == ToolGateOutcome.RefusedPendingPlan,
                "A mutating tool with no approved plan must be refused."),

            EvalSuite.Case("mutating_tool_refused_while_awaiting_approval",
                () => ToolGate.Decide("write_file", Mutating, Watched, PlanPhase.AwaitingApproval, bypassed: false)
                      == ToolGateOutcome.RefusedPendingPlan,
                "A submitted-but-unapproved plan is not an approved plan. Only Execute unlocks."),

            EvalSuite.Case("mutating_tool_allowed_once_plan_is_approved",
                () => ToolGate.Decide("write_file", Mutating, Watched, PlanPhase.Execute, bypassed: false)
                      == ToolGateOutcome.Allowed,
                "Execute phase must unlock mutating tools, or an approved plan buys nothing."),

            // The ordering case. If this ever flips, a trusted session can mutate with no plan.
            EvalSuite.Case("bypass_does_not_skip_plan_enforcement",
                () => ToolGate.Decide("write_file", Mutating, Watched, PlanPhase.Research, bypassed: true)
                      == ToolGateOutcome.RefusedPendingPlan,
                "Bypass exempts a session from the HUMAN step, never from having a plan. "
                + "A trusted session still flows through submit_plan, where it auto-approves."),

            EvalSuite.Case("watched_tool_requires_a_human_by_default",
                () => ToolGate.Decide("delete", Mutating, Watched, PlanPhase.Execute, bypassed: false)
                      == ToolGateOutcome.RequiresHumanApproval,
                "A watched tool in an untrusted session must reach a human."),

            EvalSuite.Case("watched_tool_skips_the_human_when_bypassed",
                () => ToolGate.Decide("delete", Mutating, Watched, PlanPhase.Execute, bypassed: true)
                      == ToolGateOutcome.AllowedByBypass,
                "A trusted session must not be asked to approve its own watched tools."),

            EvalSuite.Case("tool_that_is_both_mutating_and_watched_needs_plan_then_human",
                () => ToolGate.Decide("place_order", Mutating, Watched, PlanPhase.Research, bypassed: false)
                          == ToolGateOutcome.RefusedPendingPlan
                      && ToolGate.Decide("place_order", Mutating, Watched, PlanPhase.Execute, bypassed: false)
                          == ToolGateOutcome.RequiresHumanApproval,
                "place_order is on both lists. It must clear the plan gate first and the human second — "
                + "either check alone would let it through in one of these two states."),

            EvalSuite.Case("matching_is_case_insensitive",
                () => ToolGate.Decide("WRITE_FILE", Mutating, Watched, PlanPhase.Research, bypassed: false)
                      == ToolGateOutcome.RefusedPendingPlan,
                "A gate that can be defeated by casing is not a gate. Tool names arrive from the "
                + "model, which does not guarantee the casing it was given."),

            EvalSuite.Case("empty_lists_gate_nothing",
                () => ToolGate.Decide("place_order", None, None, PlanPhase.Research, bypassed: false)
                      == ToolGateOutcome.Allowed,
                "Plan and HITL disabled means no gating at all — configuring nothing must not "
                + "accidentally refuse everything."),

            EvalSuite.Case("only_refusals_and_approvals_block",
                () => ToolGate.IsAllowedWithoutHuman(ToolGateOutcome.Allowed)
                      && ToolGate.IsAllowedWithoutHuman(ToolGateOutcome.AllowedByBypass)
                      && !ToolGate.IsAllowedWithoutHuman(ToolGateOutcome.RefusedPendingPlan)
                      && !ToolGate.IsAllowedWithoutHuman(ToolGateOutcome.RequiresHumanApproval),
                "IsAllowedWithoutHuman must agree with the outcome names."),
        ]);
    }
}
