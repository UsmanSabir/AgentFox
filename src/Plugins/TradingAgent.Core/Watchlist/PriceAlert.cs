using System.Text.Json.Serialization;

namespace TradingAgent.Watchlist;

public sealed record PriceAlertInput(string? Symbol, string? Direction, decimal Target);

public sealed record PriceAlertObservation(decimal Value, DateTime ObservedUtc, string Source);

public sealed record PriceAlert(
    Guid Id, string Symbol, string Direction, decimal Target, DateTime CreatedUtc, DateTime ExpiresUtc,
    string State = "watching", PriceAlertObservation? Trigger = null, DateTime? FinishedUtc = null,
    DateTime? NextDeliveryUtc = null, int RecipientCount = 0, bool PushQueued = false,
    // Omitted while false: the repository's compare-and-set matches the stored JSON exactly,
    // so a newly serialized member would make rows written before it unwritable.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ChannelUnconfirmed = false)
{
    public const string Index = "KSE100";
    public bool Matches(decimal value) => Direction == "above" ? value >= Target : value <= Target;

    public static PriceAlert? Create(PriceAlertInput input, DateTime now, Guid? id = null)
    {
        var symbol = input.Symbol?.Trim().ToUpperInvariant();
        if (symbol is "KSE 100" or "KSE-100") symbol = Index;
        var direction = input.Direction?.Trim().ToLowerInvariant();
        if (symbol is not { Length: >= 1 and <= 16 }
            || !char.IsAsciiLetterOrDigit(symbol[0])
            || symbol.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.'))
            || direction is not ("above" or "below")
            || input.Target is <= 0 or > 100_000_000 || decimal.Round(input.Target, 4) != input.Target)
            return null;
        return new(id ?? Guid.NewGuid(), symbol, direction, input.Target, now, now.AddDays(30));
    }
}

/// <summary>Optional edition-specific push transport. A true result means durably queued.</summary>
public interface IPriceAlertPush
{
    Task<bool> TryQueueAsync(PriceAlert alert, CancellationToken ct);
}

/// <summary>
/// Optional edition-specific KSE 100 reading for index alerts, asked before PSX's intraday series. Null means it has
/// no current reading; the worker then reads PSX. A reading must carry the time the index had that value, not the
/// time it was fetched, because the worker's freshness rule is what stops a previous session's value firing.
/// </summary>
public interface IIndexAlertSource
{
    Task<PriceAlertObservation?> ObserveIndexAsync(CancellationToken ct);
}
