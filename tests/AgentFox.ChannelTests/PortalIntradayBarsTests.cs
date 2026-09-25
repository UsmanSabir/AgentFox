using TradingAgent.AhlAnalytics;
using TradingAgent.Research;

namespace AgentFox.ChannelTests;

/// <summary>
/// The intraday chart's current session now comes from the AHL research portal's one-minute bars,
/// rolled up to the requested width. These pin the three things that decide whether those bars can
/// stand beside the PSX tick path and the archive: how a stamp becomes a bucket, that the buckets line
/// up with the tick path's, and that adjusted bars are recognised before they reach a raw series.
/// </summary>
[TestClass]
public sealed class PortalIntradayBarsTests
{
    private static AhlCandle Row(string stamp, decimal o, decimal h, decimal l, decimal c, long v) =>
        new() { Date = stamp, Open = o, High = h, Low = l, Close = c, Volume = v };

    // 2026-09-25 10:00 PKT = 05:00 UTC.
    private static readonly DateTime LateInDay = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void A_minute_stamp_is_the_PKT_END_of_that_minute()
    {
        // MEASURED 2026-09-25: the row stamped 09:18:00 matched the PSX tape's 09:17:00-09:17:59
        // trades exactly (PPL 231.6/232/231.51/231.85), and that reading matched every settled minute.
        var bars = AhlCandleSource.MapMinuteRows("PPL", [Row("2026-09-25 09:18:00", 231.6m, 232m, 231.51m, 231.85m, 2526)], LateInDay);

        Assert.AreEqual(1, bars.Count);
        Assert.AreEqual(new DateTime(2026, 9, 25, 4, 17, 0, DateTimeKind.Utc), bars[0].BucketStartUtc);
        Assert.AreEqual(new DateOnly(2026, 9, 25), bars[0].Date);
        Assert.AreEqual(1, bars[0].IntervalMinutes);
        Assert.IsFalse(bars[0].IsLive);
    }

    [TestMethod]
    public void Rows_come_back_oldest_first_and_unpriced_or_undated_rows_are_dropped()
    {
        var bars = AhlCandleSource.MapMinuteRows("PPL",
        [
            Row("2026-09-25 09:33:00", 232, 232, 232, 232, 1),
            Row("2026-09-25 09:32:00", 0, 232, 232, 232, 1),   // no open: dropped, not zero-filled
            Row("2026-09-25", 232, 232, 232, 232, 1),          // no time: dropped
            Row("2026-09-25 09:31:00", 231, 231, 231, 231, 1)
        ], LateInDay);

        CollectionAssert.AreEqual(new[] { 231m, 232m }, bars.Select(b => b.Close).ToArray());
    }

    [TestMethod]
    public void The_minute_still_forming_is_live()
    {
        var now = new DateTime(2026, 9, 25, 4, 31, 30, DateTimeKind.Utc); // 09:31:30 PKT
        // Stamped 09:32:00, so it holds 09:31:00-09:31:59 and is still forming.
        var bars = AhlCandleSource.MapMinuteRows("PPL", [Row("2026-09-25 09:32:00", 1, 1, 1, 1, 1)], now);

        Assert.IsTrue(bars[0].IsLive);
    }

    [TestMethod]
    public void Minute_bars_roll_up_into_one_OHLCV_bar_per_bucket()
    {
        var minutes = AhlCandleSource.MapMinuteRows("PPL",
        [
            // Stamps close their minute: 09:31 holds 09:30, 09:45 holds 09:44, 09:46 holds 09:45.
            Row("2026-09-25 09:31:00", 232.00m, 232.40m, 231.90m, 232.30m, 100),
            Row("2026-09-25 09:38:00", 232.30m, 233.10m, 232.20m, 232.90m, 250),
            Row("2026-09-25 09:45:00", 232.90m, 233.00m, 231.50m, 231.80m, 50),
            Row("2026-09-25 09:46:00", 231.80m, 231.90m, 231.70m, 231.75m, 10)
        ], LateInDay);

        var bars = CandleResampler.ToIntraday(minutes, 15, LateInDay);

        Assert.AreEqual(2, bars.Count);
        var first = bars[0];
        Assert.AreEqual(new DateTime(2026, 9, 25, 4, 30, 0, DateTimeKind.Utc), first.BucketStartUtc);
        Assert.AreEqual(232.00m, first.Open);
        Assert.AreEqual(233.10m, first.High);
        Assert.AreEqual(231.50m, first.Low);
        Assert.AreEqual(231.80m, first.Close);
        Assert.AreEqual(400, first.Volume);
        Assert.AreEqual(15, first.IntervalMinutes);
        Assert.IsFalse(first.IsLive);
        Assert.AreEqual(new DateTime(2026, 9, 25, 4, 45, 0, DateTimeKind.Utc), bars[1].BucketStartUtc);
    }

    [TestMethod]
    public void Buckets_line_up_with_the_PSX_tick_path_so_the_archive_never_holds_two_bars_for_one_slot()
    {
        var minutes = AhlCandleSource.MapMinuteRows("PPL", [Row("2026-09-25 09:53:00", 232, 232, 232, 232, 1)], LateInDay);
        var fromMinutes = CandleResampler.ToIntraday(minutes, 15, LateInDay);

        var tick = new PsxTick(new DateTime(2026, 9, 25, 4, 52, 20, DateTimeKind.Utc), 232m, 1);
        var fromTicks = PsxDataClient.AggregateTicks("PPL", [tick], 15, LateInDay);

        Assert.AreEqual(fromTicks[0].BucketStartUtc, fromMinutes[0].BucketStartUtc);
        Assert.AreEqual(fromTicks[0].Date, fromMinutes[0].Date);
    }

    [TestMethod]
    public void A_bucket_whose_window_has_not_elapsed_is_live()
    {
        var now = new DateTime(2026, 9, 25, 4, 38, 0, DateTimeKind.Utc); // 09:38 PKT, inside 09:30-09:45
        var minutes = AhlCandleSource.MapMinuteRows("PPL", [Row("2026-09-25 09:31:00", 1, 1, 1, 1, 1)], now);

        Assert.IsTrue(CandleResampler.ToIntraday(minutes, 15, now)[0].IsLive);
    }

    [TestMethod]
    public void Sub_paisa_prices_mark_bars_as_adjusted()
    {
        var raw = AhlCandleSource.MapMinuteRows("LUCK", [Row("2026-09-24 10:00:00", 439.60m, 439.61m, 439.50m, 439.55m, 5)], LateInDay);
        var adjusted = AhlCandleSource.MapMinuteRows("LUCK", [Row("2026-09-24 10:00:00", 162.09221369698164m, 163m, 162m, 162.5m, 5)], LateInDay);

        Assert.IsFalse(AhlCandleSource.LooksAdjusted(raw));
        Assert.IsTrue(AhlCandleSource.LooksAdjusted(adjusted));
    }
}
