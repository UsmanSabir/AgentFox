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
    Event,

    /// <summary>
    /// Fire once the price has fallen <see cref="ArmedOrder.TriggerPercent"/>% below
    /// <see cref="ArmedOrder.ReferencePrice"/>. "Get me out if it starts dropping", expressed as a
    /// move rather than as a level — which is how a fall is actually reasoned about, and which does
    /// not require the person arming it to know where support sits.
    ///
    /// <para>
    /// With <see cref="ArmedOrder.Trailing"/> set the reference follows the highest price seen since
    /// arming, so the level ratchets UP as the position gains and never down. That is a trailing stop,
    /// and it is the case a fixed level genuinely cannot express.
    /// </para>
    /// </summary>
    PercentDrop,

    /// <summary>
    /// The mirror of <see cref="PercentDrop"/>: fire once the price has risen
    /// <see cref="ArmedOrder.TriggerPercent"/>% above the reference. Trailing follows the LOWEST price
    /// seen, so a breakout entry chases a falling market down instead of expiring above it.
    /// </summary>
    PercentRise
}

/// <summary>
/// The arithmetic behind the percent triggers, in one pure place.
///
/// <para>
/// It lives here rather than inside the evaluator because three callers need the same answer: the
/// evaluator (does it fire), the arm endpoint (what level am I committing to, and is it already
/// breached), and the trail ratchet (where does the level move to). Three copies of
/// <c>reference * (1 - percent/100)</c> is three chances for the level shown to the user to differ
/// from the one that fires.
/// </para>
/// </summary>
public static class PercentTrigger
{
    /// <summary>Largest move that can be armed as a percent trigger.</summary>
    public const decimal MaxPercent = 50m;

    public static bool IsPercent(ArmedTriggerKind kind) =>
        kind is ArmedTriggerKind.PercentDrop or ArmedTriggerKind.PercentRise;

    /// <summary>
    /// Whether the trigger is reached by the price RISING. Null for an event trigger, which has no
    /// direction. The risk engine needs this to judge a stop-limit's geometry — see StopLimitRule.
    /// </summary>
    public static bool? FiresOnRisingPrice(ArmedTriggerKind kind) => kind switch
    {
        ArmedTriggerKind.PriceAbove or ArmedTriggerKind.PercentRise => true,
        ArmedTriggerKind.PriceBelow or ArmedTriggerKind.PercentDrop => false,
        _                                                           => null
    };

    /// <summary>
    /// The price level a percent trigger currently sits at, or null when the inputs cannot produce
    /// one. Rounded to the 2 decimals PSX quotes in, so the level fires at the number the user was
    /// shown rather than at an unrepresentable fraction of it.
    /// </summary>
    public static decimal? Level(ArmedTriggerKind kind, decimal? reference, decimal? percent)
    {
        if (!IsPercent(kind)) return null;
        if (reference is not { } from || from <= 0) return null;
        if (percent is not { } move || move <= 0 || move > MaxPercent) return null;

        var factor = kind == ArmedTriggerKind.PercentDrop
            ? 1m - move / 100m
            : 1m + move / 100m;

        var level = Math.Round(from * factor, 2, MidpointRounding.AwayFromZero);
        return level > 0 ? level : null;
    }
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

    /// <summary>
    /// Level for a price trigger; null for an event trigger.
    ///
    /// <para>
    /// For a PERCENT trigger this is the level the percentage currently works out to — materialised
    /// rather than left null so that everything already reading a trigger level (the panel, the
    /// stop-limit check, the disarm confirmation) keeps showing a real number. It is derived state:
    /// <see cref="ReferencePrice"/> and <see cref="TriggerPercent"/> are the truth, the evaluator
    /// recomputes from them, and a trailing order rewrites this alongside the reference. A stale copy
    /// therefore cannot fire an order early — it can only make a panel a pass out of date.
    /// </para>
    /// </summary>
    public decimal? TriggerPrice { get; init; }

    /// <summary>Alert kind for an event trigger; null for a price trigger.</summary>
    public AlertKind? TriggerAlertKind { get; init; }

    /// <summary>Size of the move, in percent, for a percent trigger. Null for every other kind.</summary>
    public decimal? TriggerPercent { get; init; }

    /// <summary>
    /// The price the percentage is measured FROM. Captured when the order is armed — normally the
    /// price on screen at that moment — and rewritten by the ratchet while <see cref="Trailing"/>.
    /// </summary>
    public decimal? ReferencePrice { get; init; }

    /// <summary>
    /// The reference follows the price in the favourable direction instead of staying where it was
    /// armed: the highest price seen for a drop trigger, the lowest for a rise trigger.
    ///
    /// <para>
    /// The ratchet is one-way by construction, so a trailing stop can only ever move the level away
    /// from a loss. A trail that could slip back down would quietly widen the risk the operator
    /// signed up for, which is the one thing a stop must not do.
    /// </para>
    /// </summary>
    public bool Trailing { get; init; }

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

