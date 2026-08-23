namespace TradingAgent.Trading;

/// <summary>
/// A local good-until-expiry intent. The venue only accepts DAY orders, so one native order is
/// materialised per trading date until the requested quantity has filled or the intent expires.
/// </summary>
public sealed record PersistentOrderIntent
{
    public required string IntentId { get; init; }
    public required string Symbol { get; init; }
    public required string Action { get; init; }       // BUY | SELL
    public required int Quantity { get; init; }
    public required string OrderType { get; init; }    // LIMIT | STOPLOSS
    public decimal? Price { get; init; }
    public decimal? LimitPrice { get; init; }

    /// <summary>
    /// The inclusive lifetime of the local intent. Once reached, no new order may be submitted and
    /// any still-resting native order must be cancelled and verified before the intent is terminal.
    /// </summary>
    public required DateTime ExpiresUtc { get; init; }

    /// <summary>
    /// active | placing | resting | partial | expiring | cancelling | attention | fulfilled | expired | cancelled
    /// </summary>
    public string State { get; init; } = "active";

    /// <summary>Cumulative quantity filled by this intent's exact broker order numbers.</summary>
    public int FilledQuantity { get; init; }

    /// <summary>Trading date on which a placement was last claimed.</summary>
    public DateOnly? LastAttemptSessionDate { get; init; }

    public int AttemptCount { get; init; }
    public string? LastOrderNo { get; init; }
    public string? SourceArmedId { get; init; }
    public string? StateReason { get; init; }
    public string? Note { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? TerminalUtc { get; init; }

    public int RemainingQuantity => Math.Max(0, Quantity - FilledQuantity);
    public bool IsTerminal => State is "fulfilled" or "expired" or "cancelled";

    public TradingAgent.Models.TradingSignal ToSignal(int quantity) => new()
    {
        IsSignal = true,
        Action = Action,
        Symbol = Symbol,
        Quantity = quantity,
        OrderType = OrderType,
        EntryPrice = Price,
        LimitPrice = LimitPrice,
        Confidence = "HIGH",
        PreservePriceIntent = true,
        RawMessage = $"persistent-order:{IntentId}"
    };
}

/// <summary>One native DAY-order attempt made for a persistent intent.</summary>
public sealed record PersistentOrderPlacement
{
    public required string PlacementId { get; init; }
    public required string IntentId { get; init; }
    public required DateOnly SessionDate { get; init; }
    public required int Attempt { get; init; }
    public required int Quantity { get; init; }
    public string? BrokerOrderNo { get; init; }
    public string? ExecutionId { get; init; }
    public string State { get; init; } = "accepted"; // accepted | lapsed | failed | unknown
    public decimal? RequestedPrice { get; init; }
    public decimal? SubmittedPrice { get; init; }
    public string? Message { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}

public readonly record struct PersistentOrderAttemptClaim(bool Acquired, int Attempt);

/// <summary>Pure decisions shared by the worker and table tests.</summary>
public static class PersistentOrderDecisions
{
    public static string? ValidateEligibility(string? orderType) =>
        (orderType ?? "").Trim().ToUpperInvariant() switch
        {
            "LIMIT" or "STOPLOSS" => null,
            "MARKET" => "Market orders are one-shot and cannot be re-placed automatically.",
            _ => "Only LIMIT and STOPLOSS orders can be kept working across trading days."
        };

    public static int QuantityToPlace(PersistentOrderIntent intent, int filled, int? availableToSell = null)
    {
        var remaining = Math.Max(0, intent.Quantity - Math.Max(0, filled));
        if (intent.Action.Equals("SELL", StringComparison.OrdinalIgnoreCase)
            && availableToSell is { } available)
            return Math.Min(remaining, Math.Max(0, available));
        return remaining;
    }

    public static bool MayAttempt(
        PersistentOrderIntent intent,
        DateTime nowUtc,
        DateOnly sessionDate,
        bool ownOrderIsResting,
        out string reason)
    {
        if (intent.IsTerminal)
        {
            reason = $"Intent is terminal ({intent.State}).";
            return false;
        }

        if (intent.State is "attention" or "expiring")
        {
            reason = $"Intent needs reconciliation ({intent.State}).";
            return false;
        }

        if (nowUtc >= intent.ExpiresUtc)
        {
            reason = $"Intent expired at {intent.ExpiresUtc:u}.";
            return false;
        }

        if (intent.RemainingQuantity <= 0)
        {
            reason = "The requested quantity is fully filled.";
            return false;
        }

        if (ownOrderIsResting)
        {
            reason = "A native order for this intent is still resting at the broker.";
            return false;
        }

        if (intent.LastAttemptSessionDate == sessionDate)
        {
            reason = $"A placement was already attempted for {sessionDate:yyyy-MM-dd}.";
            return false;
        }

        reason = $"{intent.RemainingQuantity} share(s) remain and no order is resting.";
        return true;
    }

    /// <summary>
    /// An accepted order from a prior date is not proof of non-fill merely because DAY orders are no
    /// longer visible. If the process missed that close, automatic replacement could duplicate a fill.
    /// </summary>
    public static bool PriorOutcomeWasNotObserved(
        PersistentOrderIntent intent,
        PersistentOrderPlacement? latestPlacement,
        DateOnly today) =>
        intent.LastAttemptSessionDate is { } priorDate
        && priorDate < today
        && latestPlacement?.State == "accepted";
}
