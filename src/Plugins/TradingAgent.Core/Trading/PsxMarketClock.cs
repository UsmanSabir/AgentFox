namespace TradingAgent.Trading;

/// <summary>
/// Legacy static compatibility clock. New execution code must use IMarketCalendar so configured
/// holidays and special sessions are enforced. This wrapper implements the current regular schedule.
/// </summary>
public static class PsxMarketClock
{
    private static readonly TimeZoneInfo Pkt = ResolvePkt();

    public readonly record struct MarketStatus(bool IsOpen, DateTime PktNow, string Reason);

    /// <summary>Current PSX market status (time in PKT, open/closed, and a human reason).</summary>
    public static MarketStatus Now()
    {
        var pktNow    = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Pkt);
        var tod       = TimeOnly.FromDateTime(pktNow);
        var sessions  = pktNow.DayOfWeek switch
        {
            DayOfWeek.Monday or DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday =>
                new[] { (new TimeOnly(9, 32), new TimeOnly(15, 30)) },
            DayOfWeek.Friday =>
                new[]
                {
                    (new TimeOnly(9, 17), new TimeOnly(12, 0)),
                    (new TimeOnly(14, 32), new TimeOnly(16, 30))
                },
            _ => Array.Empty<(TimeOnly, TimeOnly)>()
        };
        var isOpen = sessions.Any(s => tod >= s.Item1 && tod < s.Item2);

        var reason = isOpen
            ? $"PSX regular market is open. Current time: {tod:HH:mm} PKT."
            : sessions.Length == 0
                ? $"PSX is closed on {pktNow.DayOfWeek}s."
                : $"PSX regular market is outside an open session. Current: {tod:HH:mm} PKT.";

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
