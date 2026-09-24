using TradingAgent.Trading;

namespace TradingAgent.Watchlist;

/// <summary>What to do about a fired take-profit that carries a pull-back level.</summary>
public enum PullbackAction
{
    /// <summary>Nothing to do this pass.</summary>
    None,

    /// <summary>The price fell to the pull-back level: cancel the working sell, verified, and re-arm.</summary>
    PullBack,

    /// <summary>
    /// The session is over and nothing of this order's can still be resting (the venue clears its book
    /// at the close). Retire the idle persistent order WITHOUT a broker call and re-arm, so tomorrow's
    /// open does not re-send a sell over a market that may have fallen overnight.
    /// </summary>
    HandBack,

    /// <summary>A pull-back was requested and its persistent order is now terminal: re-arm.</summary>
    Rearm,

    /// <summary>
    /// Stop watching this order for a pull-back. It filled, a person cancelled it, or its outcome needs
    /// a human — none of which a re-arm may override.
    /// </summary>
    Finish,

    /// <summary>A pull-back was requested and its cancel has not settled yet.</summary>
    Wait
}

public sealed record PullbackDecision(PullbackAction Action, string Reason);

/// <summary>The row a pulled-back order is re-armed as.</summary>
public sealed record PullbackRearm(
    int Quantity,
    decimal? TriggerPrice,
    decimal? PullbackPrice,
    int PullbackCount,
    int RearmCount,
    string Reason);

/// <summary>
/// The pull-back rule for a take-profit that fires ahead of its limit. Pure — no clock, no I/O — so every
/// branch is table-testable; <c>WatchlistMonitorWorker</c> does the gathering and the acting.
///
/// <para>
/// <b>Why it exists.</b> A target that fires only when the price reaches its limit arrives after the
/// touch, at the back of the queue, and misses brief touches. Firing a little early is safe on PRICE —
/// the limit refuses to sell below itself — but not on PROTECTION: firing stands the protective stop
/// down over those shares, because at this broker a SELL is bounded by free shares and a stop over the
/// whole holding leaves none. A sell resting above a market that has turned protects nothing. So the
/// early fire is paired with a level at which it is undone: the sell is cancelled (confirmed by the
/// broker), the order goes back to <c>armed</c>, and the stop is re-placed over the whole holding.
/// </para>
///
/// <para>
/// <b>Only a request this system made may re-arm.</b> The row stays <c>fired</c> throughout and
/// <see cref="ArmedOrder.PullbackRequestedUtc"/> marks the request. A persistent order that reaches
/// <c>cancelled</c> WITHOUT that marker was cancelled by a person, and re-arming it would put back an
/// order the operator has just taken away.
/// </para>
/// </summary>
public static class ArmedOrderPullback
{
    /// <summary>
    /// Pull-backs one order may take before it stops firing early. A price hovering between the fire
    /// level and the pull-back level would otherwise cycle stop-off, sell-on, sell-off, stop-on — four of
    /// the account's order numbers a round (CLAUDE.md §6a records what re-using one cost). Past the cap
    /// the order is re-armed AT its limit with no pull-back: exactly how a take-profit behaved before this
    /// existed, so the worst case is never worse than that. A documented constant rather than a setting:
    /// it bounds churn, it never decides whether a trade happens.
    /// </summary>
    public const int MaxPullbacks = 2;

    /// <summary>
    /// The persistent order id a firing creates. Deterministic, as it always was, so a crash between
    /// saving the intent and marking the trigger fired stays visible rather than inventing a second
    /// instruction — but distinct per re-arm, because intent ids are a primary key and a re-armed order
    /// that fired under its old id again would throw at the insert.
    /// </summary>
    public static string IntentIdFor(ArmedOrder order) =>
        order.RearmCount == 0 ? $"armed-{order.ArmedId}" : $"armed-{order.ArmedId}-r{order.RearmCount}";

    /// <summary>Whether this order is watched for a pull-back at all.</summary>
    public static bool IsWatched(ArmedOrder order) =>
        order.State.Equals("fired", StringComparison.OrdinalIgnoreCase)
        && order.PullbackPrice is > 0m;

