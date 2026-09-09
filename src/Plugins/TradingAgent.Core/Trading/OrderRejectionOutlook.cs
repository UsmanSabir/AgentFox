namespace TradingAgent.Trading;

/// <summary>
/// Whether re-sending an order the broker refused could ever work, and if so when.
/// </summary>
public enum RejectionOutlook
{
    /// <summary>
    /// The condition can clear during today's session, so back off and try again. The default for
    /// anything not positively recognised, because a wording nobody has measured must get the
    /// treatment that cannot lose a fill.
    /// </summary>
    RetryToday,

    /// <summary>
    /// Fixed for the rest of the session but not permanent. Stop attempting today; the intent's own
    /// next-trading-date eligibility carries it forward with no new durable state.
    /// </summary>
    RetryTomorrow,

    /// <summary>
    /// Cannot clear by waiting at all. The intent goes to <c>attention</c> so a person decides, rather
    /// than a timer re-asking a question the broker has answered.
    /// </summary>
    Never
}

/// <summary>
/// Reads a broker's own refusal text and says whether waiting can help.
///
/// <para>
/// <b>The backoff alone was not enough, and this is the missing half.</b>
/// <see cref="PersistentOrderDecisions.AutoRetryDelayFor"/> stopped a refusal being re-sent every 60
/// seconds, but it is blind to WHY: a duplicate the exchange already holds, and a capability this
/// account does not have, were still being re-asked ~15 times a day for ever. Equally, a price outside
/// the day's ±10% band was being retried all session for a price the venue cannot accept until the
/// next close moves the band.
/// </para>
///
/// <para>
/// <b>Every entry below is a wording this integration has MEASURED</b> — see CLAUDE.md §6a in the
/// premium repo for the captures. Nothing is here on the strength of reading plausibly, because the
/// standing lesson from this broker is that guessed-at wordings are how confident, documented, wrong
/// behaviour gets shipped.
/// </para>
///
/// <para>
/// <b>The default is the whole safety argument.</b> An unrecognised refusal is
/// <see cref="RejectionOutlook.RetryToday"/>, so a new or misspelled wording costs the old backoff
/// behaviour rather than a wrongly abandoned order. Widening this list is a deliberate act taken from
/// an observed refusal, never from guesswork — the same asymmetry <c>AhlMarketStateReader</c>'s
/// vocabulary has, for the same reason.
/// </para>
///
/// <para>
/// Pure: takes a string, reads no clock, performs no I/O.
/// </para>
/// </summary>
public static class OrderRejectionOutlook
{
    /// <summary>
    /// Classifies a refusal.
    ///
    /// <para>
    /// <b>Transient wins outright.</b> A refusal already classified transient by the adapter — a shut
    /// board, or the broker serialising placements — says nothing about this order and must stay
    /// retryable whatever its text happens to contain.
    /// </para>
    /// </summary>
    /// <param name="reason">The broker's message, however it was wrapped on the way here.</param>
    /// <param name="transient">
    /// The adapter's own transient flag (<c>OrderResult.TransientRejection</c>), when the caller has
    /// it. A transient refusal is never anything but <see cref="RejectionOutlook.RetryToday"/>.
    /// </param>
    public static RejectionOutlook Classify(string? reason, bool transient = false)
    {
        if (transient) return RejectionOutlook.RetryToday;

        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text)) return RejectionOutlook.RetryToday;

        // ── Never ────────────────────────────────────────────────────────────
        // A duplicate is already AT the exchange. Re-sending it is asking to hold the order twice,
        // which is the one outcome worse than not holding it at all.
        if (Has(text, "Duplicated_Order") || Has(text, "Duplicate Order"))
            return RejectionOutlook.Never;

        // Account capability, not a market condition. CONFIRMED twice on this account:
        // "OrderSendToMarketFail|Order[N]: MIT Order is not allowed".
        //
        // Matched as "Order is not allowed" rather than a bare "not allowed", deliberately. The bare
        // form would also catch a SESSION-level refusal ("trading is not allowed at this time"), which
        // clears on its own and must keep retrying. The narrower token still covers the same refusal
        // for another order type, and anything it misses falls through to the safe default.
        if (Has(text, "Order is not allowed"))
            return RejectionOutlook.Never;

        // Our own request was malformed. Waiting cannot make it well-formed.
        if (Has(text, "Invalid Order Type") || Has(text, "does not recognise order type"))
            return RejectionOutlook.Never;

        // ── Tomorrow ─────────────────────────────────────────────────────────
        // PSX locks each day's price to within 10% of the previous close and refuses an order beyond
        // that outright rather than letting it rest. The band is a property of the DAY, so the same
        // price is refused for the rest of the session and becomes placeable only once a later close
        // moves the band.
        //
        // The account owner has seen a handful of names apparently re-banded mid-session and is not
        // confident in it (2026-09-09). Deliberate decision, theirs: assume no intraday re-band. The
        // cost of being wrong is bounded and visible — the intent says why it is waiting and Retry is
        // one click — whereas assuming the opposite means retrying a refusal all session for a price
        // the venue has said it will not take. Revisit from a capture, not from a recollection.
        if (Has(text, "Over_Price_Limit"))
            return RejectionOutlook.RetryTomorrow;

        return RejectionOutlook.RetryToday;
    }

    /// <summary>
    /// The operator-facing sentence for an outlook that stops the automatic retry, or null for
    /// <see cref="RejectionOutlook.RetryToday"/>, which the backoff already explains.
    ///
    /// <para>
    /// It says what the broker refused, why waiting cannot fix it, and what to do — including that
    /// Retry is still available, because this is a judgement about an automatic loop and never a
    /// restriction on the operator.
    /// </para>
    /// </summary>
    public static string? Explain(RejectionOutlook outlook, string? reason) => outlook switch
    {
        RejectionOutlook.Never =>
            $"The broker refused this order and the refusal cannot clear by waiting: {Clip(reason)} "
            + "Automatic retries have stopped so this is not re-sent all day. Change the order, or "
            + "press Retry if you believe the refusal no longer applies.",

        RejectionOutlook.RetryTomorrow =>
            $"The exchange refused this order's PRICE for today: {Clip(reason)} PSX locks each day's "
            + "price to within 10% of the previous close, so the same price is refused for the rest of "
            + "the session. No further attempt will be made today; the next one is eligible on the "
            + "next trading date, when a later close may have moved the band. Re-price it, or press "
            + "Retry to send it again as it stands.",

        _ => null
    };

    /// <summary>A short label for a log line or an activity row.</summary>
    public static string Label(RejectionOutlook outlook) => outlook switch
    {
        RejectionOutlook.Never => "retries stopped — the refusal cannot clear by waiting",
        RejectionOutlook.RetryTomorrow => "no further attempt today — the price is outside today's band",
        _ => "retrying later today"
    };

    private static bool Has(string text, string token) =>
        text.Contains(token, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The broker's words, bounded and terminated. This string is written onto the intent, which the
    /// dashboard renders, and a refusal can carry a whole wire frame.
    /// </summary>
    private static string Clip(string? reason)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text)) return "(no reason was recorded).";
        text = text.Length <= 240 ? text : text[..240] + "…";
        return text.EndsWith('.') || text.EndsWith('…') ? text : text + ".";
    }
}
