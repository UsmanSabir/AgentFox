using System.Security.Cryptography;
using System.Text.Json;
using TradingAgent.Manager;
using TradingAgent.Persistence;
using TradingAgent.Reconciliation;

namespace TradingAgent.Trading;

public sealed record ClientCancellationResult(string RequestId, string ExecutionId, string State, string Outcome, bool IsReplay);

/// <summary>One opt-in, durable cancellation action. The authenticated caller supplies the reviewed
/// target and verified broker/account scope; neither a client ID nor this service grants authority.
/// Uses the same atomic execution claim namespace as placement and the existing ownership-aware
/// cancellation service. Never retries, infers a fill, or places a replacement.</summary>
public sealed class ClientCancellationService
{
    private readonly ITradingRepository _repository;
    private readonly Func<WorkingOrderCancelRequest, string,
        Func<BrokerWorkingOrder, CancellationToken, Task<IReadOnlyList<WorkingOrderOwner>>>?, CancellationToken, Task<WorkingOrderCancelResult>> _cancel;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ClientCancellationService(ITradingRepository repository, WorkingOrderCancellationService cancellations)
        : this(repository, cancellations.CancelAsync) { }
    internal ClientCancellationService(ITradingRepository repository, Func<WorkingOrderCancelRequest, string,
        Func<BrokerWorkingOrder, CancellationToken, Task<IReadOnlyList<WorkingOrderOwner>>>?, CancellationToken, Task<WorkingOrderCancelResult>> cancel)
    { _repository = repository; _cancel = cancel; }

    public async Task<ClientCancellationResult> ExecuteAsync(WorkingOrderCancelRequest request, string requestId,
        string verifiedScope, string actor,
        Func<BrokerWorkingOrder, CancellationToken, Task<IReadOnlyList<WorkingOrderOwner>>>? editionOwners = null,
        CancellationToken ct = default)
    {
        if (_repository is not IClientActionOperationRepository actions) throw new NotSupportedException("Recoverable cancellation is unavailable.");
        static string Clean(string? value, int max) => value is { Length: > 0 } && value.Length <= max && !value.Any(char.IsControl)
            ? value.Trim() : throw new ArgumentException("Invalid cancellation binding.");
        // Detach the owner's list before hashing and the first await; caller mutation cannot change the action.
        var order = request with { OrderNo = Clean(request.OrderNo, 128), Symbol = Clean(request.Symbol, 32).ToUpperInvariant(),
            ExpectedOwners = request.ExpectedOwners?.ToArray() };
        if (order.OrderNo.Length == 0 || order.Symbol.Length == 0) throw new ArgumentException("Invalid cancellation target.");
        verifiedScope = Clean(verifiedScope, 512); actor = Clean(actor, 128);
        if (verifiedScope.Length == 0 || actor.Length == 0 || !ClientOrderRequestId.TryGetExpiry(requestId, out _)) throw new ArgumentException("Invalid cancellation binding.");
        var json = JsonSerializer.Serialize(new { operationKind = "cancel", version = 1, verifiedScope, actor, request = order }, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 16_384) throw new ArgumentException("Cancellation binding is too large.");
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        var prior = await actions.FindClientOrderOperationAsync(requestId, ct);
        if (prior is not null)
        {
            if (prior.Kind != "cancel" || prior.RequestHash != hash) throw new ClientOrderConflictException();
            return Project(prior, true);
        }
        var claim = await actions.TryBeginClientActionOperationAsync("cancel", requestId, hash, json, "client-cancel-v1", ct);
        if (claim.Conflict) throw new ClientOrderConflictException();
        if (!claim.Acquired) return Project(claim.Operation, true);
        string outcome, state;
        try
        {
            var result = await _cancel(order, actor, editionOwners, ct);
            outcome = KnownOutcome(result.Outcome);
            state = outcome == "cancelled" ? "accepted" : outcome is "not_confirmed" or "attention" or "cancelling" or "unconfirmed" ? "unknown" : "failed";
        }
        catch { outcome = "unconfirmed"; state = "unknown"; }
        // Request cancellation cannot erase a claim after a possible broker action. A small independent
        // persistence deadline records uncertainty; if that write fails, submitting still forbids replay.
        try
        {
            using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _repository.CompleteExecutionAsync(claim.Operation.ExecutionId, state, JsonSerializer.Serialize(new
            {
                operationKind = "cancel", cancellationOutcome = outcome, executed = outcome == "cancelled", isReplay = false,
                executionId = claim.Operation.ExecutionId, policyVersion = "client-cancel-v1",
                reason = Description(outcome), groups = Array.Empty<object>()
            }, Json), persist.Token).WaitAsync(persist.Token);
        }
        catch { return new(requestId, claim.Operation.ExecutionId, "unknown", "unconfirmed", false); }
        return new(requestId, claim.Operation.ExecutionId, state, outcome, false);
    }

    public static ClientCancellationResult Project(ClientOrderOperation operation, bool replay = true)
    {
        if (operation.Kind != "cancel") throw new ClientOrderConflictException();
        var outcome = "unconfirmed";
        if (operation.ResultJson is { Length: > 0 and <= 8192 } raw)
        {
            try
            {
                using var d = JsonDocument.Parse(raw);
                if (d.RootElement.GetProperty("operationKind").GetString() == "cancel")
                    outcome = KnownOutcome(d.RootElement.GetProperty("cancellationOutcome").GetString());
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        // Operator resolution remains distinct from the interrupted result; it does not rewrite it.
        if (operation.State is "resolved_cancelled" or "resolved_not_cancelled" or "resolved_filled") outcome = operation.State;
        return new(operation.RequestId, operation.ExecutionId, operation.State, outcome, replay);
    }
    private static string KnownOutcome(string? s) => s is "cancelled" or "gone_possibly_filled" or "not_confirmed"
        or "needs_acknowledgement" or "not_resting" or "unreadable" or "invalid" or "fulfilled" or "attention" or "cancelling" or "ambiguous" ? s : "unconfirmed";
    private static string Description(string s) => s switch
    {
        "cancelled" => "The broker confirmed cancellation. This is not a fill report.",
        "gone_possibly_filled" or "not_resting" or "fulfilled" => "The order is no longer resting and may have filled. Verify fills before any replacement.",
        "needs_acknowledgement" or "ambiguous" => "Ownership changed or needs acknowledgement. Review the current order before acting.",
        "invalid" or "unreadable" => "Cancellation could not proceed with the current target or broker evidence.",
        _ => "Cancellation is unconfirmed. Do not repeat this request or place a replacement. Verify at the broker."
    };
}
