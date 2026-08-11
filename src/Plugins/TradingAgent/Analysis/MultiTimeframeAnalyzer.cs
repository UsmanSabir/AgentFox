using TradingAgent.Research;

namespace TradingAgent.Analysis;

/// <summary>How the weekly and daily reads relate to each other.</summary>
public enum TimeframeAlignment
{
    Unknown,

    /// <summary>Both timeframes point the same way — the highest-quality setup.</summary>
    Aligned,

    /// <summary>One timeframe is neutral; the other carries the case.</summary>
    Mixed,

    /// <summary>The daily setup trades straight into the weekly one. Usually a trap.</summary>
    Conflicting
}

/// <summary>
/// A price level that exists on the daily chart and is corroborated by a weekly level nearby.
/// <see cref="WeeklyTouches"/> is how many weekly pivots merged into the confirming level: a floor
/// the weekly chart has defended repeatedly is a different proposition from one drawn off a single bar.
/// </summary>
public sealed record ConfluenceLevel(
    decimal Price,
    string Side,
    int DailyTouches,
    decimal WeeklyPrice,
    int WeeklyTouches,
    decimal SeparationPercent,
    string Origin);

/// <summary>
/// The combined weekly + daily read for one symbol, plus the levels both timeframes agree on.
/// Intraday is deliberately absent: it belongs to entry timing, not to level discovery.
/// </summary>
public sealed record MultiTimeframeView
{
    public string Symbol { get; init; } = "";
    public TechnicalSnapshot Daily { get; init; } = new();

    /// <summary>Null when there is not enough daily history archived to form usable weekly bars.</summary>
    public TechnicalSnapshot? Weekly { get; init; }

    public int WeeklyBars { get; init; }
    public TimeframeAlignment Alignment { get; init; }

    /// <summary>Daily supports corroborated by a weekly level, nearest to price first.</summary>
    public IReadOnlyList<ConfluenceLevel> ConfirmedSupports { get; init; } = [];

    /// <summary>Daily resistances corroborated by a weekly level, nearest to price first.</summary>
    public IReadOnlyList<ConfluenceLevel> ConfirmedResistances { get; init; } = [];

    /// <summary>True when the daily entry level is one the weekly chart also recognises.</summary>
    public bool EntryLevelConfirmedWeekly { get; init; }

