using TradingAgent.Analysis;
using TradingAgent.Research;

namespace AgentFox.ChannelTests;

/// <summary>
/// Verifies the deterministic candle read that drives buy-at-support / sell-at-resistance
/// recommendations. The behaviour that matters most is the distinction between price being LOW and
/// price being SUPPORTED: a stock printing fresh lows on consecutive red sessions must classify as a
/// breakdown, never as a buy, because a naive "near the range low" screen would recommend exactly
/// that trade.
/// </summary>
[TestClass]
public sealed class TechnicalAnalyzerTests
{
    private static readonly TechnicalOptions Options = new();

    /// <summary>Range-bound series oscillating 100↔120 in 4-point steps, low/high = close ∓ 1.</summary>
    private static List<PsxCandle> RangeBoundSeries(int cycles = 6)
    {
        var closes = new List<decimal>();
        for (var c = 0; c < cycles; c++)
            closes.AddRange([100m, 104m, 108m, 112m, 116m, 120m, 116m, 112m, 108m, 104m]);

        return closes.Select((close, i) => Bar(i, close)).ToList();
    }

    private static PsxCandle Bar(
        int dayOffset,
        decimal close,
        decimal? low = null,
        decimal? high = null,
        long volume = 100_000,
        bool isLive = false) => new()
    {
        Symbol = "TEST",
        Date = new DateOnly(2026, 1, 5).AddDays(dayOffset),
        Open = close,
        High = high ?? close + 1m,
        Low = low ?? close - 1m,
        Close = close,
        Volume = volume,
        IsLive = isLive
    };

    [TestMethod]
    public void Analyze_PriceReturningToRangeLow_IsBuyAtSupport()
    {
        var bars = RangeBoundSeries();
        bars.Add(Bar(bars.Count, 100.5m, low: 100.2m, high: 104m));

        var snapshot = TechnicalAnalyzer.Analyze("TEST", bars, Options);

        Assert.AreEqual(PriceZone.AtSupport, snapshot.Zone);
        Assert.AreEqual(TradeSetup.BuyAtSupport, snapshot.Setup);
        Assert.IsNotNull(snapshot.NearestSupport);
        Assert.IsTrue(snapshot.NearestSupport <= 100.5m,
            $"Support {snapshot.NearestSupport} should sit at or below the last price.");
        Assert.IsTrue(snapshot.NearestResistance >= 119m,
            $"Resistance {snapshot.NearestResistance} should be drawn from the ~121 swing highs.");
        Assert.IsFalse(snapshot.MakesNewRangeLow, "Holding above the prior low is a test, not a break.");
    }

    [TestMethod]
    public void Analyze_RepeatedlyTestedLevel_CountsTouches()
    {
        var bars = RangeBoundSeries();
        bars.Add(Bar(bars.Count, 100.5m, low: 100.2m, high: 104m));

        var snapshot = TechnicalAnalyzer.Analyze("TEST", bars, Options);

        Assert.IsTrue(snapshot.Supports.Count > 0);
        Assert.IsTrue(snapshot.Supports[0].Touches > 1,
            "A level revisited every cycle should merge into one level with several touches.");
    }

    [TestMethod]
    public void Analyze_RangeBoundSeriesAtSupport_ProducesTradableRewardRisk()
    {
        var bars = RangeBoundSeries();
        bars.Add(Bar(bars.Count, 100.5m, low: 100.2m, high: 104m));

        var snapshot = TechnicalAnalyzer.Analyze("TEST", bars, Options);

        Assert.IsNotNull(snapshot.SuggestedEntry);
        Assert.IsNotNull(snapshot.SuggestedStop);
        Assert.IsNotNull(snapshot.SuggestedTarget);
        Assert.IsTrue(snapshot.SuggestedStop < snapshot.SuggestedEntry, "The stop must sit below the entry.");
        Assert.IsTrue(snapshot.SuggestedTarget > snapshot.SuggestedEntry, "The target must sit above the entry.");
        Assert.IsTrue(snapshot.RewardRiskRatio > 1m,
            $"Buying the bottom of a 100-120 range should beat 1:1, got {snapshot.RewardRiskRatio}.");
    }

    [TestMethod]
    public void Analyze_PriceApproachingRangeHigh_IsSellAtResistance()
    {
        var bars = RangeBoundSeries();
        bars.Add(Bar(bars.Count, 119.5m, low: 116m, high: 120m));

        var snapshot = TechnicalAnalyzer.Analyze("TEST", bars, Options);

        Assert.AreEqual(PriceZone.AtResistance, snapshot.Zone);
        Assert.AreEqual(TradeSetup.SellAtResistance, snapshot.Setup);
        Assert.IsNotNull(snapshot.NearestResistance);
        Assert.IsTrue(snapshot.PercentBelowResistance <= Options.ResistanceProximityPercent);

        // The trade math is buy-side (entry at support), so a sell setup must name the sell level
        // explicitly — otherwise the entry figure below the current price reads like a buy signal.
        Assert.IsTrue(snapshot.Reasons.Any(r => r.StartsWith("SELL setup:", StringComparison.Ordinal)),
            "A sell setup must state the level to sell into.");
    }

