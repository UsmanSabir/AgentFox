namespace TradingAgent.Persistence;

using System.Text.Json;
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

    Task<IReadOnlyList<TradeProposalRecord>> GetProposalsAsync(
        int limit = 100,
        CancellationToken ct = default);

    Task<IReadOnlyList<TradingExecutionRecord>> GetExecutionsAsync(
        int limit = 100,
        CancellationToken ct = default);

    Task<IReadOnlyList<TradingEventRecord>> GetEventsAsync(
        int limit = 200,
        CancellationToken ct = default);

    Task<IReadOnlyList<ReconciliationRunRecord>> GetReconciliationRunsAsync(
        int limit = 100,
        CancellationToken ct = default);

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

public sealed record TradeProposalRecord(
    string ProposalId,
    string Status,
    JsonElement Proposal,
    string PolicyVersion,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record TradingExecutionRecord(
    string ExecutionId,
    string State,
    JsonElement Request,
    JsonElement? Result,
    string PolicyVersion,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record TradingEventRecord(
    long EventId,
    string ExecutionId,
    string EventType,
    JsonElement Payload,
    DateTime CreatedUtc);

public sealed record ReconciliationRunRecord(
    string ReconciliationId,
    string State,
    JsonElement Details,
    DateTime StartedUtc,
    DateTime? CompletedUtc);
