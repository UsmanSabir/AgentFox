namespace TradingAgent.Research;

/// <summary>
/// Naming for the bar widths the analyzers work in. Kept in one place because the prose in a
/// technical read has to match the series it was computed from — "3 consecutive down days" on a 15m
/// series or a "20-day range" on a weekly one misstates what was actually measured.
/// </summary>
public static class CandleInterval
{
    public const int Daily = PsxCandle.DailyIntervalMinutes;
    public const int Weekly = 7 * PsxCandle.DailyIntervalMinutes;

    /// <summary>Canonical label: <c>1W</c>, <c>1D</c>, or <c>15m</c>.</summary>
    public static string Label(int minutes) => minutes switch
    {
        >= Weekly => "1W",
        >= Daily => "1D",
        _ => $"{minutes}m"
    };

    /// <summary>Noun for one bar, used in composed level names ("20-week low").</summary>
    public static string Unit(int minutes) => minutes switch
    {
        >= Weekly => "week",
        >= Daily => "day",
        _ => "bar"
    };

    /// <summary>Noun for a period in running prose ("3 consecutive down sessions").</summary>
    public static string Period(int minutes) => minutes switch
    {
        >= Weekly => "week",
        >= Daily => "session",
        _ => "bar"
    };
}
