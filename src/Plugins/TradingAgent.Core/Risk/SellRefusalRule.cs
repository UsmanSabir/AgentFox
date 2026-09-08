using TradingAgent.Reconciliation;

namespace TradingAgent.Risk;

/// <summary>
/// The pure half of the dashboard's pre-flight SELL refusal: when a "no sellable holding" answer is
/// allowed to become a refusal, and what that refusal is permitted to say.
///
/// <para>
/// This exists because the refusal is a DIAGNOSTIC — its whole purpose is to replace the broker's own
/// bare rejection with a reason and a remedy — and a diagnostic must never become the reason a
/// legitimate order is refused. It was exactly that until 2026-09-07: the endpoint judged on
/// <see cref="TradingReconciliationState"/>, a snapshot a background timer refreshes every
/// <c>ReconciliationIntervalSeconds</c>, and the age test inside <see cref="SellQuantityRule.Available"/>
/// only catches a DEAD worker. A snapshot can sit well inside <c>ReconciliationMaxAgeSeconds</c> and
/// still describe the account as it was a whole poll interval ago, so a cancellation made anywhere else
/// — the broker's own mobile app, the desktop client, a phone call — was invisible to it.
/// </para>
///
/// <para>
/// CONFIRMED live 2026-09-07 on FCEPL. A resting SELL over the whole 50-share holding was cancelled from
/// the broker's mobile app; the order dialog's own panel, which reads the account LIVE, showed 50 owned,
/// 0 in working SELLs and 50 available; and this check answered zero from the pre-cancellation copy and
/// returned a 409. What made it a defect rather than a stale display is that the AUTHORITATIVE check
/// disagreed and never got to run — <c>TradingManager.ExecuteGroupsAsync</c> re-reads the broker book
/// live for every independent SELL and would have sized that order correctly. A 409 short-circuits it.
/// </para>
///
/// <para>
/// So a cached zero is now only a REASON TO LOOK (<see cref="NeedsBrokerConfirmation"/>), never a reason
/// to refuse (<see cref="MayRefuse"/>). The confirming read is affordable precisely because it happens
/// on a path that is otherwise about to fail, which is the same trade the order gate makes when it
/// prefers a freshly polled market state to a cached one at the moment an order is going out.
/// </para>
/// </summary>
public static class SellRefusalRule
{
    /// <summary>
    /// Is this cached answer worth spending a broker read on? Only when it would SHORT-CHANGE a sell of
    /// <paramref name="requestedQuantity"/> — refuse it outright, or quietly size it down.
    ///
    /// <para>
    /// A cached figure that already covers the request needs no read: staleness can only make it
    /// understate what is free (a cancellation or a fill FREES shares; a commitment this system did not
    /// make is the only way it moves the other way, and the execution boundary re-reads before
    /// submitting regardless). An UNKNOWN answer has nothing to confirm and never refuses anyway.
    /// </para>
    ///
    /// <para>
    /// The shortfall form rather than the zero form is deliberate, and 2026-09-07 is why. The zero case
    /// is only the loudest symptom of a stale snapshot — the quiet one is a clamp. Three callers reduce
    /// a SELL to this figure without refusing anything: the dashboard's keep-working branch, the same
    /// branch for a triggered armed order, and the decision to stand our own protective stop down. On
    /// the deployed interval a clamp can be sized on an account picture up to a poll interval old, and
    /// unlike a refusal it produces no error for anyone to notice — just a smaller sell than was asked
    /// for, explained by a <see cref="SellQuantityAdjustment"/> message nobody reads.
    /// </para>
    /// </summary>
    public static bool NeedsBrokerConfirmation(
        SellAvailabilityDecision cached, int requestedQuantity) =>
        cached is { Known: true } && cached.AvailableQuantity < Math.Max(1, requestedQuantity);

