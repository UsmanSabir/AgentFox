namespace TradingAgent.Market;

/// <summary>
/// Shared PSX wall clock. The exchange trades on Pakistan Standard Time, so anything that needs
/// "which trading day is it" — the market calendar, the candle loader, the watchlist scanner —
/// must agree on one timezone. Falls back to a fixed UTC+5 zone when the host has no tz database
/// entry (PKT has no DST, so the fallback is exact rather than approximate).
/// </summary>
public static class PsxTime
{
    public static TimeZoneInfo Zone { get; } = Resolve();

    /// <summary>Local exchange time for a UTC instant (defaults to now).</summary>
    public static DateTime Now(DateTime? utcNow = null) =>
        TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utcNow ?? DateTime.UtcNow, DateTimeKind.Utc), Zone);

    /// <summary>The exchange's current calendar date — the day a live quote belongs to.</summary>
    public static DateOnly Today(DateTime? utcNow = null) => DateOnly.FromDateTime(Now(utcNow));

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Pakistan Standard Time", "Asia/Karachi" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("PKT", TimeSpan.FromHours(5), "PKT", "PKT");
    }
}
