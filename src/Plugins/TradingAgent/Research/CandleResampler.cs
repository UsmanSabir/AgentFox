using System.Globalization;

namespace TradingAgent.Research;

/// <summary>
/// Rolls daily candles up into higher timeframes. Pure and deterministic, so the rollup is
/// unit-tested rather than trusted.
///
/// Weekly bars are EXACT rather than approximated: because the daily bars carry true high and low
/// (from the exchange's per-date market summary), a week's high is genuinely the highest price traded
/// that week. Resampling from closing prices — the only thing the portal's long JSON series offers —
/// would silently drop every wick and shift support/resistance inward.
/// </summary>
public static class CandleResampler
{
    /// <summary>
    /// Groups daily bars into ISO weeks (Monday-start), oldest first. A week is marked
    /// <see cref="PsxCandle.IsLive"/> while it is still in progress, so an unfinished week is never
    /// mistaken for a settled one when levels are drawn.
    /// </summary>
    public static IReadOnlyList<PsxCandle> ToWeekly(IReadOnlyList<PsxCandle> dailyBars, DateOnly? asOf = null)
    {
        if (dailyBars is null || dailyBars.Count == 0) return [];

        var today = asOf ?? DateOnly.FromDateTime(Market.PsxTime.Now());
        var currentWeek = IsoWeekKey(today);

        var weeks = new List<PsxCandle>();

        foreach (var group in dailyBars
            .Where(b => !b.IsIntraday)
            .GroupBy(b => IsoWeekKey(b.Date))
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Week))
        {
            var ordered = group.OrderBy(b => b.Date).ToList();
            var last = ordered[^1];

            weeks.Add(new PsxCandle
            {
                Symbol = last.Symbol,
                // Dated by the week's last session so "as of" reads as a real trading date, while the
                // bucket start pins the week itself for sorting.
                Date            = last.Date,
                BucketStartUtc  = MondayOf(group.Key).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                IntervalMinutes = CandleInterval.Weekly,
                Open            = ordered[0].Open,
                High            = ordered.Max(b => b.High),
                Low             = ordered.Min(b => b.Low),
                Close           = last.Close,
                PreviousClose   = ordered[0].PreviousClose,
                Volume          = ordered.Sum(b => b.Volume),
                IsLive          = group.Key == currentWeek || ordered.Any(b => b.IsLive)
            });
        }

        return weeks;
    }

    private static (int Year, int Week) IsoWeekKey(DateOnly date)
    {
        var value = date.ToDateTime(TimeOnly.MinValue);
        return (ISOWeek.GetYear(value), ISOWeek.GetWeekOfYear(value));
    }

    private static DateOnly MondayOf((int Year, int Week) key) =>
        DateOnly.FromDateTime(ISOWeek.ToDateTime(key.Year, key.Week, DayOfWeek.Monday));
}