    /// <summary>
    /// May a refusal be issued on this answer? Only when it is both KNOWN and zero, which after a
    /// confirming read means the broker itself currently has nothing free.
    ///
    /// <para>
    /// Deliberately the same shape as <see cref="NeedsBrokerConfirmation"/> rather than shared with it:
    /// they are asked at different times, of different data, and a future change to one of them
    /// (a floor, a tolerance, a partial answer) must not silently move the other. The asymmetry that
    /// matters is that everything else — unknown included — ALLOWS the order through to the live check,
    /// because refusing on ignorance would newly block ordinary sells whenever the broker book cannot be
    /// read (invariant 4 running in the direction that eases the trader rather than gating them).
    /// </para>
    /// </summary>
    public static bool MayRefuse(SellAvailabilityDecision confirmed) =>
        confirmed is { Known: true, AvailableQuantity: <= 0 };

    /// <summary>
    /// The SELL orders resting at the broker for <paramref name="symbol"/> that this system did not
    /// place, newest reading in, with anything named by <paramref name="ourOrderNumbers"/> removed.
    ///
    /// <para>
    /// The point of the split is that the two halves get different remedies: a stop this system placed
    /// can be stood down for one sell, and an order placed elsewhere can only be cancelled where it was
    /// placed. Before this existed, an order placed elsewhere produced an empty blocking list and
    /// therefore a refusal that named nothing at all — a dead end on a screen that was simultaneously
    /// showing the shares as available.
    /// </para>
    ///
    /// <para>
    /// Side matching goes through <see cref="SellQuantityRule.IsSell"/> on purpose. This list has to name
    /// the very orders whose quantity produced the commitment; a second, privately-spelled vocabulary
    /// here would let the rule count a commitment the message could not explain.
    /// </para>
    /// </summary>
    public static IReadOnlyList<BrokerWorkingOrder> RestingSellsNotPlacedHere(
        BrokerReconciliationSnapshot snapshot,
        string symbol,
        IReadOnlySet<string> ourOrderNumbers)
    {
        symbol = (symbol ?? "").Trim();
        return snapshot.OpenOrders
            .Where(o => o.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                     && SellQuantityRule.IsSell(o.Side)
                     && o.RemainingQuantity is > 0
                     && !ourOrderNumbers.Contains((o.OrderNo ?? "").Trim()))
            .ToList();
    }

    /// <summary>
    /// The refusal sentence. Always states the arithmetic it refused on and that the arithmetic came
    /// from a read taken just now, because "but I just refreshed the panel" is the first thing the
    /// operator will think and the screen should answer it rather than a support conversation.
    /// </summary>
    /// <param name="basis">
    /// <see cref="SellAvailabilityDecision.Reason"/> from the CONFIRMING read — "50 held minus 50 already
    /// committed to outstanding SELL orders." Never the cached one, or the sentence documents the wrong
    /// snapshot.
    /// </param>
    /// <param name="ourStopClauses">Server-authored clauses naming stops this system placed.</param>
    /// <param name="foreignOrderClauses">Server-authored clauses naming orders placed elsewhere.</param>
    public static string Compose(
        string symbol,
        string basis,
        IReadOnlyList<string> ourStopClauses,
        IReadOnlyList<string> foreignOrderClauses)
    {
        var opening = $"No uncommitted {symbol} shares are available to sell";

        // Our own stops first and alone when present: they are the only case with a remedy on this
        // screen, and offering it is worth more than a complete inventory of what else is resting.
        var body = ourStopClauses.Count > 0
            ? $": {string.Join(" and ", ourStopClauses)}. It can stand down for this sell and go back "
              + "over what remains afterwards."
            : foreignOrderClauses.Count > 0
                ? $": {string.Join(" and ", foreignOrderClauses)}. "
                  + "None of them were placed here, so they cannot be stood down from this screen — "
                  + "cancel the one you want the shares back from wherever it was placed."
                : ".";

        return $"{opening}{body} {basis} Checked with the broker just now, not from a cached snapshot.";
    }
}
