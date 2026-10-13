using System.Globalization;

namespace TradingAgent.Research;

/// <summary>
/// Rolls daily candles up into higher timeframes. Pure and deterministic, so the rollup is
/// unit-tested rather than trusted.
///
/// Weekly and monthly bars are EXACT rather than approximated: because the daily bars carry true high
/// and low (from the exchange's per-date market summary), a period's high is genuinely the highest
/// price traded in it. Resampling from closing prices — the only thing the portal's long JSON series
/// offers — would silently drop every wick and shift support/resistance inward.
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

    /// <summary>
    /// Groups daily bars into calendar months, oldest first. The bar is anchored to the first of the
    /// month but dated by its last trading session, matching the weekly representation.
    /// </summary>
    public static IReadOnlyList<PsxCandle> ToMonthly(IReadOnlyList<PsxCandle> dailyBars, DateOnly? asOf = null)
    {
        if (dailyBars is null || dailyBars.Count == 0) return [];

        var today = asOf ?? DateOnly.FromDateTime(Market.PsxTime.Now());
        var currentMonth = (today.Year, today.Month);
        var months = new List<PsxCandle>();

        foreach (var group in dailyBars
            .Where(b => !b.IsIntraday)
            .GroupBy(b => (b.Date.Year, b.Date.Month))
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month))
        {
            var ordered = group.OrderBy(b => b.Date).ToList();
            var first = ordered[0];
            var last = ordered[^1];

            months.Add(new PsxCandle
            {
                Symbol          = last.Symbol,
                Date            = last.Date,
                BucketStartUtc  = new DateTime(group.Key.Year, group.Key.Month, 1, 0, 0, 0, DateTimeKind.Utc),
                IntervalMinutes = CandleInterval.Monthly,
                Open            = first.Open,
                High            = ordered.Max(b => b.High),
                Low             = ordered.Min(b => b.Low),
                Close           = last.Close,
                PreviousClose   = first.PreviousClose,
                Volume          = ordered.Sum(b => b.Volume),
                IsLive          = group.Key == currentMonth || ordered.Any(b => b.IsLive)
            });
        }

        return months;
    }

    /// <summary>
    /// Rolls fine intraday bars (the analytics portal's one-minute bars) up into
    /// <paramref name="intervalMinutes"/>-wide bars, oldest first.
    ///
    /// <para>
    /// Buckets are epoch-aligned exactly as <see cref="PsxDataClient.AggregateTicks"/> aligns them, so a
    /// 15-minute bar built from minute bars and one built from the PSX tick tape start at the same
    /// instant and an archive holding both never carries two bars for one slot. A source bar belongs
    /// to the bucket its START falls in. A bucket is live while its window has not elapsed at
    /// <paramref name="nowUtc"/>, or while any bar inside it is still forming.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PsxCandle> ToIntraday(
        IReadOnlyList<PsxCandle> fineBars, int intervalMinutes, DateTime nowUtc)
    {
        if (fineBars is null || fineBars.Count == 0 || intervalMinutes <= 0) return [];

        var width = intervalMinutes * 60L;
        var bars = new List<PsxCandle>();

        foreach (var group in fineBars
            .Where(b => b.BucketStartUtc is not null)
            .OrderBy(b => b.BucketStartUtc)
            .GroupBy(b => new DateTimeOffset(DateTime.SpecifyKind(b.BucketStartUtc!.Value, DateTimeKind.Utc))
                .ToUnixTimeSeconds() / width * width)
            .OrderBy(g => g.Key))
        {
            var ordered = group.ToList();
            var start = DateTimeOffset.FromUnixTimeSeconds(group.Key).UtcDateTime;

            bars.Add(new PsxCandle
            {
                Symbol          = ordered[0].Symbol,
                Date            = ordered[0].Date,
                BucketStartUtc  = start,
                IntervalMinutes = intervalMinutes,
                Open            = ordered[0].Open,
                High            = ordered.Max(b => b.High),
                Low             = ordered.Min(b => b.Low),
                Close           = ordered[^1].Close,
                Volume          = ordered.Sum(b => b.Volume),
                IsLive          = start.AddMinutes(intervalMinutes) > nowUtc || ordered.Any(b => b.IsLive)
            });
        }

        return bars;
    }

    private static (int Year, int Week) IsoWeekKey(DateOnly date)
    {
        var value = date.ToDateTime(TimeOnly.MinValue);
        return (ISOWeek.GetYear(value), ISOWeek.GetWeekOfYear(value));
    }

    private static DateOnly MondayOf((int Year, int Week) key) =>
        DateOnly.FromDateTime(ISOWeek.ToDateTime(key.Year, key.Week, DayOfWeek.Monday));
}
