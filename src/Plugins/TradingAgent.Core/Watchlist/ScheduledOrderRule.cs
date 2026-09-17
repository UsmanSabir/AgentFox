using TradingAgent.Market;

namespace TradingAgent.Watchlist;

/// <summary>
/// The outcome of resolving an armed order's two time bounds. Either both instants are usable, or
/// <see cref="ErrorCode"/> says which rule refused and <see cref="Message"/> says it in the
/// operator's words.
/// </summary>
public readonly record struct ScheduledOrderWindow(
    DateTime? ActiveFromUtc,
    DateTime ExpiresUtc,
    string? ErrorCode = null,
    string? Message = null)
{
    public bool Ok => ErrorCode is null;
}

/// <summary>
/// Turns "on the 22nd, expiring in 30 days" into the two UTC instants the lifecycle runs on.
///
/// <para>
/// Pure, and separate from the endpoint, for the reason every other rule in this repo is: the endpoint
/// needs a broker, a universe, a quote source and a database to reach, and none of them has anything to
/// do with which day an order becomes active. This is the part that is easy to get subtly wrong —
/// timezone, ordering, the interaction with a defaulted expiry — so it is the part that gets tabled
/// tests instead of a live market.
/// </para>
/// </summary>
public static class ScheduledOrderRule
{
    /// <summary>Widest expiry the caller may ask for, in days. Matches the arm endpoint's clamp.</summary>
    public const int MaxExpiryDays = 365;

    /// <summary>Expiry applied when the caller names none.</summary>
    public const int DefaultExpiryDays = 30;

    /// <summary>
    /// Resolves the window.
    /// </summary>
    /// <param name="activeFromDate">
    /// The PKT calendar date the order becomes active on, or null for "active immediately".
    /// </param>
    /// <param name="expiresUtc">An explicit expiry, taken exactly as given.</param>
    /// <param name="expiresInDays">How long to keep trying, when no explicit expiry is sent.</param>
    /// <param name="todayPkt">Today's date at the exchange. Passed in so this stays pure.</param>
    /// <param name="nowUtc">The clock, for the same reason.</param>
    public static ScheduledOrderWindow Resolve(
        DateOnly? activeFromDate,
        DateTime? expiresUtc,
        int? expiresInDays,
        DateOnly todayPkt,
        DateTime nowUtc)
    {
        DateTime? activeFromUtc = null;

        if (activeFromDate is { } date)
        {
            // Compared in PKT against a PKT date. Comparing it to UTC "today" would refuse a date all
            // evening in Pakistan, where the UTC day has not rolled over yet — the operator's calendar
            // and the exchange's are the same one, and neither is UTC.
            if (date < todayPkt)
                return Refuse(
                    "active_from_in_past",
                    $"{date:yyyy-MM-dd} has already passed in Pakistan (today is "
                    + $"{todayPkt:yyyy-MM-dd}). An order cannot be scheduled backwards.");

            // Midnight PKT, not the session open: the PSX session window differs Monday-Thursday from
            // Friday, so projecting a FUTURE date's open is a second calendar calculation with its own
            // way to be wrong — and it would buy nothing, because ArmedOrderEvaluator holds a scheduled
            // order closed until the venue opens anyway.
            activeFromUtc = TimeZoneInfo.ConvertTimeToUtc(
                date.ToDateTime(TimeOnly.MinValue), PsxTime.Zone);
        }

        // The default expiry is measured from ACTIVATION, not from now. "Expires in 30 days" is the
        // operator saying how long to keep TRYING; measured from today it would be spent waiting, and
        // anything scheduled more than that far out would expire before it ever became active — a
        // whole class of orders that could never fire, refused by a rule the operator never set.
        var resolvedExpiry = expiresUtc
                             ?? (activeFromUtc ?? nowUtc)
                                .AddDays(Math.Clamp(expiresInDays ?? DefaultExpiryDays, 1, MaxExpiryDays));

        if (activeFromUtc is { } becomesActive && resolvedExpiry <= becomesActive)
            return Refuse(
                "expires_before_active",
                $"This order would expire at {resolvedExpiry:u}, before it became active at "
                + $"{becomesActive:u} — so it could never fire. Move the expiry past the activation "
                + "date.");

        return new ScheduledOrderWindow(activeFromUtc, resolvedExpiry);

        static ScheduledOrderWindow Refuse(string code, string message) =>
            new(null, default, code, message);
    }

    /// <summary>
    /// Why a <see cref="ArmedTriggerKind.Scheduled"/> request cannot be accepted, or null when it can.
    ///
    /// <para>
    /// A level or an alert kind is REFUSED rather than dropped. Dropping would store an order the
    /// operator believes is conditional and the evaluator treats as unconditional — the one
    /// disagreement in this feature that ends with shares being bought that nobody asked for. Only the
    /// caller knows which of the two they meant.
    /// </para>
    /// </summary>
    public static (string Code, string Message)? ValidateScheduledRequest(
        DateOnly? activeFromDate,
        decimal? triggerPrice,
        decimal? triggerPercent,
        string? triggerAlertKind)
    {
        if (triggerPrice is not null || triggerPercent is not null
            || !string.IsNullOrWhiteSpace(triggerAlertKind))
            return ("scheduled_trigger_takes_no_condition",
                "A Scheduled order fires on its date alone. To make it conditional as well, arm a "
                + "PriceBelow/PriceAbove trigger and send activeFromDate with it.");

        if (activeFromDate is null)
            return ("scheduled_needs_a_date",
                "A Scheduled order needs activeFromDate — the date is what fires it.");

        return null;
    }
}
