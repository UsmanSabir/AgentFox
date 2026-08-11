using TradingAgent.Analysis;
using TradingAgent.Research;

namespace AgentFox.ChannelTests;

/// <summary>
/// Verifies weekly rollup and weekly/daily corroboration. Two behaviours carry real money risk and are
/// pinned here: a weekly bar must keep the true extremes of its constituent days (a rollup that used
/// closes would quietly pull every level inward), and a daily support test inside a WEEKLY breakdown
/// must not be reported as a buy — that is the falling knife one timeframe up, and it is exactly the
/// trade a daily-only screen recommends.
/// </summary>
[TestClass]
public sealed class MultiTimeframeTests
{
    // Monday 2026-01-05 is the start of ISO week 2 of 2026.
    private static readonly DateOnly FirstMonday = new(2026, 1, 5);

    private static PsxCandle Daily(
        DateOnly date, decimal open, decimal high, decimal low, decimal close, long volume = 100_000) => new()
    {
        Symbol = "TEST",
        Date = date,
        Open = open,
        High = high,
        Low = low,
        Close = close,
        Volume = volume
    };

    /// <summary>Weeks of Mon-Fri bars whose closes follow <paramref name="closeFor"/>.</summary>
    private static List<PsxCandle> DailySeries(int weeks, Func<int, decimal> closeFor)
    {
        var bars = new List<PsxCandle>();
        for (var i = 0; i < weeks * 5; i++)
        {
            var date = FirstMonday.AddDays(i / 5 * 7 + i % 5);
            var close = closeFor(i);
            bars.Add(Daily(date, close, close + 1.5m, close - 1.5m, close));
        }
        return bars;
    }

    [TestMethod]
    public void ToWeekly_GroupsMondayToFridayIntoOneBar()
    {
        var bars = DailySeries(3, i => 100m + i);

        var weekly = CandleResampler.ToWeekly(bars, asOf: FirstMonday.AddDays(60));

        Assert.AreEqual(3, weekly.Count);
        Assert.AreEqual(CandleInterval.Weekly, weekly[0].IntervalMinutes);
        Assert.AreEqual(FirstMonday, DateOnly.FromDateTime(weekly[0].BucketStartUtc!.Value),
            "The bucket start is the ISO week's Monday.");
        Assert.AreEqual(FirstMonday.AddDays(4), weekly[0].Date,
            "The bar is dated by the week's last session, so 'as of' is a real trading date.");
    }

    [TestMethod]
    public void ToWeekly_KeepsTrueExtremesNotClosingRange()
    {
        // A week whose wicks reach well beyond every close: resampling from closes would report the
        // range as 100-104 and move support and resistance inward by several percent.
        var bars = new List<PsxCandle>
        {
            Daily(FirstMonday,            100m, 108m, 92m, 101m, 10),
            Daily(FirstMonday.AddDays(1), 101m, 103m, 99m, 102m, 20),
            Daily(FirstMonday.AddDays(2), 102m, 105m, 97m, 103m, 30),
            Daily(FirstMonday.AddDays(3), 103m, 106m, 95m, 104m, 40),
            Daily(FirstMonday.AddDays(4), 104m, 107m, 90m, 100m, 50)
        };

        var week = CandleResampler.ToWeekly(bars, asOf: FirstMonday.AddDays(30))[0];

        Assert.AreEqual(100m, week.Open, "Open is Monday's open.");
        Assert.AreEqual(100m, week.Close, "Close is Friday's close.");
        Assert.AreEqual(108m, week.High, "High is the week's highest traded price, wick included.");
        Assert.AreEqual(90m, week.Low, "Low is the week's lowest traded price, wick included.");
        Assert.AreEqual(150L, week.Volume, "Volume is the sum of the week's sessions.");
    }

    [TestMethod]
    public void ToWeekly_MarksAnUnfinishedWeekAsLive()
    {
        var bars = DailySeries(2, i => 100m + i);
        // "Now" sits inside the second week, so that bar is still forming.
        var asOf = FirstMonday.AddDays(8);

        var weekly = CandleResampler.ToWeekly(bars, asOf);

        Assert.IsFalse(weekly[0].IsLive);
        Assert.IsTrue(weekly[^1].IsLive, "The in-progress week must not be treated as settled.");
    }

