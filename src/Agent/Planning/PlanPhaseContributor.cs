using AgentFox.Agents;

namespace AgentFox.Planning;

/// <summary>
/// Injects per-turn instructions that steer the model according to the session's current
/// <see cref="PlanPhase"/> — the prompt-side half of the research → plan → execute workflow.
/// Registered as an <see cref="IPromptContributor"/> only when the Plan feature is enabled.
/// </summary>
public sealed class PlanPhaseContributor : IPromptContributor
{
    private readonly PlanStateStore _store;

    public PlanPhaseContributor(PlanStateStore store) => _store = store;

    public string ContributorId => "plan-phase";

    public string? GetFragment()
    {
        var sessionKey = FoxAgent.CurrentSessionKey.Value;
        var state = sessionKey != null ? _store.Peek(sessionKey) : null;
        var phase = state?.Phase ?? PlanPhase.Research;

        return phase switch
        {
            PlanPhase.Research =>
                """

                ## Operating mode: RESEARCH & PLAN
                You are in the planning phase. Investigate the task using read-only tools
                (search, fetch, read, and sub-agents). Side-effecting/mutating tools are LOCKED
                and will be refused until a plan is approved.
                When you have gathered enough to act, call `submit_plan` with a concrete,
                step-by-step plan. Do not attempt mutating actions before then.
                """
                + (state?.RejectionReason is { Length: > 0 } reason
                    ? $"\nThe previous plan was rejected: \"{reason}\". Revise accordingly before resubmitting."
                    : string.Empty),

            PlanPhase.AwaitingApproval =>
                """

                ## Operating mode: AWAITING APPROVAL
                A plan has been submitted and is awaiting the human's decision. Do not take any
                further action — `submit_plan` will return once they approve or reject.
                """,

            PlanPhase.Execute =>
                """

                ## Operating mode: EXECUTE
                The plan is approved. Carry out the approved steps in order. Mutating tools are now
                unlocked (individual high-risk tools may still ask for their own confirmation).
                If the goal changes materially, draft and submit a new plan before continuing.
                """,

            _ => null
        };
    }
}
