using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingAgent.Models;

namespace TradingAgent.Manager;

/// <summary>
/// Immutable, one-time, expiring record that binds an approved execution to the exact orders,
/// source message, and policy version it was validated against. The hash covers only the
/// execution-relevant order fields (action, symbol, price, quantity, order type) so it is
/// reproducible from the submitted groups. <see cref="TradingManager"/> recomputes the hash
/// immediately before broker submission; a changed price, quantity, policy version, expired
/// intent, or replayed intent is rejected there.
/// </summary>
public sealed record ApprovalIntent(
    string IntentId,
    string SourceMessage,
    string PolicyVersion,
    decimal EstimatedExposurePkr,
    DateTime CreatedUtc,
    DateTime ExpiresUtc,
    string IntegrityHash)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ApprovalIntent Create(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups,
        string? sourceMessage,
        string policyVersion,
        TimeSpan timeToLive)
    {
        var now = DateTime.UtcNow;
        var exposure = groups.SelectMany(g => g)
            .Where(s => string.Equals(s.Action, "BUY", StringComparison.OrdinalIgnoreCase))
            .Sum(s => (s.EntryPrice ?? 0m) * (s.Quantity ?? 0));

        return new ApprovalIntent(
            Guid.NewGuid().ToString("N"),
            sourceMessage?.Trim() ?? string.Empty,
            policyVersion,
            exposure,
            now,
            now + timeToLive,
            ComputeHash(groups, sourceMessage, policyVersion));
    }

    public static string ComputeHash(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups,
        string? sourceMessage,
        string policyVersion)
    {
        var canonical = new
        {
            source = sourceMessage?.Trim() ?? string.Empty,
            policyVersion,
            groups = groups.Select(group => group.Select(s => new
            {
                action = s.Action?.Trim().ToUpperInvariant(),
                symbol = s.Symbol?.Trim().ToUpperInvariant(),
                price = s.EntryPrice,
                quantity = s.Quantity,
                orderType = s.OrderType?.Trim().ToUpperInvariant()
            }).ToList()).ToList()
        };
        var payload = JsonSerializer.Serialize(canonical, Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}

/// <summary>
/// In-memory single-use store for approval intents. An intent can be consumed exactly once;
/// a second consume attempt (a replay) fails. Expired intents are pruned opportunistically.
/// </summary>
public sealed class ApprovalIntentRegistry
{
    private readonly ConcurrentDictionary<string, ApprovalIntent> _intents = new();

    public void Register(ApprovalIntent intent)
    {
        PruneExpired();
        _intents[intent.IntentId] = intent;
    }

    public bool TryConsume(string intentId, out ApprovalIntent? intent)
    {
        var removed = _intents.TryRemove(intentId, out var found);
        intent = found;
        return removed;
    }

    private void PruneExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, intent) in _intents)
            if (now > intent.ExpiresUtc)
                _intents.TryRemove(id, out _);
    }
}
