namespace TradingAgent.Risk;

/// <summary>What a triggered armed SELL should do about the availability it was just given.</summary>
public enum ArmedSellDisposition
{
    /// <summary>Proceed. There are shares free to sell.</summary>
    Submit,

    /// <summary>
    /// Stay armed. The shares exist but are committed to something resting, which clears when that is
    /// superseded or at the close when the venue clears its book.
    /// </summary>
    StayArmed,

    /// <summary>
    /// Retire the order. The account holds none of this symbol, so no amount of waiting produces a
    /// fill and every retry is a round trip to the broker asking to sell nothing.
    /// </summary>
    Retire
}

/// <param name="Disposition">What to do.</param>
/// <param name="Reason">
/// The sentence written to the armed order's state and the activity log. Always states the arithmetic,
/// because the whole failure this type exists to end was one message that could not distinguish two
/// situations.
/// </param>
public readonly record struct ArmedSellVerdict(ArmedSellDisposition Disposition, string Reason);

/// <summary>
/// Whether a triggered armed SELL that cannot be filled right now should wait or be retired.
///
/// <para>
/// <b>The incident, MEASURED 2026-09-11 on CNERGY.</b> Two passes 22 seconds apart each armed a trailing
/// SELL for the whole 2471-share position on the same invalidated thesis. One fired at 09:41:50 and sold
/// it. The other then triggered and was refused 29 times between 09:41:50 and 10:34:33, on the identical
/// sentence — <c>No uncommitted CNERGY shares are available to sell</c> — and stopped only because a
/// human disarmed it. Each cycle cost a confirming broker read, a protective-stop release attempt and a
/// second read at the execution boundary: roughly 320 SOAP calls over the account's one session, to sell
/// shares that no longer existed. The duplicate itself is fixed upstream in the premium exit evaluator;
/// this is the half that stops ANY stale exit — however it arose — from behaving that way.
/// </para>
///
/// <para>
/// <b>Retirement is allowed only on a BROKER-CONFIRMED zero holding, and that is the whole safety
/// argument.</b> A cached figure can describe the account as it was a poll interval ago, and retiring a
/// live exit on one would destroy a thesis at the moment it was proved right — the exact failure
/// <see cref="SellRefusalRule"/> was written for, inverted. Every other answer, unknown included, leaves
/// the order armed: this type may only ever END an order that provably cannot fill, never postpone or
/// refuse one that can.
/// </para>
///
/// <para>
/// <b>Why custody rather than availability.</b> Zero AVAILABLE has two meanings the arithmetic folds
/// together — "you own them and they are committed", which clears when the commitment goes, and "you own
/// none", which never clears. <see cref="SellAvailabilityDecision.HeldQuantity"/> exists to keep them
/// apart. A partial holding is NOT retired: it is sized down at the execution boundary, and selling some
/// of an exit is right where selling none of it is not.
/// </para>
/// </summary>
public static class ArmedSellOutlook
{
    /// <param name="confirmed">
    /// The availability answer. <paramref name="brokerConfirmed"/> must be the flag from the read that
    /// produced it, not a hopeful default.
    /// </param>
    /// <param name="brokerConfirmed">
    /// A read was taken FOR this question. <see cref="ArmedSellDisposition.Retire"/> is unreachable
    /// without it.
    /// </param>
    /// <param name="symbol">Named in the reason, because the activity log is read across symbols.</param>
    public static ArmedSellVerdict For(
        SellAvailabilityDecision confirmed, bool brokerConfirmed, string symbol)
    {
        symbol = (symbol ?? "").Trim();

        // Unknown never decides anything. Invariant 4 in the direction that eases the trader: an
        // unreadable book is a fact about the broker, not about this order.
        if (!confirmed.Known)
            return new(ArmedSellDisposition.StayArmed,
                $"Trigger met, but SELL availability was unknown: {confirmed.Reason}");

        if (confirmed.AvailableQuantity > 0)
            return new(ArmedSellDisposition.Submit, confirmed.Reason);

        // Zero available AND a confirmed zero holding: the position this order exists to close is gone.
        // Note the ordering — the holding test is inside the zero-available branch, so a position that
        // is merely fully committed can never reach it.
        if (brokerConfirmed && confirmed.HeldQuantity == 0)
            return new(ArmedSellDisposition.Retire,
                $"Retired without selling: the account holds no {symbol} shares, so this order cannot "
                + "fill however long it waits. The position it was armed against was closed by "
                + "something else — another exit, a protective stop, or a sale made elsewhere. "
                + $"Confirmed with the broker just now: {confirmed.Reason}");

        return new(ArmedSellDisposition.StayArmed,
            $"Trigger met, but every {symbol} share is committed to a resting SELL — {confirmed.Reason} "
            + "Still armed; it retries while the trigger holds.");
    }
}
