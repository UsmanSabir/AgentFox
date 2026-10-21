using System.Globalization;
using System.Text.RegularExpressions;

namespace TradingAgent.Persistence;

/// <summary>
/// Optional capability of the account's existing execution ledger. Claims and operation association
/// are one transaction, before broker submission. No independent operation table or expiry worker.
/// </summary>
public interface IClientOrderOperationRepository
{
    Task<ClientOrderOperation?> FindClientOrderOperationAsync(string requestId, CancellationToken ct = default);
    Task<ClientOrderOperationClaim> TryBeginClientOrderOperationAsync(string requestId, string requestHash,
        string effectiveRequestJson, string policyVersion, CancellationToken ct = default);
}

public sealed record ClientOrderOperation(string RequestId, string RequestHash, string ExecutionId,
    string State, string PolicyVersion, string? ResultJson, DateTime CreatedUtc, DateTime UpdatedUtc, DateTime ExpiresUtc);
public sealed record ClientOrderOperationClaim(bool Acquired, bool Conflict, ClientOrderOperation Operation);

/// <summary>
/// Timestamped opaque request identity, not authorization. New claims expire after 24 hours, so a
/// pruned old ID cannot submit again. Lookup/replay of a retained row remains possible after expiry.
/// Generate with trusted server time and 128 random bits; never renew or replace it on a lost reply.
/// </summary>
public static partial class ClientOrderRequestId
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    [GeneratedRegex(@"\Aop1_([0-9]{10})_([a-f0-9]{32})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Format();

    public static bool TryGetExpiry(string? value, out DateTime expiresUtc)
    {
        expiresUtc = default;
        if (value is null || value.Length != 47) return false;
        var match = Format().Match(value);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return false;
        expiresUtc = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.Add(Lifetime);
        return true;
    }

    public static bool CanStart(string requestId, DateTime now) => TryGetExpiry(requestId, out var expiry)
        && expiry > now && expiry - Lifetime <= now.AddSeconds(5);

    /// <summary>The source to bind when constructing the existing single-use approval intent.</summary>
    public static string SourceMessage(string requestId) => TryGetExpiry(requestId, out _)
        ? "client-order:" + requestId : throw new ArgumentException("Invalid client request ID.", nameof(requestId));
}