    [TestMethod]
    public void Analyze_FreshLowsOnConsecutiveDownDays_IsBreakdownNotABuy()
    {
        // The falling-knife case: price IS at the bottom of its range, which is exactly why a naive
        // screen would buy it. Support has given way, so it must not be offered as a buy.
        var bars = RangeBoundSeries(4);
        foreach (var close in new[] { 98m, 95m, 92m, 89m, 86m, 83m })
            bars.Add(Bar(bars.Count, close));

        var snapshot = TechnicalAnalyzer.Analyze("TEST", bars, Options);

        Assert.AreEqual(TradeSetup.AvoidBreakdown, snapshot.Setup);
        Assert.AreNotEqual(TradeSetup.BuyAtSupport, snapshot.Setup);
        Assert.IsTrue(snapshot.MakesNewRangeLow);
        Assert.IsTrue(snapshot.ConsecutiveDownDays >= Options.BreakdownDownDays);
        Assert.IsTrue(snapshot.Reasons.Any(r => r.Contains("BREAKDOWN", StringComparison.Ordinal)),
            "The breakdown must be stated in the reasons the agent reads back to the user.");
    }

    [TestMethod]
    public void Analyze_MidRangePrice_IsWait()
    {
        var bars = RangeBoundSeries();
        bars.Add(Bar(bars.Count, 110m));

        var snapshot = TechnicalAnalyzer.Analyze("TEST", bars, Options);

        Assert.AreEqual(TradeSetup.Wait, snapshot.Setup);
        Assert.AreEqual(PriceZone.MidRange, snapshot.Zone);
    }

    [TestMethod]
    public void Analyze_BrokenSupportBecomesResistance()
    {
        // After a decisive break, the old floor sits ABOVE price — the analyzer classifies levels by
        // where they are relative to the last price, so the level reappears as overhead resistance.
        var bars = RangeBoundSeries(4);
        foreach (var close in new[] { 98m, 95m, 92m, 89m, 86m, 83m })
            bars.Add(Bar(bars.Count, close));

        var snapshot = TechnicalAnalyzer.Analyze("TEST", bars, Options);

        Assert.IsTrue(snapshot.Resistances.Any(r => r.Price >= 99m && r.Price <= 101m),
            "The former ~99-100 support should now be listed as resistance.");
    }

    [TestMethod]
    public void Analyze_IndicatorsComputedOverAdequateHistory()
    {
        var bars = RangeBoundSeries();

        var snapshot = TechnicalAnalyzer.Analyze("TEST", bars, Options);

        Assert.IsNotNull(snapshot.Rsi14);
        Assert.IsTrue(snapshot.Rsi14 is > 0 and < 100);
        Assert.IsNotNull(snapshot.Atr14);
        Assert.IsTrue(snapshot.Atr14 > 0);
        Assert.IsNotNull(snapshot.Sma20);
        Assert.IsNotNull(snapshot.Sma50);
        Assert.IsNotNull(snapshot.AverageVolume);
        Assert.AreEqual(1m, snapshot.VolumeRatio, "A flat-volume series should sit at 1× its average.");
    }

    [TestMethod]
    public void Analyze_TooLittleHistory_ReportsInsufficientDataWithoutGuessing()
    {
        var bars = Enumerable.Range(0, 5).Select(i => Bar(i, 100m + i)).ToList();

        var snapshot = TechnicalAnalyzer.Analyze("TEST", bars, Options);

        Assert.AreEqual(TradeSetup.InsufficientData, snapshot.Setup);
        Assert.IsTrue(snapshot.Warnings.Count > 0);
    }

    [TestMethod]
    public void Analyze_NoCandles_ReportsInsufficientData()
    {
        var snapshot = TechnicalAnalyzer.Analyze("TEST", [], Options);

        Assert.AreEqual(TradeSetup.InsufficientData, snapshot.Setup);
        Assert.AreEqual(PriceZone.Unknown, snapshot.Zone);
        Assert.IsTrue(snapshot.Warnings.Count > 0);
    }

    [TestMethod]
    public void Analyze_LiveLastBar_IsFlaggedForTheAgent()
    {
        var bars = RangeBoundSeries();
        bars.Add(Bar(bars.Count, 100.5m, low: 100.2m, high: 104m, isLive: true));

        var snapshot = TechnicalAnalyzer.Analyze("TEST", bars, Options);

        Assert.IsTrue(snapshot.UsesLiveBar);
        Assert.IsTrue(snapshot.Reasons.Any(r => r.Contains("forming", StringComparison.OrdinalIgnoreCase)
                && r.Contains("not a closed one", StringComparison.OrdinalIgnoreCase)),
            "A verdict drawn from an unsettled bar must say so: " + string.Join(" | ", snapshot.Reasons));
    }

    [TestMethod]
    public void Analyze_UnorderedInput_IsSortedByDate()
    {
        var bars = RangeBoundSeries();
        bars.Add(Bar(bars.Count, 100.5m, low: 100.2m, high: 104m));
        var shuffled = bars.OrderByDescending(b => b.Date).ToList();

        var snapshot = TechnicalAnalyzer.Analyze("TEST", shuffled, Options);

        Assert.AreEqual(bars[^1].Date, snapshot.AsOf);
        Assert.AreEqual(100.5m, snapshot.Close);
    }

    [TestMethod]
    public void Analyze_FiftyTwoWeekExtremes_BecomeAdditionalLevels()
    {
        var bars = RangeBoundSeries();
        bars.Add(Bar(bars.Count, 110m));

        var snapshot = TechnicalAnalyzer.Analyze("TEST", bars, Options, high52Week: 150m, low52Week: 70m);

        Assert.IsTrue(snapshot.Resistances.Any(r => r.Origin.Contains("52-week", StringComparison.Ordinal)),
            "The 52-week high should appear among the resistance levels.");
        Assert.IsTrue(snapshot.Supports.Any(s => s.Origin.Contains("52-week", StringComparison.Ordinal)),
            "The 52-week low should appear among the support levels.");
    }
}
