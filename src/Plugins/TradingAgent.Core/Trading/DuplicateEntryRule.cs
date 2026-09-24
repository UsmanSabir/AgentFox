namespace TradingAgent.Trading;

/// <summary>
/// A live standing instruction that already covers what a new order is about to do.
/// </summary>
/// <param name="Describe">Server-authored clause, shown verbatim inside the warning sentence.</param>
public sealed record LiveEntryConflict(
    string IntentId,
    string Symbol,
    string Action,
    int RemainingQuantity,
    string State,
    string? StateReason,
    string Describe);

/// <summary>
/// Whether a new order would duplicate a keep-working instruction the account is still carrying.
///
/// <para>
/// <b>The incident, 2026-09-21 on MWMP.</b> A keep-working BUY of 52 shares was refused for
/// <c>Insufficient Exposure</c> at 11:50:46 and again at 11:52:15, and then — as designed — it waited
/// on <see cref="PersistentOrderDecisions.AutoRetryDelayFor"/>'s 1, 2, 4… minute curve rather than
/// giving up. Exposure was freed by an unrelated cancellation at 11:52:20. At 11:53:35 the operator,
/// reading two failures and no order, placed a replacement for 56 shares; it filled at 11:53:47. The
/// original's second automatic retry went out at 11:54:16 and filled at 11:54:29. The account bought
/// the same name twice, 42 seconds apart, for a position nobody sized. Only one of the two attached
/// stops then covered anything, so half the position was unprotected as well.
/// </para>
///
/// <para>
/// <b>Nothing was broken.</b> The backoff behaved exactly as its own documentation says it should, the
/// refusals were classified correctly, and both fills were legitimate orders. The gap is that a
/// retrying intent is invisible at the one moment it matters — the operator is looking at a failure,
/// and the thing that will act on their behalf in two minutes is on another screen.
/// </para>
///
/// <para>
/// <b>So this warns; it does not refuse.</b> A second entry on a name is a perfectly ordinary thing to
/// want — scaling in, averaging a winner, a different plan on the same stock — and a gate that refused
/// it would be the §0.2 hurdle in its purest form. The endpoint answers 409 with this conflict named,
/// and the same request carrying <c>AcknowledgeDuplicate</c> goes straight through. The point is that
/// a person saw it, not that the system had a view.
/// </para>
///
/// <para>
/// Pure: takes intents and a request, reads no clock, performs no I/O.
/// </para>
/// </summary>
public static class DuplicateEntryRule
{
    /// <summary>
    /// The live keep-working intents that would act on the same symbol and side as this order.
    ///
    /// <para>
    /// <b>Matched on symbol and side only, deliberately.</b> Not on price, and not on quantity: the
    /// operator in the incident above placed 56 shares at 94.81 against a live 52 at 95.53, and any
    /// rule keyed on the numbers would have found no conflict and said nothing. What made those two
    /// orders a duplicate was that both were buying MWMP, which is the only fact the warning needs.
    /// </para>
    ///
    /// <para>
    /// <b>Terminal and attention-held intents are excluded.</b> <c>fulfilled</c>, <c>expired</c> and
    /// <c>cancelled</c> will never place again. <c>attention</c> is the state an intent reaches when a
    /// person must resolve it, and it places nothing until they do — warning about it would be warning
    /// about a queue that is not moving, which teaches the operator to dismiss the warning that
    /// matters.
    /// </para>
    ///
    /// <para>
    /// An intent with nothing left to fill is excluded too: its remaining quantity is zero, so it can
    /// take no more of the position however its state happens to read.
    /// </para>
    /// </summary>
    public static IReadOnlyList<LiveEntryConflict> FindLiveIntents(
        IEnumerable<PersistentOrderIntent> intents, string symbol, string action)
    {
        symbol = (symbol ?? "").Trim();
        action = (action ?? "").Trim();

        return intents
            .Where(i => !i.IsTerminal
                     && !string.Equals(i.State, "attention", StringComparison.OrdinalIgnoreCase)
                     && i.RemainingQuantity > 0
                     && i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                     && i.Action.Equals(action, StringComparison.OrdinalIgnoreCase))
            .Select(i => new LiveEntryConflict(
                i.IntentId,
                i.Symbol,
                i.Action,
                i.RemainingQuantity,
                i.State,
                i.StateReason,
                Describe(i)))
            .ToList();
    }

    private static string Describe(PersistentOrderIntent intent)
    {
        var price = intent.Price is { } p ? $" at {p:N2}" : "";
        var progress = intent.FilledQuantity > 0
            ? $" ({intent.FilledQuantity:N0} of {intent.Quantity:N0} already filled)"
            : "";

        // The state reason is where the backoff writes what the broker last said and when it will try
        // again, which is the single most useful sentence here — it is the answer to "is this actually
        // going to do anything, or is it stuck?".
        var why = string.IsNullOrWhiteSpace(intent.StateReason) ? "" : $" — {intent.StateReason.Trim()}";

        return $"a keep-working {intent.Action} of {intent.RemainingQuantity:N0} "
             + $"{intent.Symbol}{price}{progress} is still live and will keep placing on its own "
             + $"[{intent.State}]{why}";
    }

    /// <summary>
    /// The warning sentence. It says what is live, that it acts unattended, and what the two ways
    /// forward are — because the operator in the incident had no idea there was a decision to make.
    /// </summary>
    public static string Compose(
        string symbol, string action, IReadOnlyList<LiveEntryConflict> conflicts)
    {
        var body = string.Join("; ", conflicts.Select(c => c.Describe));
        var total = conflicts.Sum(c => c.RemainingQuantity);

        return $"This {action} of {symbol} would be a SECOND entry: {body}. Placing this now can leave "
             + $"the account holding both — up to {total:N0} more share(s) than this order asks for, "
             + "bought whenever that instruction next retries, which it does on its own. Cancel the "
             + "keep-working order first if this is meant to replace it, or confirm to place both.";
    }
}