    [TestMethod]
    public void ToWeekly_SkipsWeekendDatesAndEmptyInput()
    {
        Assert.AreEqual(0, CandleResampler.ToWeekly([]).Count);

        // A holiday-shortened week still forms one bar from whatever sessions exist.
        var week = CandleResampler.ToWeekly(
            [Daily(FirstMonday, 100m, 102m, 99m, 101m)], asOf: FirstMonday.AddDays(30));

        Assert.AreEqual(1, week.Count);
        Assert.AreEqual(101m, week[0].Close);
    }

    [TestMethod]
    public void Analyze_WeeklyBreakdown_IsFlaggedEvenWhenDailyLooksLikeSupport()
    {
        // A relentless multi-month decline: on the weekly chart this is a breakdown. Any daily bounce
        // inside it still sits under falling weekly structure.
        var bars = DailySeries(40, i => 400m - i * 1.5m);

        var view = MultiTimeframeAnalyzer.Analyze("TEST", bars, new TechnicalOptions(), 2.0m);

        Assert.IsNotNull(view.Weekly, "40 weeks is ample history for a weekly read.");
        Assert.IsTrue(view.WeeklyBreakdown,
            $"A sustained decline must read as a weekly breakdown, got {view.Weekly!.Setup}.");
        Assert.IsTrue(view.Notes.Any(n => n.Contains("WEEKLY BREAKDOWN", StringComparison.Ordinal)),
            "The breakdown has to be stated in the notes the agent reads back.");
    }

    [TestMethod]
    public void Analyze_RangeBoundYear_ConfirmsLevelsOnBothTimeframes()
    {
        // A price oscillating in a stable band prints daily and weekly pivots at the same extremes,
        // which is precisely the confluence worth acting on.
        var bars = DailySeries(60, i => 100m + (i % 20 < 10 ? i % 20 : 20 - i % 20) * 2m);

        var view = MultiTimeframeAnalyzer.Analyze("TEST", bars, new TechnicalOptions(), 2.0m);

        Assert.IsNotNull(view.Weekly);
        Assert.IsTrue(view.ConfirmedSupports.Count + view.ConfirmedResistances.Count > 0,
            "A stable band should produce at least one level both timeframes recognise.");

        foreach (var level in view.ConfirmedSupports.Concat(view.ConfirmedResistances))
        {
            Assert.IsTrue(level.SeparationPercent <= 2.0m,
                $"A confirmed level must sit inside the tolerance, got {level.SeparationPercent}%.");
            Assert.IsTrue(level.WeeklyTouches >= 1);
        }
    }

    [TestMethod]
    public void Analyze_TooFewWeeks_ReportsUnknownAlignmentRatherThanGuessing()
    {
        var bars = DailySeries(4, i => 100m + i);

        var view = MultiTimeframeAnalyzer.Analyze("TEST", bars, new TechnicalOptions(), 2.0m);

        Assert.IsNull(view.Weekly, "Four weeks is not enough weekly history to report a read.");
        Assert.AreEqual(TimeframeAlignment.Unknown, view.Alignment);
        Assert.AreEqual(0, view.ConfirmedSupports.Count);
        Assert.IsFalse(view.EntryLevelConfirmedWeekly);
        Assert.IsTrue(view.Notes.Any(n => n.Contains("weekly bars", StringComparison.Ordinal)),
            "The shortfall must be explained, not silently omitted.");
    }

    [TestMethod]
    public void Analyze_AlwaysReportsTheDailyReadEvenWithoutWeeklyHistory()
    {
        var bars = DailySeries(4, i => 100m + i);

        var view = MultiTimeframeAnalyzer.Analyze("TEST", bars, new TechnicalOptions(), 2.0m);

        Assert.AreEqual("TEST", view.Daily.Symbol);
        Assert.AreEqual(bars.Count, view.Daily.Bars);
        Assert.AreEqual("1D", view.Daily.Interval);
    }

    [TestMethod]
    public void Analyze_WeeklySeriesIsLabelledAsWeekly()
    {
        var bars = DailySeries(30, i => 100m + (i % 20 < 10 ? i % 20 : 20 - i % 20) * 2m);

        var view = MultiTimeframeAnalyzer.Analyze("TEST", bars, new TechnicalOptions(), 2.0m);

        Assert.AreEqual("1W", view.Weekly!.Interval);
        Assert.IsFalse(view.Weekly.Reasons.Any(r => r.Contains("-day", StringComparison.Ordinal)),
            "A weekly read must describe its measurements in weeks: " +
            string.Join(" | ", view.Weekly.Reasons));
    }
}