    /// <param name="intent">The persistent order this firing created, or null when none can be found.</param>
    /// <param name="price">The latest live price, or null when none is known.</param>
    /// <param name="marketIsOpen">The venue is in session.</param>
    /// <param name="sessionEnded">
    /// Today's session is over — the next open belongs to a later trading date. False during a midday
    /// break, when an order placed that morning may still be resting.
    /// </param>
    /// <param name="today">The PKT trading date.</param>
    public static PullbackDecision Decide(
        ArmedOrder order,
        PersistentOrderIntent? intent,
        decimal? price,
        bool marketIsOpen,
        bool sessionEnded,
        DateOnly today)
    {
        if (!IsWatched(order))
            return new(PullbackAction.None, "not a fired order with a pull-back level");

        if (intent is null)
            return new(PullbackAction.Finish,
                "no persistent order can be found for this firing, so there is nothing to pull back");

        if (order.PullbackRequestedUtc is not null)
        {
            return intent.State switch
            {
                "cancelled" or "expired" => new(PullbackAction.Rearm,
                    "the pulled-back sell is confirmed gone"),
                "fulfilled" => new(PullbackAction.Finish,
                    "the sell filled before the pull-back could cancel it"),
                "attention" => new(PullbackAction.Finish,
                    "the pull-back's cancel left an outcome a person has to settle; not re-armed"),
                _ => new(PullbackAction.Wait, $"the cancel has not settled (order {intent.State})")
            };
        }

        if (intent.IsTerminal)
            return new(PullbackAction.Finish, intent.State == "fulfilled"
                ? "the sell filled"
                : $"the sell was {intent.State} by something other than a pull-back; not re-armed");

        if (!marketIsOpen)
        {
            // No broker call is made on this path, so it must be CERTAIN nothing rests: an idle state
            // (the worker writes these only once it has found no order of ours outstanding), AND no
            // placement from a session that is still going. A Friday midday break is the case this
            // excludes — an order placed that morning may well be resting through it.
            var idle = intent.State is "active" or "partial";
            var nothingToday = intent.LastAttemptSessionDate is null
                               || intent.LastAttemptSessionDate < today
                               || sessionEnded;
            return idle && nothingToday
                ? new(PullbackAction.HandBack,
                    "the session is over and the venue has cleared its book; re-armed rather than "
                    + "re-sent at the next open over a price that may have moved")
                : new(PullbackAction.None, "the market is shut");
        }

        if (price is not > 0m)
            return new(PullbackAction.None, "no live price");

        if (price > order.PullbackPrice)
            return new(PullbackAction.None, $"{price} is above the {order.PullbackPrice} pull-back level");

        // A state the persistent worker is already resolving is left to it: a claimed placement, an
        // expiry or a cancel in flight, or an outcome only a person can settle.
        if (intent.State is "placing" or "expiring" or "cancelling" or "attention")
            return new(PullbackAction.None, $"the persistent order is {intent.State}");

        return new(PullbackAction.PullBack,
            $"{order.Symbol} traded at {price}, at or below the {order.PullbackPrice} pull-back level");
    }

    /// <summary>
    /// What a pulled-back order is re-armed as, or null when nothing remains to sell.
    /// </summary>
    /// <param name="bounce">
    /// True for an in-session pull-back, which counts toward <see cref="MaxPullbacks"/>. A hand-back at
    /// the close does not: it is the session ending, not the price bouncing.
    /// </param>
    public static PullbackRearm? Rearm(ArmedOrder order, PersistentOrderIntent intent, bool bounce)
    {
        var remaining = order.Quantity - Math.Max(0, intent.FilledQuantity);
        if (remaining <= 0) return null;

        var pullbacks = order.PullbackCount + (bounce ? 1 : 0);
        var capped = pullbacks >= MaxPullbacks;
        var trigger = capped ? order.Price ?? order.TriggerPrice : order.TriggerPrice;
        var partial = intent.FilledQuantity > 0
            ? $" {intent.FilledQuantity} share(s) sold before it came back; re-armed for the remaining {remaining}."
            : "";

        return new PullbackRearm(
            Quantity: remaining,
            TriggerPrice: trigger,
            PullbackPrice: capped ? null : order.PullbackPrice,
            PullbackCount: pullbacks,
            RearmCount: order.RearmCount + 1,
            Reason: (bounce
                    ? $"Pulled back ({pullbacks} of {MaxPullbacks}): the sell was cancelled and the stop "
                      + "re-placed over the whole holding."
                    : "Handed back at the close: nothing rests overnight, and tomorrow it fires again "
                      + "from its trigger rather than being re-sent at the open.")
                    + partial
                    + (capped
                        ? $" Pull-back limit reached, so it now fires only at its {trigger} limit."
                        : $" Fires again at {trigger}."));
    }
}
