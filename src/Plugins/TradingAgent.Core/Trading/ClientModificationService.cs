using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TradingAgent.Manager;
using TradingAgent.Persistence;

namespace TradingAgent.Trading;

public sealed record ClientModificationIds(string Parent, string Cancellation, string Replacement)
{
    public static ClientModificationIds From(string parent)
    {
        if (!ClientOrderRequestId.TryGetExpiry(parent, out _)) throw new ArgumentException("Invalid modification request ID.");
        string Child(string stage) => parent[..15] + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "client-modification-v1:" + stage + ":" + parent)))[..32].ToLowerInvariant();
        return new(parent, Child("cancel"), Child("replace"));
    }
}
public sealed record ClientModificationResult(string RequestId, string ExecutionId, string State,
    string CancellationOutcome, ClientModificationIds Operations, bool IsReplay);
public sealed record ClientModificationFillEvidence(string Id, int Quantity, decimal Price, DateTime OccurredUtc, int RemainingQuantity);
public sealed class ClientModificationEvidenceException : InvalidOperationException
{ public ClientModificationEvidenceException() : base("Modification fill evidence is incomplete, changed or could not be retained.") { } }

/// <summary>Durable first half of a reviewed cancel-and-replace workflow. The authenticated caller
/// supplies reviewed server terms/scope and an ownership-aware cancellation callback. This class
/// grants no authority and NEVER places a replacement. It claims the parent before that callback;
/// replay/restart is lookup-only, including failed/unknown cancellation. Replacement requires its
/// separately confirmed child order after fresh complete fill reconciliation and approval.</summary>
public sealed class ClientModificationService(ITradingRepository repository)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<ClientModificationResult> CancelOriginalAsync(string requestId, string verifiedScope, string actor,
        string reviewedServerTermsJson, Func<ClientModificationIds, CancellationToken, Task<string>> cancel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        if (repository is not IClientModificationOperationRepository actions) throw new NotSupportedException("Recoverable modification is unavailable.");
        static bool Text(string? value, int max) => value is { Length: > 0 } && value.Length <= max && !value.Any(char.IsControl);
        if (!Text(verifiedScope, 512) || !Text(actor, 128) || !Text(reviewedServerTermsJson, 12_288)
            || Encoding.UTF8.GetByteCount(reviewedServerTermsJson) > 12_288) throw new ArgumentException("Invalid modification binding.");
        var ids = ClientModificationIds.From(requestId);
        using var terms = JsonDocument.Parse(reviewedServerTermsJson, new JsonDocumentOptions { MaxDepth = 8 });
        if (terms.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid modification terms.");
        // Snapshot before the first await. No caller-provided fingerprint, child identity or execution state.
        var canonical = JsonSerializer.Serialize(new { operationKind = "modify", version = 1, verifiedScope, actor,
            operations = ids, terms = terms.RootElement }, Json);
        if (Encoding.UTF8.GetByteCount(canonical) > 16_384) throw new ArgumentException("Modification binding is too large.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var claim = await actions.TryBeginClientActionOperationAsync("modify", requestId, hash, canonical, "client-modify-v1", ct);
        if (claim.Conflict) throw new ClientOrderConflictException();
        if (!claim.Acquired) return Project(claim.Operation);
        string outcome;
        try { outcome = Outcome(await cancel(ids, ct)); }
        catch { outcome = "unconfirmed"; }
        var state = outcome == "cancelled" ? "accepted" : outcome is "not_confirmed" or "attention" or "cancelling" or "unconfirmed" ? "unknown" : "failed";
        try
        {
            using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await repository.CompleteExecutionAsync(claim.Operation.ExecutionId, state, JsonSerializer.Serialize(new {
                operationKind = "modify", cancellationOutcome = outcome, operations = ids,
                stage = outcome == "cancelled" ? "cancelled-awaiting-review" : "replacement-blocked",
                // A successful cancellation is NOT a successful replacement or broker fill.
                executed = false, groups = Array.Empty<object>()
            }, Json), persist.Token).WaitAsync(persist.Token);
        }
        catch { return new(requestId, claim.Operation.ExecutionId, "unknown", "unconfirmed", ids, false); }
        return new(requestId, claim.Operation.ExecutionId, state, outcome, ids, false);
    }
    public static ClientModificationResult Project(ClientOrderOperation operation)
    {
        if (operation.Kind != "modify") throw new ClientOrderConflictException();
        var outcome = "unconfirmed";
        if (operation.ResultJson is { Length: > 0 and <= 16_384 } raw)
        {
            try {
                using var json = JsonDocument.Parse(raw);
                if (json.RootElement.GetProperty("operationKind").GetString() == "modify")
                    outcome = Outcome(json.RootElement.GetProperty("cancellationOutcome").GetString());
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
        }
        // In-progress, unknown or manually changed state can never become replacement permission.
        if (operation.State != "accepted") outcome = outcome == "cancelled" ? "unconfirmed" : outcome;
        return new(operation.RequestId, operation.ExecutionId, operation.State, outcome, ClientModificationIds.From(operation.RequestId), true);
    }
    /// <summary>Retains the complete exact fill set already observed after confirmed cancellation.
    /// Later sources must contain every recorded execution unchanged. CAS prevents concurrent reads
    /// replacing a newer set with an older one; restart keeps the same rule. No order is submitted.</summary>
    public async Task<ClientOrderOperation> RememberPostCancellationFillsAsync(string requestId,
        IReadOnlyList<ClientModificationFillEvidence> freshFills, CancellationToken ct = default)
    {
        if (repository is not IClientModificationOperationRepository progress) throw new ClientModificationEvidenceException();
        var snapshot = freshFills?.ToArray();
        if (!ValidFills(snapshot)) throw new ClientModificationEvidenceException();
        snapshot = snapshot!.OrderBy(f => f.Id, StringComparer.Ordinal).ToArray();
        for (var attempt = 0; attempt < 4; attempt++) {
            var parent = await progress.FindClientOrderOperationAsync(requestId, ct);
            if (parent is not { Kind: "modify", State: "accepted", ResultJson.Length: > 0 and <= 16_384 }
                || Project(parent).CancellationOutcome != "cancelled") throw new ClientModificationEvidenceException();
            var node = JsonNode.Parse(parent.ResultJson) as JsonObject ?? throw new ClientModificationEvidenceException();
            var prior = RecordedPostCancellationFills(parent) ?? [];
            if (!prior.All(f => snapshot!.Contains(f))) throw new ClientModificationEvidenceException();
            if (prior.OrderBy(f => f.Id, StringComparer.Ordinal).SequenceEqual(snapshot!)) return parent;
            node["postCancelFills"] = JsonSerializer.SerializeToNode(snapshot, Json);
            var json = node.ToJsonString(Json);
            if (Encoding.UTF8.GetByteCount(json) > 16_384) throw new ClientModificationEvidenceException();
            bool changed;
            try { changed = await progress.CompareExchangeModificationResultAsync(requestId, parent.ResultJson, json, ct); }
            catch {
                // If possible, leave an uncertain parent after learning facts that could not be
                // durably retained. Never grant replacement permission after a failed progress save.
                try { using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await repository.CompleteExecutionAsync(parent.ExecutionId, "unknown", parent.ResultJson, persist.Token).WaitAsync(persist.Token); }
                catch { /* Durable storage unavailable; this request still refuses execution. */ }
                throw new ClientModificationEvidenceException();
            }
            if (changed)
                return (await progress.FindClientOrderOperationAsync(requestId, ct)) ?? throw new ClientModificationEvidenceException();
        }
        throw new ClientModificationEvidenceException();
    }
    public static IReadOnlyList<ClientModificationFillEvidence>? RecordedPostCancellationFills(ClientOrderOperation parent)
    {
        if (parent.Kind != "modify" || parent.ResultJson is not { Length: > 0 and <= 16_384 }) throw new ClientModificationEvidenceException();
        using var doc = JsonDocument.Parse(parent.ResultJson, new JsonDocumentOptions { MaxDepth = 8 });
        if (!doc.RootElement.TryGetProperty("postCancelFills", out var value)) return null;
        var fills = value.Deserialize<ClientModificationFillEvidence[]>(Json);
        if (!ValidFills(fills)) throw new ClientModificationEvidenceException();
        return Array.AsReadOnly(fills!);
    }
    private static bool ValidFills(IReadOnlyList<ClientModificationFillEvidence>? values) => values is { Count: <= 64 }
        && values.All(f => f is not null) && values.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() == values.Count
        && values.All(f => f.Id is { Length: 70 } && Regex.IsMatch(f.Id, @"\Afill1_[a-f0-9]{64}\z") && f.Quantity > 0 && f.Price > 0
            && f.RemainingQuantity >= 0 && f.OccurredUtc.Kind == DateTimeKind.Utc && f.OccurredUtc >= DateTime.UnixEpoch && f.OccurredUtc <= DateTime.UtcNow.AddSeconds(5));
    private static string Outcome(string? value) => value is "cancelled" or "gone_possibly_filled" or "not_confirmed" or "needs_acknowledgement"
        or "not_resting" or "unreadable" or "invalid" or "fulfilled" or "attention" or "cancelling" or "ambiguous" ? value : "unconfirmed";
}