    /// <summary>
    /// True when the weekly chart is breaking down. A daily dip inside a weekly breakdown is the
    /// classic falling knife, and no amount of daily-level neatness redeems it.
    /// </summary>
    public bool WeeklyBreakdown { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Combines the weekly and daily technical reads for one symbol.
///
/// The point is to stop levels being drawn from one timeframe in isolation. A daily support that the
/// weekly chart also recognises is structure; a daily support with nothing behind it is often just
/// last month's noise. Both snapshots come from <see cref="TechnicalAnalyzer"/>, so every number
/// remains deterministic — this class only relates them.
/// </summary>
public static class MultiTimeframeAnalyzer
{
    /// <summary>Weekly bars needed before the weekly read is worth reporting at all.</summary>
    public const int MinimumWeeklyBars = 12;

    public static MultiTimeframeView Analyze(
        string symbol,
        IReadOnlyList<PsxCandle> dailyBars,
        TechnicalOptions options,
        decimal confluenceTolerancePercent,
        decimal? high52Week = null,
        decimal? low52Week = null)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        var notes = new List<string>();

        var daily = TechnicalAnalyzer.Analyze(symbol, dailyBars, options, high52Week, low52Week);
        var weeklyBars = CandleResampler.ToWeekly(dailyBars);

        if (weeklyBars.Count < MinimumWeeklyBars)
        {
            notes.Add(
                $"Only {weeklyBars.Count} weekly bars could be formed from {dailyBars.Count} daily " +
                $"sessions ({MinimumWeeklyBars} needed). Weekly structure is unavailable, so the daily " +
                "levels stand alone and are less reliable than a confirmed level would be. Backfilling " +
                "more daily history fixes this permanently.");

            return new MultiTimeframeView
            {
                Symbol     = symbol,
                Daily      = daily,
                Weekly     = null,
                WeeklyBars = weeklyBars.Count,
                Alignment  = TimeframeAlignment.Unknown,
                Notes      = notes
            };
        }

        var weekly = TechnicalAnalyzer.Analyze(symbol, weeklyBars, options, high52Week, low52Week);
        var tolerance = Math.Clamp(confluenceTolerancePercent, 0.1m, 10m);

        var confirmedSupports = Corroborate(daily.Supports, weekly.Supports, "support", tolerance);
        var confirmedResistances = Corroborate(daily.Resistances, weekly.Resistances, "resistance", tolerance);

        var weeklyBreakdown = weekly.Setup == TradeSetup.AvoidBreakdown;
        var alignment = Align(daily.Setup, weekly.Setup, weekly.Zone);

        var entryConfirmed = daily.NearestSupport is { } entry
            && confirmedSupports.Any(c => Within(c.Price, entry, tolerance));

        // ── Notes: the reasons a human would want to see spelled out ──────────
        notes.Add($"Weekly: {weekly.Zone} / {weekly.Setup}" +
                  (weekly.NearestSupport is not null
                      ? $", nearest weekly support {weekly.NearestSupport} ({weekly.PercentAboveSupport}% below price)"
                      : ", no weekly support below price") +
                  (weekly.NearestResistance is not null
                      ? $", nearest weekly resistance {weekly.NearestResistance}."
                      : "."));

        if (weeklyBreakdown)
            notes.Add("WEEKLY BREAKDOWN: the weekly chart is making fresh lows while still falling. " +
                      "A daily support test inside this is a falling knife — do not buy it on the daily setup.");

        if (entryConfirmed)
            notes.Add($"The daily entry level {daily.NearestSupport} is confirmed by a weekly level — " +
                      "this is structure, not just a recent daily swing.");
        else if (daily.NearestSupport is not null)
            notes.Add($"The daily entry level {daily.NearestSupport} has NO weekly level behind it. " +
                      "Treat it as a weaker floor and size accordingly.");

        notes.Add(alignment switch
        {
            TimeframeAlignment.Aligned =>
                "Weekly and daily agree, which is the strongest configuration this analysis can report.",
            TimeframeAlignment.Conflicting =>
                "Weekly and daily disagree: the daily setup trades directly into the weekly one. " +
                "Counter-trend — either skip it or halve the size and take the first target.",
            TimeframeAlignment.Mixed =>
                "One timeframe is neutral, so the case rests on the other alone.",
            _ => "Timeframe alignment could not be established."
        });

        return new MultiTimeframeView
        {
            Symbol                    = symbol,
            Daily                     = daily,
            Weekly                    = weekly,
            WeeklyBars                = weeklyBars.Count,
            Alignment                 = alignment,
            ConfirmedSupports         = confirmedSupports,
            ConfirmedResistances      = confirmedResistances,
            EntryLevelConfirmedWeekly = entryConfirmed,
            WeeklyBreakdown           = weeklyBreakdown,
            Notes                     = notes
        };
    }

    /// <summary>
    /// Pairs each daily level with the closest weekly level within tolerance. Levels with no weekly
    /// counterpart are simply absent — the caller reads "confirmed" from presence, so an unconfirmed
    /// level can never be mistaken for a confirmed one with a low score.
    /// </summary>
    private static List<ConfluenceLevel> Corroborate(
        IReadOnlyList<PriceLevel> dailyLevels,
        IReadOnlyList<PriceLevel> weeklyLevels,
        string side,
        decimal tolerancePercent)
    {
        var confirmed = new List<ConfluenceLevel>();

        foreach (var daily in dailyLevels)
        {
            var match = weeklyLevels
                .Where(w => Within(w.Price, daily.Price, tolerancePercent))
                .OrderBy(w => Math.Abs(w.Price - daily.Price))
                .FirstOrDefault();

            if (match is null) continue;

            confirmed.Add(new ConfluenceLevel(
                Price:             daily.Price,
                Side:              side,
                DailyTouches:      daily.Touches,
                WeeklyPrice:       match.Price,
                WeeklyTouches:     match.Touches,
                SeparationPercent: daily.Price > 0
                                       ? Math.Round(Math.Abs(match.Price - daily.Price) / daily.Price * 100m, 2)
                                       : 0m,
                Origin:            $"daily {daily.Origin} + weekly {match.Origin}"));
        }

        return confirmed;
    }

    private static bool Within(decimal a, decimal b, decimal tolerancePercent) =>
        b > 0 && Math.Abs(a - b) / b * 100m <= tolerancePercent;

    /// <summary>
    /// Classifies daily-vs-weekly agreement. A weekly breakdown conflicts with any daily buy, and a
    /// daily buy under weekly resistance is the counter-trend case worth naming explicitly.
    /// </summary>
    private static TimeframeAlignment Align(TradeSetup daily, TradeSetup weekly, PriceZone weeklyZone)
    {
        if (daily is TradeSetup.InsufficientData || weekly is TradeSetup.InsufficientData)
            return TimeframeAlignment.Unknown;

        return (daily, weekly) switch
        {
            (TradeSetup.BuyAtSupport, TradeSetup.AvoidBreakdown) => TimeframeAlignment.Conflicting,
            (TradeSetup.BuyAtSupport, TradeSetup.SellAtResistance) => TimeframeAlignment.Conflicting,
            (TradeSetup.BuyAtSupport, TradeSetup.BuyAtSupport) => TimeframeAlignment.Aligned,
            (TradeSetup.BuyAtSupport, TradeSetup.Wait) =>
                weeklyZone is PriceZone.UpperRange or PriceZone.AtResistance
                    ? TimeframeAlignment.Conflicting
                    : TimeframeAlignment.Mixed,

            (TradeSetup.SellAtResistance, TradeSetup.SellAtResistance) => TimeframeAlignment.Aligned,
            (TradeSetup.SellAtResistance, TradeSetup.AvoidBreakdown) => TimeframeAlignment.Aligned,
            (TradeSetup.SellAtResistance, TradeSetup.BuyAtSupport) => TimeframeAlignment.Conflicting,
            (TradeSetup.SellAtResistance, TradeSetup.Wait) => TimeframeAlignment.Mixed,

            (TradeSetup.AvoidBreakdown, _) => TimeframeAlignment.Aligned,

            _ => TimeframeAlignment.Mixed
        };
    }
}
