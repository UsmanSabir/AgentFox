using TradingAgent.Reconciliation;

namespace TradingAgent.Risk;

/// <summary>
/// What the broker will fund a BUY with, read off an account snapshot.
/// </summary>
/// <param name="Known">
/// False when the snapshot is unusable or carries no buying-power row at all. Unknown is never zero
/// (invariant 4), and here that means unknown never refuses — see <see cref="BuyAffordabilityRule.MayRefuse"/>.
/// </param>
/// <param name="BuyingPowerPkr">
/// <see cref="BrokerReconciliationSnapshot.AvailableCashPkr"/>: what the venue will fund, ALREADY net
/// of the orders already resting. Never subtract open orders from it a second time — the broker counts
/// commitments this client cannot price, and a local tally silently cannot.
/// </param>
/// <param name="Reason">The sentence a refusal quotes as its arithmetic.</param>
public readonly record struct BuyAffordability(bool Known, decimal BuyingPowerPkr, string Reason);

/// <summary>
/// The pure half of the dashboard's pre-flight BUY refusal: when "you cannot fund this" is allowed to
/// become a refusal, and what that refusal may say.
///
/// <para>
/// <b>Why this exists, measured 2026-09-21 on MWMP.</b> A BUY of 52 shares at 95.53 (PKR 4,967.56) went
/// to the broker against PKR 760 of buying power and was refused pre-trade with
/// <c>Insufficient Exposure ( Amount Remaining = 760.00 )</c>. Nothing in this repository had looked:
/// the SELL side has had <see cref="SellAvailabilityConfirmer"/> since 2026-09-07, and the BUY side had
/// no equivalent at any point in the path — <c>available_cash</c> was read by a reporting tool and by
/// nothing that submits an order. The order dialog's own warning is client-side, and only appears once
/// the operator has pressed "Check buying power".
/// </para>
///
/// <para>
/// <b>The cost was not the refused order.</b> It was a keep-working intent, so it backed off and
/// retried on <see cref="Trading.PersistentOrderDecisions.AutoRetryDelayFor"/>'s curve while the
/// operator — believing it had failed — placed a replacement. Exposure was freed by an unrelated
/// cancellation at 11:52:20, and then BOTH filled: 56 shares at 11:53:47 and 52 at 11:54:29, for a
/// position twice the size anyone intended. See <see cref="Trading.DuplicateEntryRule"/> for the other
/// half of that incident.
/// </para>
///
/// <para>
/// <b>It refuses on traded value alone, and must never learn a fee schedule.</b> Core models no broker
/// charges at all and inventing a headroom percentage here would be a fee model by the back door that
/// is wrong for the next broker (premium CLAUDE.md invariant 10a). The venue funds value PLUS its own
/// charges, so an order that only just fits this figure can still be refused — that near-limit case
/// stays a client-side caution beside the cost estimate, which reads the edition's real cost
/// projection. This rule fires only on a clear over-spend, where no charge schedule could change the
/// answer.
/// </para>
///
/// <para>
/// <b>And a cached figure is only ever a reason to LOOK</b>, never a reason to refuse — the whole
/// lesson of the 2026-09-07 FCEPL incident, which <see cref="SellRefusalRule"/> records at length. A
/// snapshot can sit well inside its age tolerance and still describe the account as it was a poll
/// interval ago, which on the deployed configuration is 21 minutes.
/// </para>
///
/// <para>Pure: takes a snapshot and a value, reads no clock beyond the one handed to it, performs no I/O.</para>
/// </summary>
public static class BuyAffordabilityRule
{
    /// <summary>
    /// What the account can fund, or why that is unknown.
    /// </summary>
    /// <param name="maxAge">
    /// How old the snapshot may be before it is treated as no reading at all. The same tolerance
    /// <see cref="SellQuantityRule.Available"/> applies, and it catches a DEAD reconciliation worker
    /// rather than stale content — which is exactly why <see cref="MayRefuse"/> may only be asked of a
    /// confirming read.
    /// </param>
    public static BuyAffordability Available(
        BrokerReconciliationSnapshot snapshot, DateTime nowUtc, TimeSpan maxAge)
    {
        if (!snapshot.Supported || !snapshot.Healthy || nowUtc - snapshot.CheckedUtc > maxAge)
            return new(false, 0m,
                "Buying power is unavailable because broker reconciliation is not healthy and fresh: "
                + snapshot.Reason);

        // No row rather than a zero row. A broker that does not report buying power must not be read as
        // reporting none of it, which would refuse every BUY on the account.
        if (snapshot.AvailableCashPkr is not { } buyingPower)
            return new(false, 0m,
                "This broker's account read carries no buying-power figure, so what it will fund "
                + "cannot be checked here.");

        return new(true, buyingPower,
            $"The broker reports {buyingPower:N2} PKR of buying power, already net of the orders "
            + "resting against this account.");
    }

