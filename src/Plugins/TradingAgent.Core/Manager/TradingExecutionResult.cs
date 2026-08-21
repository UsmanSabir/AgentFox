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

public sealed record ExecutionAuthorization(
    string Method,
    string Actor,
    DateTime AuthorizedUtc,
    ApprovalIntent? Intent = null)
{
    public static ExecutionAuthorization HostToolGate(
        string actor = "agentfox-hitl", ApprovalIntent? intent = null) =>
        new("host-tool-gate", actor, DateTime.UtcNow, intent);
}
