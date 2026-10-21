using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingAgent.Models;
using TradingAgent.Persistence;

namespace TradingAgent.Manager;

public sealed class ClientOrderConflictException : InvalidOperationException
{
    public ClientOrderConflictException() : base("Client request ID is already associated with a different order. No new submission was made.") { }
}

public sealed partial class TradingManager
{
    private sealed record ClientOrderContext(string RequestId, string RequestHash);

    /// <summary>
    /// Explicit opt-in for ONE immediate client order. Existing dashboard/automation callers retain
    /// their current contract. Does not implement persistent instructions, attachments or HTTP auth.
    /// All execution gates below still apply to a new claim; replay is a ledger read only.
    /// </summary>
    public async Task<TradingExecutionResult> ExecuteClientOrderAsync(TradingSignal order, string requestId,
        ExecutionAuthorization? authorization = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (!ClientOrderRequestId.TryGetExpiry(requestId, out _)) throw new ArgumentException("Invalid client request ID.", nameof(requestId));
        if (_repository is not IClientOrderOperationRepository operations)
            return Reject(_policyProvider.Current().Version, "This ledger does not support client operation recovery.");

        // Fingerprint the ORIGINAL requested maximum, before SELL sizing, policy changes or awaits.
        // Clone it too: mutation of the caller's object must not change what this hash authenticated.
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups = [[order]];
        var original = JsonSerializer.Serialize(groups, Json);
        if (Encoding.UTF8.GetByteCount(original) > 65_536) throw new ArgumentException("Client order is too large.", nameof(order));
        var snapshot = JsonSerializer.Deserialize<List<List<TradingSignal>>>(original, Json)!;
        // TradingSignal initializes Timestamp to now, including when a request is reconstructed.
        // It is observation metadata, not an order choice; every other supplied field stays bound.
        var timestamp = snapshot[0][0].Timestamp;
        snapshot[0][0].Timestamp = DateTime.UnixEpoch;
        var canonical = JsonSerializer.Serialize(snapshot, Json);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        snapshot[0][0].Timestamp = timestamp;
        groups = snapshot.Select(g => (IReadOnlyList<TradingSignal>)g).ToArray();

        var prior = await operations.FindClientOrderOperationAsync(requestId, ct);
        if (prior is not null)
        {
            if (prior.RequestHash != hash) throw new ClientOrderConflictException();
            return ReplayClientOrder(prior);
        }
        if (!ClientOrderRequestId.CanStart(requestId, DateTime.UtcNow))
            throw new InvalidOperationException("Client request ID expired or is in the future. No submission was made.");
        return await ExecuteTrackedAsync(groups, ClientOrderRequestId.SourceMessage(requestId), authorization, ct, new(requestId, hash));
    }

    private static TradingExecutionResult ReplayClientOrder(ClientOrderOperation operation)
    {
        // Manual reconciliation changes state but deliberately preserves the original unknown result.
        // Never deserialize that old result as a fresh ordinary success after operator resolution.
        if (operation.State is "accepted" or "failed" or "simulated" && operation.ResultJson is { Length: > 0 } raw)
        {
            try
            {
                var result = JsonSerializer.Deserialize<TradingExecutionResult>(raw, Json);
                if (result?.ExecutionId == operation.ExecutionId) return result with { IsReplay = true };
            }
            catch (JsonException) { /* the recorded state still forbids another submission */ }
        }
        return new(false, true, operation.ExecutionId, operation.PolicyVersion,
            $"Client operation is recorded as '{operation.State}'. No new submission was made; reconcile any uncertain outcome.", []);
    }
}
