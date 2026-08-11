using TradingAgent.Analysis;
using TradingAgent.Research;

namespace AgentFox.ChannelTests;

/// <summary>
/// Verifies that the PSX tick tape is turned into correct intraday candles. The portal publishes no
/// intraday bars — only individual trades for the current session — so every intraday OHLC the agent
/// reasons over is produced here. A bucketing error would not fail loudly; it would quietly reshape
/// the candles a trade decision is read from.
/// </summary>
[TestClass]
public sealed class PsxIntradayAggregationTests
{
    // 09:30 PKT on 2026-08-11 == 04:30 UTC, which is where the real session's first tick lands.
    private static readonly DateTime SessionOpenUtc = new(2026, 8, 11, 4, 30, 0, DateTimeKind.Utc);

    private static PsxTick Tick(int secondsIn, decimal price, long quantity = 100) =>
        new(SessionOpenUtc.AddSeconds(secondsIn), price, quantity);

    [TestMethod]
    public void AggregateTicks_BuildsOhlcvPerBucket()
    {
        // Two full 5m buckets: prices chosen so open/high/low/close are all distinct.
        List<PsxTick> ticks =
        [
            Tick(0, 100m, 10), Tick(60, 105m, 20), Tick(120, 98m, 30), Tick(240, 102m, 40),
            Tick(300, 103m, 50), Tick(400, 110m, 60), Tick(590, 107m, 70)
        ];

        var bars = PsxDataClient.AggregateTicks("test", ticks, 5, SessionOpenUtc.AddHours(2));

        Assert.AreEqual(2, bars.Count);

        Assert.AreEqual(100m, bars[0].Open, "Open is the first trade in the bucket.");
        Assert.AreEqual(105m, bars[0].High);
        Assert.AreEqual(98m, bars[0].Low);
        Assert.AreEqual(102m, bars[0].Close, "Close is the last trade in the bucket.");
        Assert.AreEqual(100L, bars[0].Volume, "Volume is the sum of the bucket's trade quantities.");

        Assert.AreEqual(103m, bars[1].Open);
        Assert.AreEqual(110m, bars[1].High);
        Assert.AreEqual(103m, bars[1].Low);
        Assert.AreEqual(107m, bars[1].Close);
        Assert.AreEqual(180L, bars[1].Volume);
    }

    [TestMethod]
    public void AggregateTicks_AlignsBucketsToWallClock()
    {
        var bars = PsxDataClient.AggregateTicks("TEST",
            [Tick(0, 100m), Tick(299, 101m), Tick(300, 102m)], 5, SessionOpenUtc.AddHours(2));

        // PKT is UTC+5 with no DST, so epoch-aligned buckets land on clean exchange-clock boundaries.
        Assert.AreEqual(SessionOpenUtc, bars[0].BucketStartUtc);
        Assert.AreEqual(SessionOpenUtc.AddMinutes(5), bars[1].BucketStartUtc);
        Assert.AreEqual(new DateOnly(2026, 8, 11), bars[0].Date, "Bars carry their PKT session date.");
        Assert.AreEqual(5, bars[0].IntervalMinutes);
    }

    [TestMethod]
    public void AggregateTicks_DifferentWidthsPartitionTheSameTicks()
    {
        // 04:30–04:59 UTC — inside a single hourly bucket, so the two widths are directly comparable.
        var ticks = Enumerable.Range(0, 30)
            .Select(i => Tick(i * 60, 100m + i % 7, 10))
            .ToList();

        var fifteen = PsxDataClient.AggregateTicks("TEST", ticks, 15, SessionOpenUtc.AddHours(3));
        var sixty = PsxDataClient.AggregateTicks("TEST", ticks, 60, SessionOpenUtc.AddHours(3));

        Assert.AreEqual(2, fifteen.Count, "30 one-minute trades fill two 15m buckets.");
        Assert.AreEqual(1, sixty.Count);
        Assert.AreEqual(fifteen.Sum(b => b.Volume), sixty.Sum(b => b.Volume),
            "Rebucketing must not create or lose volume.");
        Assert.AreEqual(fifteen.Max(b => b.High), sixty[0].High);
        Assert.AreEqual(fifteen.Min(b => b.Low), sixty[0].Low);
        Assert.AreEqual(fifteen[0].Open, sixty[0].Open);
        Assert.AreEqual(fifteen[^1].Close, sixty[0].Close);
    }

    [TestMethod]
    public void AggregateTicks_HourlyBucketsAlignToTheClockNotToTheFirstTick()
    {
        // A 60m series must break at 05:00, not 60 minutes after the session's first trade —
        // otherwise today's bars would not line up with any other session's.
        var bars = PsxDataClient.AggregateTicks("TEST",
            [Tick(0, 100m), Tick(1800, 101m), Tick(2100, 102m)], 60, SessionOpenUtc.AddHours(4));

        Assert.AreEqual(2, bars.Count);
        Assert.AreEqual(new DateTime(2026, 8, 11, 4, 0, 0, DateTimeKind.Utc), bars[0].BucketStartUtc);
        Assert.AreEqual(new DateTime(2026, 8, 11, 5, 0, 0, DateTimeKind.Utc), bars[1].BucketStartUtc);
    }

