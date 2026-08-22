using TradingAgent.Models;

namespace TradingAgent.Manager;

public sealed record TradingExecutionResult(
    bool Executed,
    bool IsReplay,
    string ExecutionId,
    string PolicyVersion,
    string Reason,
    IReadOnlyList<IReadOnlyList<OrderResult>> Groups)
{
    public static TradingExecutionResult Rejected(string policyVersion, string reason) =>
        new(false, false, "", policyVersion, reason, Array.Empty<IReadOnlyList<OrderResult>>());
}

public sealed record ExecutionClaim(
    bool Acquired,
    string ExecutionId,
    string State,
    string? ResultJson);

/// <summary>
/// Who said yes to an execution, and how.
/// </summary>
/// <param name="Attended">
/// True when a HUMAN said yes to THIS order at the moment it was placed — a dashboard submission, or a
/// tool call the host's approval gate put in front of someone. False for standing permission granted in
/// advance: a pre-authorized trigger, an open approval window, an execution mode that allows unattended
/// orders. A null authorization is the same answer as false, so "nobody said yes" and "policy said yes
/// earlier" are treated alike.
///
/// <para>
/// The distinction exists for one rule — <see cref="Config.TradingAgentOptions.ManualOnlySymbols"/> —
/// which needs to separate "the operator is trading this" from "the machine is trading this", something
/// no other field here can express: <see cref="Method"/> describes the gate, <see cref="Actor"/> is a
/// label, and both look identical whether a person was actually watching or not. It defaults to FALSE so
/// a new automated caller is denied a manual-only symbol by omission rather than admitted by it.
/// </para>
/// </param>
public sealed record ExecutionAuthorization(
    string Method,
    string Actor,
    DateTime AuthorizedUtc,
    ApprovalIntent? Intent = null,
    bool Attended = false)
{
    /// <summary>
    /// A human confirmation carried by the host tool-approval gate (or the dashboard, which IS the
    /// approval event for a <c>TradingTrader</c> request). This is the only authorization
    /// <c>ApprovalRequired</c> mode accepts.
    /// </summary>
    public static ExecutionAuthorization HostToolGate(
        string actor = "agentfox-hitl", ApprovalIntent? intent = null) =>
        new("host-tool-gate", actor, DateTime.UtcNow, intent, Attended: true);

    /// <summary>
    /// A human acted, but not through the approval gate — approving a stored proposal, for instance.
    /// Records attendance WITHOUT claiming an approval intent, so <c>ApprovalRequired</c> mode still
    /// refuses it exactly as it refuses no authorization at all. Marking such a caller
    /// <see cref="HostToolGate"/> instead would quietly widen what may execute in that mode.
    /// </summary>
    public static ExecutionAuthorization Attendant(string actor) =>
        new("human-attended", actor, DateTime.UtcNow, null, Attended: true);

    /// <summary>
    /// Permission granted in advance by policy rather than by someone watching — see
    /// <see cref="ApprovalGate"/>. Carries a real, single-use intent (so the manager re-checks the
    /// hash exactly as it does for a clicked approval) while staying unattended.
    /// </summary>
    public static ExecutionAuthorization PreAuthorized(string actor, ApprovalIntent? intent = null) =>
        new("host-tool-gate", actor, DateTime.UtcNow, intent, Attended: false);
}
