namespace AgentFox.Planning;

/// <summary>What the tool gate decided, and why.</summary>
public enum ToolGateOutcome
{
    /// <summary>Not gated by anything — the common case, and the one that must stay cheap.</summary>
    Allowed,

    /// <summary>
    /// A mutating tool without an approved plan for this session. Refused locally; the model is
    /// told, and the plan-phase prompt steers it to <c>submit_plan</c>.
    /// </summary>
    RefusedPendingPlan,

    /// <summary>A watched tool in a trusted context — the human step is skipped deliberately.</summary>
    AllowedByBypass,

    /// <summary>A watched tool that needs a human to say yes before it runs.</summary>
    RequiresHumanApproval
}

/// <summary>
/// The tool-approval gate's DECISION, as a pure function.
///
/// <para>
/// This used to live inside a lambda in <c>AgentOrchestrator</c> alongside the work of building an
/// approval message, broadcasting it to every channel, writing to the console and awaiting a
/// human. Two very different things had become one: a policy decision that is a function of four
/// values, and roughly sixty lines of delivery. Separating them means the decision can be asserted
/// on directly — a gate that reaches for a service is a gate that stops being tested — while the
/// orchestrator keeps every side effect it had.
/// </para>
///
/// <para>
/// The ORDER of the two checks is load-bearing and is the reason this is one function rather than
/// two. Plan enforcement runs FIRST and bypass does not skip it: a trusted session still flows
/// through <c>submit_plan</c>, where its plan auto-approves and flips the phase to Execute.
/// Checking bypass first would let a trusted session run a mutating tool with no plan at all,
/// which reads like a reasonable optimisation and is not.
/// </para>
/// </summary>
public static class ToolGate
{
    /// <param name="phase">The session's plan phase. Only <see cref="PlanPhase.Execute"/> unlocks mutating tools.</param>
    /// <param name="bypassed">The result of <c>HitlBypassPolicy.IsBypassed</c> for this session and role.</param>
    public static ToolGateOutcome Decide(
        string toolName,
        IReadOnlyCollection<string> mutatingTools,
        IReadOnlyCollection<string> watchedTools,
        PlanPhase phase,
        bool bypassed)
    {
        // 1) Plan enforcement. Before bypass, deliberately — see the class remarks.
        if (Contains(mutatingTools, toolName) && phase != PlanPhase.Execute)
            return ToolGateOutcome.RefusedPendingPlan;

        // 2) Per-tool human approval.
        if (!Contains(watchedTools, toolName))
            return ToolGateOutcome.Allowed;

        return bypassed ? ToolGateOutcome.AllowedByBypass : ToolGateOutcome.RequiresHumanApproval;
    }

    /// <summary>
    /// Whether an outcome permits the call without asking anyone. Kept beside
    /// <see cref="Decide"/> so a new outcome added later cannot be silently treated as allowing —
    /// the switch is exhaustive and a missing arm is a compile error, not a permissive default.
    /// </summary>
    public static bool IsAllowedWithoutHuman(ToolGateOutcome outcome) => outcome switch
    {
        ToolGateOutcome.Allowed => true,
        ToolGateOutcome.AllowedByBypass => true,
        ToolGateOutcome.RefusedPendingPlan => false,
        ToolGateOutcome.RequiresHumanApproval => false,
        _ => false
    };

    private static bool Contains(IReadOnlyCollection<string> names, string toolName) =>
        names.Count > 0 && names.Contains(toolName, StringComparer.OrdinalIgnoreCase);
}
