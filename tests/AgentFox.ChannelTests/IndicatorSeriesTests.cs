using TradingAgent.Analysis;
using TradingAgent.Research;

namespace AgentFox.ChannelTests;

/// <summary>
/// The chart plots <see cref="IndicatorSeries"/> while the text beside it quotes
/// <see cref="TechnicalAnalyzer"/>'s snapshot. Those are two implementations of the same formulas, so
/// these tests pin them together: the last element of each series must equal the snapshot's scalar
/// value. Without that, the chart could draw one story while the numbers tell another — and the
/// discrepancy would be invisible until someone traded on it.
/// </summary>
[TestClass]
public sealed class IndicatorSeriesTests
{
    [TestMethod]
    public void SmaAndRsiSeries_EndOnTheSnapshotValues()
    {
        var candles = Series(120);
        var snapshot = TechnicalAnalyzer.Analyze("TEST", candles, new TechnicalOptions());
        var closes = IndicatorSeries.Closes(candles);

        Assert.AreEqual(snapshot.Sma20, IndicatorSeries.Sma(closes, 20)[^1], "SMA20 must agree.");
        Assert.AreEqual(snapshot.Sma50, IndicatorSeries.Sma(closes, 50)[^1], "SMA50 must agree.");
        Assert.AreEqual(snapshot.Rsi14, IndicatorSeries.Rsi(closes, 14)[^1], "RSI(14) must agree.");
    }

    [TestMethod]
    public void Series_AreBarAlignedAndNullUntilComputable()
    {
        var closes = IndicatorSeries.Closes(Series(30));

        var sma20 = IndicatorSeries.Sma(closes, 20);
        Assert.AreEqual(closes.Count, sma20.Length, "A series must be index-aligned with its bars.");
        Assert.IsNull(sma20[18], "SMA20 cannot exist before 20 closes.");
        Assert.IsNotNull(sma20[19], "SMA20 must appear exactly at the 20th close.");

        // Wilder's RSI needs period+1 closes to have `period` deltas, so its first value is at index 14.
        var rsi = IndicatorSeries.Rsi(closes, 14);
        Assert.IsNull(rsi[13]);
        Assert.IsNotNull(rsi[14]);
    }

    [TestMethod]
    public void Sma_IsAWindowedMeanNotACumulativeOne()
    {
        // A running sum that forgets to subtract the leaving element still looks plausible on trending
        // data, so this uses a step: the mean of the last 3 must ignore the early values entirely.
        decimal[] closes = [1m, 1m, 1m, 10m, 10m, 10m];
        var sma = IndicatorSeries.Sma(closes, 3);

        Assert.AreEqual(1m, sma[2]);
        Assert.AreEqual(10m, sma[5]);
    }

    [TestMethod]
    public void Rsi_IsOneHundredWhenNothingFalls()
    {
        var closes = Enumerable.Range(0, 40).Select(i => 100m + i).ToList();
        Assert.AreEqual(100m, IndicatorSeries.Rsi(closes, 14)[^1],
            "An unbroken advance has no average loss; the formula must not divide by zero.");
    }

    [TestMethod]
    public void EmptyOrShortInput_ProducesNoValuesRatherThanThrowing()
    {
        Assert.AreEqual(0, IndicatorSeries.Sma([], 20).Length);
        Assert.AreEqual(0, IndicatorSeries.Rsi([], 14).Length);
        Assert.IsTrue(IndicatorSeries.Rsi([1m, 2m, 3m], 14).All(v => v is null));
    }

    /// <summary>A deterministic zig-zag with drift — enough movement for RSI to be meaningful.</summary>
    private static List<PsxCandle> Series(int count)
    {
        var start = new DateOnly(2026, 1, 5);
        var bars = new List<PsxCandle>(count);
        for (var i = 0; i < count; i++)
        {
            var close = 100m + i * 0.4m + (i % 7 - 3) * 1.3m;
            bars.Add(new PsxCandle
            {
                Symbol = "TEST",
                Date = start.AddDays(i),
                Open = close - 0.5m,
                High = close + 1.2m,
                Low = close - 1.4m,
                Close = close,
                Volume = 50_000 + i * 100
            });
        }
        return bars;
    }
}