    /// <summary>
    /// Is this cached answer worth spending a broker read on? Only when it would REFUSE the order.
    ///
    /// <para>
    /// A cached figure that already covers the order needs no read, and that asymmetry is what makes
    /// the check affordable: the read happens only on a path otherwise about to fail. It does leave one
    /// direction uncovered — a cached figure too HIGH, because cash was committed elsewhere since it was
    /// taken — and that is deliberate. The outcome there is the broker's own pre-trade refusal, exactly
    /// what happens today, so nothing is made worse; spending a read on every BUY to catch it would cost
    /// five SOAP calls on an account that allows one session, for a case the venue already handles.
    /// </para>
    /// </summary>
    public static bool NeedsBrokerConfirmation(BuyAffordability cached, decimal orderValuePkr) =>
        orderValuePkr > 0m && cached is { Known: true } && cached.BuyingPowerPkr < orderValuePkr;

    /// <summary>
    /// May a refusal be issued on this answer? Only when buying power is KNOWN and genuinely short,
    /// which after a confirming read means the broker itself will not fund this order.
    ///
    /// <para>
    /// Deliberately the same shape as <see cref="NeedsBrokerConfirmation"/> rather than shared with it:
    /// they are asked at different moments of different data, and a later tolerance added to one must
    /// not silently move the other. Everything else — unknown included, and an order whose value cannot
    /// be computed at all — allows the order through, because refusing on ignorance would newly block
    /// ordinary buying whenever the broker cannot be read (§0.2: a gate that refuses trades has to earn
    /// it, and this one has earned only the clear case).
    /// </para>
    /// </summary>
    public static bool MayRefuse(BuyAffordability confirmed, decimal orderValuePkr) =>
        orderValuePkr > 0m && confirmed is { Known: true } && confirmed.BuyingPowerPkr < orderValuePkr;

    /// <summary>
    /// What a MARKET order is worth, which is nothing this can know.
    ///
    /// <para>
    /// A market buy has no price until it fills, so there is no value to compare and the check is
    /// SKIPPED rather than guessed at — the same choice <see cref="Watchlist.AttachedStopRule"/> makes
    /// about a stop level on a market entry, and for the same reason: refusing on a number nobody has
    /// would block the order type least able to supply one.
    /// </para>
    /// </summary>
    public static decimal? OrderValue(decimal? entryPrice, int quantity) =>
        entryPrice is > 0m && quantity > 0 ? entryPrice.Value * quantity : null;

    /// <summary>
    /// The refusal sentence. States the arithmetic, that the arithmetic came from a read taken just
    /// now, and that the figure excludes the broker's charges — because an operator who trims the order
    /// to exactly the reported buying power meets the same refusal again, and the screen should say so
    /// before they try it.
    /// </summary>
    public static string Compose(
        string symbol, int quantity, decimal orderValuePkr, BuyAffordability confirmed) =>
        $"The broker will not fund this BUY of {quantity:N0} {symbol}. It is worth "
        + $"{orderValuePkr:N2} PKR and {confirmed.BuyingPowerPkr:N2} PKR is available — short by "
        + $"{orderValuePkr - confirmed.BuyingPowerPkr:N2} PKR. {confirmed.Reason} Checked with the "
        + "broker just now, not from a cached snapshot. Note that buying power funds the traded value "
        + "AND the broker's charges, so an order sized to exactly the figure above is still refused; "
        + "leave room for the cost shown beside the order.";
}