    /// <summary>
    /// Once the local trigger fires, hand the LIMIT/STOPLOSS to the persistent DAY-order lifecycle
    /// instead of treating broker acceptance as completion.
    /// </summary>
    public bool PersistentUntilFilled { get; init; }

    /// <summary>
    /// A person armed this order by hand, rather than a strategy arming it as part of a plan.
    ///
    /// <para>
    /// The one thing that separates them at fire time is a manual-only symbol. "Manual" means the
    /// operator manages that name themselves — no strategy or plan may originate an order for it —
    /// and it deliberately does NOT mean the operator's own standing instructions stop working. An
    /// armed order carrying this flag is the operator's instruction, given in advance, so it fires on
    /// a manual-only symbol exactly as it fires anywhere else. See <c>TradingManager</c>, which is the
    /// boundary that enforces the distinction.
    /// </para>
    ///
    /// <para>
    /// Defaults to FALSE, so an order armed by anything that has not said otherwise — a strategy, a
    /// row written before this column existed — is treated as automation and stays refused on a
    /// manual-only symbol. Origination is claimed, never inferred.
    /// </para>
    /// </summary>
    public bool OperatorOriginated { get; init; }

    /// <summary>
    /// The level this order fires at as of right now: recomputed for a percent trigger, the stored
    /// level for a fixed one, null for an event.
    /// </summary>
    public decimal? EffectiveTriggerPrice =>
        PercentTrigger.IsPercent(TriggerKind)
            ? PercentTrigger.Level(TriggerKind, ReferencePrice, TriggerPercent)
            : TriggerKind == ArmedTriggerKind.Event ? null : TriggerPrice;

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
        FiresOnRisingPrice = PercentTrigger.FiresOnRisingPrice(TriggerKind),
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
            case ArmedTriggerKind.PercentDrop:
            case ArmedTriggerKind.PercentRise:
                // A percent trigger's level is recomputed from the reference and the percentage every
                // pass rather than read from the stored column. Those two are what the operator armed;
                // the stored level is a projection of them, and trusting the projection is how a trail
                // that failed to persist its last ratchet fires at yesterday's level.
                var percent = PercentTrigger.IsPercent(order.TriggerKind);
                var level = percent
                    ? PercentTrigger.Level(order.TriggerKind, order.ReferencePrice, order.TriggerPercent)
                    : order.TriggerPrice;

                if (level is not { } trigger || trigger <= 0)
                {
                    reason = percent
                        ? "Percent trigger has no usable reference price or percentage."
                        : "Price trigger has no usable level.";
                    return false;
                }

                if (lastPrice is not { } price || price <= 0)
                {
                    // No price is NOT a reason to fire, and it is not a reason to expire either — the
                    // feed can lapse for a pass. Firing on a missing price would be the worst of both.
                    reason = "No live price this pass; cannot evaluate.";
                    return false;
                }

                var falling = PercentTrigger.FiresOnRisingPrice(order.TriggerKind) == false;
                var hit = falling ? price <= trigger : price >= trigger;

                var how = falling ? "at or below" : "at or above";
                var basis = percent
                    ? $" ({order.TriggerPercent}% {(falling ? "below" : "above")} "
                      + $"{(order.Trailing ? "trailing reference " : "")}{order.ReferencePrice})"
                    : "";

                reason = hit
                    ? $"Last {price} {how} trigger {trigger}{basis}."
                    : $"Last {price} has not reached trigger {trigger}{basis}.";
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

    /// <summary>
    /// Where a trailing percent trigger's reference should move to, given this pass's price, or null
    /// when it should stay put.
    ///
    /// <para>
    /// Only ever returns a reference FURTHER in the favourable direction — higher for a drop trigger,
    /// lower for a rise trigger — so the level it implies cannot loosen. The caller persists the
    /// result under the same one-way guard, because two overlapping passes can otherwise write their
    /// prices in the wrong order and undo a ratchet that already happened.
    /// </para>
    ///
    /// <para>
    /// Evaluate the FIRE condition before calling this. Ratcheting cannot cause a fire (a price making
    /// a new extreme is by definition on the far side of the level), but doing the read-only question
    /// first means a fire never waits on a bookkeeping write.
    /// </para>
    /// </summary>
    public static decimal? NextTrailReference(ArmedOrder order, decimal? lastPrice)
    {
        if (!order.Trailing || order.State != "armed") return null;
        if (!PercentTrigger.IsPercent(order.TriggerKind)) return null;
        if (lastPrice is not { } price || price <= 0) return null;
        if (order.TriggerPercent is not { } move || move <= 0) return null;

        // A missing reference is adopted rather than ignored: an order armed while the feed was down
        // has nothing to measure from, and the first real price is the best available anchor.
        if (order.ReferencePrice is not { } reference || reference <= 0) return price;

        var improved = order.TriggerKind == ArmedTriggerKind.PercentDrop
            ? price > reference
            : price < reference;

        return improved ? price : null;
    }
}
