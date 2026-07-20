using System.Collections.Concurrent;

namespace AgentFox.Planning;

/// <summary>
/// The phase of the research → plan → execute workflow for a single session.
/// This is the AgentFox equivalent of the Agent Framework harness's plan/execute mode,
/// built from existing primitives (HITL gate + prompt contributor) instead of the harness.
/// </summary>
public enum PlanPhase
{
    /// <summary>Default. The agent investigates with read-only tools and drafts a plan.
    /// Mutating tools are locked until a plan is approved.</summary>
    Research,

    /// <summary>A plan was submitted via <c>submit_plan</c> and is waiting on the human.</summary>
    AwaitingApproval,

    /// <summary>The plan was approved (or auto-approved for a trusted session); mutating tools are unlocked.</summary>
    Execute
}

/// <summary>
/// Mutable per-session plan state. Read by the tool-approval gate and the
/// <see cref="PlanPhaseContributor"/>; mutated by <see cref="AgentFox.Tools.SubmitPlanTool"/>.
/// </summary>
public sealed class PlanState
{
    public PlanPhase Phase { get; set; } = PlanPhase.Research;

    /// <summary>The most recently submitted plan text.</summary>
    public string? PlanText { get; set; }

    /// <summary>Reason supplied by the human on the last rejection, fed back to the model.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>When the current plan was approved, if it has been.</summary>
    public DateTime? ApprovedAt { get; set; }
}

/// <summary>
/// Singleton store of <see cref="PlanState"/> keyed by session key
/// (<see cref="AgentFox.Agents.FoxAgent.CurrentSessionKey"/>). Registered in DI.
/// </summary>
public sealed class PlanStateStore
{
    private readonly ConcurrentDictionary<string, PlanState> _bySession = new();

    /// <summary>Get the state for a session, creating a fresh <see cref="PlanPhase.Research"/> state if absent.</summary>
    public PlanState For(string sessionKey) =>
        _bySession.GetOrAdd(sessionKey ?? string.Empty, _ => new PlanState());

    /// <summary>Read the state for a session without creating one. Returns null if none exists yet.</summary>
    public PlanState? Peek(string? sessionKey) =>
        sessionKey != null && _bySession.TryGetValue(sessionKey, out var s) ? s : null;

    /// <summary>Drop a session's plan state (e.g. on /new or /reset).</summary>
    public void Reset(string sessionKey) => _bySession.TryRemove(sessionKey ?? string.Empty, out _);
}
