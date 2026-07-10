namespace TradingAgent.Persistence;

using TradingAgent.Manager;
using TradingAgent.Reconciliation;

public interface ITradingRepository
{
    Task<string> CreateProposalAsync(
        string idempotencyKey,
        string proposalJson,
        string policyVersion,
        CancellationToken ct = default);

    Task<TradingLedgerStatus> GetStatusAsync(CancellationToken ct = default);

    Task RecordReconciliationAsync(
        BrokerReconciliationSnapshot snapshot,
        CancellationToken ct = default);

    Task<ExecutionClaim> TryBeginExecutionAsync(
        string idempotencyKey,
        string requestJson,
        string policyVersion,
        CancellationToken ct = default);

    Task CompleteExecutionAsync(
        string executionId,
        string state,
        string resultJson,
        CancellationToken ct = default);

    Task AppendEventAsync(
        string executionId,
        string eventType,
        string payloadJson,
        CancellationToken ct = default);
}

public sealed record TradingLedgerStatus(
    int PendingProposals,
    int SubmittingExecutions,
    int UnknownExecutions,
    int AcceptedExecutions,
    DateTime CheckedUtc);