    [TestMethod]
    public void AggregateTicks_FlagsOnlyTheUnfinishedBucketAsLive()
    {
        // "Now" sits inside the second bucket, so only that one is still forming.
        var now = SessionOpenUtc.AddMinutes(7);

        var bars = PsxDataClient.AggregateTicks("TEST",
            [Tick(0, 100m), Tick(120, 101m), Tick(360, 102m)], 5, now);

        Assert.AreEqual(2, bars.Count);
        Assert.IsFalse(bars[0].IsLive, "An elapsed bucket is settled.");
        Assert.IsTrue(bars[1].IsLive, "The in-progress bucket must be flagged, never archived as final.");
    }

    [TestMethod]
    public void AggregateTicks_AfterTheCloseEveryBucketIsSettled()
    {
        var bars = PsxDataClient.AggregateTicks("TEST",
            [Tick(0, 100m), Tick(360, 102m)], 5, SessionOpenUtc.AddHours(8));

        Assert.IsTrue(bars.All(b => !b.IsLive));
    }

    [TestMethod]
    public void AggregateTicks_UnorderedTicks_StillProduceCorrectOpenAndClose()
    {
        // The portal returns the tape newest-first; open/close must follow time, not arrival order.
        List<PsxTick> reversed = [Tick(240, 102m), Tick(120, 98m), Tick(60, 105m), Tick(0, 100m)];

        var bars = PsxDataClient.AggregateTicks("TEST", reversed, 5, SessionOpenUtc.AddHours(2));

        Assert.AreEqual(1, bars.Count);
        Assert.AreEqual(100m, bars[0].Open);
        Assert.AreEqual(102m, bars[0].Close);
    }

    [TestMethod]
    public void AggregateTicks_NoTicksOrBadInterval_ReturnsEmpty()
    {
        Assert.AreEqual(0, PsxDataClient.AggregateTicks("TEST", [], 5).Count);
        Assert.AreEqual(0, PsxDataClient.AggregateTicks("TEST", [Tick(0, 100m)], 0).Count);
    }

    [TestMethod]
    public void ResolveInterval_AcceptsSupportedLabelsAndRejectsOthers()
    {
        Assert.AreEqual(PsxCandle.DailyIntervalMinutes, PsxDataClient.ResolveInterval(null));
        Assert.AreEqual(PsxCandle.DailyIntervalMinutes, PsxDataClient.ResolveInterval("1D"));
        Assert.AreEqual(60, PsxDataClient.ResolveInterval("60m"));
        Assert.AreEqual(60, PsxDataClient.ResolveInterval("1h"));
        Assert.AreEqual(15, PsxDataClient.ResolveInterval("15M"));
        Assert.AreEqual(5, PsxDataClient.ResolveInterval("5m"));
        Assert.IsNull(PsxDataClient.ResolveInterval("7m"), "An unsupported width must be rejected, not rounded.");
        Assert.IsNull(PsxDataClient.ResolveInterval("1w"));
    }

    [TestMethod]
    public void Analyze_IntradaySeries_IsSequencedWithinTheSessionAndLabelled()
    {
        // Many bars share one session date, so ordering must come from the bucket time, not the date.
        var ticks = Enumerable.Range(0, 200)
            .Select(i => Tick(i * 60, 100m + i % 11, 10))
            .ToList();
        var bars = PsxDataClient.AggregateTicks("TEST", ticks, 5, SessionOpenUtc.AddHours(6));

        var snapshot = TechnicalAnalyzer.Analyze("TEST", bars, new TechnicalOptions());

        Assert.AreEqual("5m", snapshot.Interval);
        Assert.AreEqual(bars.Count, snapshot.Bars);
        Assert.AreEqual(bars[^1].Close, snapshot.Close, "The last bar of the session must be the current one.");
        Assert.AreEqual(bars[^1].BucketStartUtc, snapshot.AsOfUtc);
        Assert.IsNotNull(snapshot.Rsi14, "A single session of 5m bars is enough history for indicators.");
    }

    [TestMethod]
    public void Analyze_IntradaySeries_ReasonsSayBarsNotDays()
    {
        var ticks = Enumerable.Range(0, 200).Select(i => Tick(i * 60, 100m + i % 11, 10)).ToList();
        var bars = PsxDataClient.AggregateTicks("TEST", ticks, 5, SessionOpenUtc.AddHours(6));

        var snapshot = TechnicalAnalyzer.Analyze("TEST", bars, new TechnicalOptions());

        Assert.IsFalse(snapshot.Reasons.Any(r => r.Contains("-day", StringComparison.Ordinal)
                || r.Contains("session(s)", StringComparison.Ordinal)),
            "On a 5m series, describing measurements in days would misstate what was measured: " +
            string.Join(" | ", snapshot.Reasons));
    }
}
