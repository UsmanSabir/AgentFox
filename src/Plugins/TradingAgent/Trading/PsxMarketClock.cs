namespace TradingAgent.Trading;

/// <summary>
/// Single source of truth for PSX trading hours: Monday–Friday, 09:15–15:30 Pakistan Standard Time
/// (UTC+5, no DST). Used by both the check_market tool and the take-profit retry worker so they never
/// disagree about whether the market is open.
/// </summary>
public static class PsxMarketClock
{
    private static readonly TimeZoneInfo Pkt = ResolvePkt();

    private static readonly TimeOnly OpenTime  = new(9, 15);
    private static readonly TimeOnly CloseTime = new(15, 30);

    public readonly record struct MarketStatus(bool IsOpen, DateTime PktNow, string Reason);

    /// <summary>Current PSX market status (time in PKT, open/closed, and a human reason).</summary>
    public static MarketStatus Now()
    {
        var pktNow    = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pkt);
        var isWeekday = pktNow.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;
        var tod       = TimeOnly.FromDateTime(pktNow);
        var inHours   = tod >= OpenTime && tod <= CloseTime;
        var isOpen    = isWeekday && inHours;

        var reason = isOpen
            ? $"PSX is open. Current time: {tod:HH:mm} PKT."
            : !isWeekday
                ? $"PSX is closed on {pktNow.DayOfWeek}s."
                : tod < OpenTime
                    ? $"Pre-market. Opens at 09:15 PKT. Current: {tod:HH:mm} PKT."
                    : $"After-hours. Closed at 15:30 PKT. Current: {tod:HH:mm} PKT.";

        return new MarketStatus(isOpen, pktNow, reason);
    }

    public static bool IsOpen() => Now().IsOpen;

    private static TimeZoneInfo ResolvePkt()
    {
        foreach (var id in new[] { "Pakistan Standard Time", "Asia/Karachi" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("PKT", TimeSpan.FromHours(5), "PKT", "PKT");
    }
}
