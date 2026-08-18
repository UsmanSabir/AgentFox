using TradingAgent.Models;

namespace TradingAgent.Watchlist;

/// <summary>What has to happen before an armed order is sent.</summary>
public enum ArmedTriggerKind
{
    /// <summary>Fire once the last price reaches or falls below the trigger. A protective exit.</summary>
    PriceBelow,

    /// <summary>Fire once the last price reaches or rises above the trigger. A breakout entry.</summary>
    PriceAbove,

    /// <summary>
    /// Fire when the monitor raises a specific alert kind for this symbol — a bounce off support, a
    /// break, a trend flip. This is the kind the broker cannot express, and therefore the reason a
    /// locally-evaluated trigger exists at all.
    /// </summary>
    Event
}

/// <summary>
/// An order waiting for a condition, plus everything needed to explain it later.
///
/// <para>
/// <b>Prefer the broker's own stop where one exists.</b> A native Stop Loss rests at the exchange and
/// fires whether or not this process is running; an armed order here only fires while AgentFox is up
/// and the market is open. That difference is surfaced in the UI rather than buried, because a stop
/// that silently cannot fire is worse than no stop.
/// </para>
/// </summary>
public sealed record ArmedOrder
{
    public required string ArmedId { get; init; }
    public required string Symbol { get; init; }

    public required ArmedTriggerKind TriggerKind { get; init; }

    /// <summary>Level for a price trigger; null for an event trigger.</summary>
    public decimal? TriggerPrice { get; init; }

    /// <summary>Alert kind for an event trigger; null for a price trigger.</summary>
    public AlertKind? TriggerAlertKind { get; init; }

    // ── The order to place when it fires ──────────────────────────────────────
    public required string Action { get; init; }        // BUY | SELL
    public required int Quantity { get; init; }
    public required string OrderType { get; init; }     // LIMIT | MARKET | STOPLOSS
    public decimal? Price { get; init; }                // limit price, or the stop's own trigger
    public decimal? LimitPrice { get; init; }           // stop-limit only

    // ── State ─────────────────────────────────────────────────────────────────
    /// <summary><c>armed</c> | <c>fired</c> | <c>cancelled</c> | <c>expired</c> | <c>failed</c>.</summary>
    public string State { get; init; } = "armed";

    public DateTime ArmedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Null never expires. An entry trigger with no expiry can fire months later, so the UI defaults one.</summary>
    public DateTime? ExpiresUtc { get; init; }

    public DateTime? FiredUtc { get; init; }
    public string? ExecutionId { get; init; }
    public string? StateReason { get; init; }

    /// <summary>Free text from whoever armed it.</summary>
    public string? Note { get; init; }

    /// <summary>Alert this was armed from, when it came from an alert card.</summary>
    public string? SourceAlertId { get; init; }

    /// <summary>
    /// Set when this order is the local backstop for a <see cref="ProtectiveStop"/>.
    ///
    /// <para>
    /// A backstop is not an ordinary armed order: it must stand down while the native stop it backs
    /// is resting at the broker, or the two of them sell the same position twice. The monitor checks
    /// the outstanding book before firing anything carrying this id — see
    /// <see cref="ProtectiveStopDecisions.BackstopShouldStandDown"/>.
    /// </para>
    /// </summary>
    public string? ProtectiveStopId { get; init; }

    /// <summary>Projects the armed order onto the signal the trading manager executes.</summary>
    public TradingSignal ToSignal() => new()
    {
        IsSignal   = true,
        Action     = Action,
        Symbol     = Symbol,
        Quantity   = Quantity,
        OrderType  = OrderType,
        EntryPrice = Price,
        LimitPrice = LimitPrice,
        // Carried through deliberately: without it the risk engine has to infer the trigger's
        // direction from the side, which is wrong for a dip-buy and a sell-into-strength alike.
        FiresOnRisingPrice = TriggerKind switch
        {
            ArmedTriggerKind.PriceAbove => true,
            ArmedTriggerKind.PriceBelow => false,
            _                           => null
        },
        RawMessage = $"armed:{ArmedId}"
    };
}

/// <summary>
/// Decides whether an armed order's condition is met. Pure, so every rule below is table-testable
/// rather than something to be discovered in a live market.
/// </summary>
public static class ArmedOrderEvaluator
{
    /// <summary>
    /// True when <paramref name="order"/> should fire now. <paramref name="reason"/> always describes
    /// the decision, including when the answer is no and why.
    /// </summary>
    public static bool ShouldFire(
        ArmedOrder order,
        decimal? lastPrice,
        IReadOnlyCollection<AlertKind> alertsFiredForSymbol,
        DateTime nowUtc,
        out string reason)
    {
        if (order.State != "armed")
        {
            reason = $"Not armed (state: {order.State}).";
            return false;
        }

        if (order.ExpiresUtc is { } expiry && nowUtc >= expiry)
        {
            reason = $"Expired at {expiry:u} without triggering.";
            return false;
        }

        switch (order.TriggerKind)
        {
            case ArmedTriggerKind.PriceBelow:
            case ArmedTriggerKind.PriceAbove:
                if (order.TriggerPrice is not { } level || level <= 0)
                {
                    reason = "Price trigger has no usable level.";
                    return false;
                }

                if (lastPrice is not { } price || price <= 0)
                {
                    // No price is NOT a reason to fire, and it is not a reason to expire either — the
                    // feed can lapse for a pass. Firing on a missing price would be the worst of both.
                    reason = "No live price this pass; cannot evaluate.";
                    return false;
                }

                var hit = order.TriggerKind == ArmedTriggerKind.PriceBelow
                    ? price <= level
                    : price >= level;

                reason = hit
                    ? $"Last {price} {(order.TriggerKind == ArmedTriggerKind.PriceBelow ? "at or below" : "at or above")} trigger {level}."
                    : $"Last {price} has not reached trigger {level}.";
                return hit;

            case ArmedTriggerKind.Event:
                if (order.TriggerAlertKind is not { } kind)
                {
                    reason = "Event trigger has no alert kind.";
                    return false;
                }

                var fired = alertsFiredForSymbol.Contains(kind);
                reason = fired
                    ? $"{kind} raised for {order.Symbol} this pass."
                    : $"{kind} has not been raised.";
                return fired;

            default:
                reason = $"Unknown trigger kind {order.TriggerKind}.";
                return false;
        }
    }
}
