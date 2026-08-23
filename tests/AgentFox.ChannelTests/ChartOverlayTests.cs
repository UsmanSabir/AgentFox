using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Chart;
using TradingAgent.Config;
using TradingAgent.Market;

namespace AgentFox.ChannelTests;

/// <summary>
/// The chart overlay contract: how a licensed edition puts projections, predicted points, a next
/// target, or a confidence band on the dashboard's EXISTING chart instead of on a second page.
///
/// <para>
/// Two properties carry most of the risk and are what these tests pin down. First, the chart is a
/// READ path: a provider that throws or overruns its budget must be dropped with the chart still
/// rendering, because a slow model must never stop a user seeing prices. Second, future timestamps
/// come from the CORE, not from providers — a provider adding 86,400 seconds per step would draw
/// next week's target on a Saturday or on a market holiday.
/// </para>
/// </summary>
[TestClass]
public class ChartOverlayTests
{
    // 2026-08-21 is a Friday; 22nd/23rd are the weekend.
    private const long FridayBar = 1787270400;   // 2026-08-21T00:00:00Z
    // A provider is handed the chart's own last close so an overlay it anchors cannot disagree with
    // the candles being drawn.
    private const decimal LastClose = 100m;

    private static ChartOverlayCollector Collector(
        IEnumerable<IChartOverlayProvider> providers,
        params string[] holidays)
    {
        var options = Options.Create(new TradingAgentOptions { MarketHolidays = [.. holidays] });
        var calendar = new PsxMarketCalendar(options, NullLogger<PsxMarketCalendar>.Instance);
        return new ChartOverlayCollector(providers, calendar, NullLogger<ChartOverlayCollector>.Instance);
    }

    private static ChartOverlaySet OneLevel(string id, decimal price) =>
        new([new ChartOverlayLevel(id, id, price, ChartOverlayKind.Target)], [], [], []);

    // ── the community edition ─────────────────────────────────────────────────

    [TestMethod]
    public async Task NoProviders_YieldsAnEmptySet()
    {
        var collector = Collector([]);

        Assert.IsFalse(collector.HasProviders);
        var overlays = await collector.CollectAsync("ATRL", "1D", [FridayBar], LastClose, CancellationToken.None);

        // Empty, never null: the client has exactly one rendering path whatever the edition.
        Assert.IsTrue(overlays.IsEmpty);
        Assert.AreEqual(0, overlays.Levels.Count);
        Assert.AreEqual(0, overlays.Series.Count);
        Assert.AreEqual(0, overlays.Markers.Count);
        Assert.AreEqual(0, overlays.Bands.Count);
    }

    // ── merging ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task MultipleProviders_AreMergedInOrder()
    {
        var collector = Collector([
            new StubProvider("first", OneLevel("a", 100m)),
            new StubProvider("second", OneLevel("b", 200m))
        ]);

        var overlays = await collector.CollectAsync("ATRL", "1D", [FridayBar], LastClose, CancellationToken.None);

        Assert.AreEqual(2, overlays.Levels.Count);
        Assert.AreEqual("a", overlays.Levels[0].Id);
        Assert.AreEqual("b", overlays.Levels[1].Id);
    }

    // ── failure isolation: the chart renders regardless ───────────────────────

    [TestMethod]
    public async Task AThrowingProvider_IsDroppedAndTheRestSurvive()
    {
        var collector = Collector([
            new ThrowingProvider("broken"),
            new StubProvider("healthy", OneLevel("kept", 150m))
        ]);

        var overlays = await collector.CollectAsync("ATRL", "1D", [FridayBar], LastClose, CancellationToken.None);

        Assert.AreEqual(1, overlays.Levels.Count);
        Assert.AreEqual("kept", overlays.Levels[0].Id);
    }

    [TestMethod]
    public async Task ASlowProvider_IsDroppedRatherThanDelayingTheChart()
    {
        // Overruns the shared budget by an order of magnitude — a stand-in for a model call that
        // hangs. The request must come back with no overlays rather than block on it.
        var collector = Collector([new SlowProvider("hanging")]);

        var started = DateTime.UtcNow;
        var overlays = await collector.CollectAsync("ATRL", "1D", [FridayBar], LastClose, CancellationToken.None);
        var elapsed = DateTime.UtcNow - started;

        Assert.IsTrue(overlays.IsEmpty);
        Assert.IsTrue(elapsed < ChartOverlayCollector.Budget * 3,
            $"Collection took {elapsed.TotalSeconds:F1}s; the budget is "
            + $"{ChartOverlayCollector.Budget.TotalSeconds}s and a chart request waits on it.");
    }

    [TestMethod]
    public async Task ACallerWalkingAway_IsNotTreatedAsAProviderFault()
    {
        // A cancelled REQUEST (the browser navigated) must propagate, not be swallowed as a provider
        // failure — otherwise the endpoint does the rest of its work for nobody.
        var collector = Collector([new SlowProvider("slow")]);
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            collector.CollectAsync("ATRL", "1D", [FridayBar], LastClose, caller.Token));
    }

    // ── projected timestamps come from the calendar ───────────────────────────

    [TestMethod]
    public void NextSessionTimes_SkipTheWeekend()
    {
        var times = Collector([]).NextSessionTimes(FridayBar, "1D");

        Assert.IsTrue(times.Count > 0);
        var first = DateTimeOffset.FromUnixTimeSeconds(times[0]).UtcDateTime;
        Assert.AreEqual(DayOfWeek.Monday, first.DayOfWeek,
            "The session after a Friday is Monday; a provider stepping +86400 itself would have "
            + "drawn on Saturday.");
        foreach (var t in times)
        {
            var day = DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime.DayOfWeek;
            Assert.IsTrue(day is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
        }
    }

    [TestMethod]
    public void NextSessionTimes_SkipAConfiguredHoliday()
    {
        // Monday the 24th declared a holiday: the projection must step over it to Tuesday.
        var times = Collector([], "2026-08-24").NextSessionTimes(FridayBar, "1D");

        var first = DateTimeOffset.FromUnixTimeSeconds(times[0]).UtcDateTime;
        Assert.AreEqual(new DateTime(2026, 8, 25), first.Date,
            "A configured market holiday must be skipped, or a projected target lands on a day the "
            + "exchange is shut.");
    }

    [TestMethod]
    public void NextSessionTimes_AreEmptyForIntraday()
    {
        // Deliberately not guessed at: stepping an intraday bar forward correctly means modelling
        // session start/end and the Friday break, and an almost-right intraday projection draws bars
        // into a closed market where they read as real data.
        Assert.AreEqual(0, Collector([]).NextSessionTimes(FridayBar, "15m").Count);
    }

    [TestMethod]
    public void IsTradingDay_AgreesWithTheConfiguredCalendar()
    {
        var options = Options.Create(new TradingAgentOptions { MarketHolidays = ["2026-08-24"] });
        var calendar = new PsxMarketCalendar(options, NullLogger<PsxMarketCalendar>.Instance);

        Assert.IsTrue(calendar.IsTradingDay(new DateOnly(2026, 8, 21)));   // Friday
        Assert.IsFalse(calendar.IsTradingDay(new DateOnly(2026, 8, 22)));  // Saturday
        Assert.IsFalse(calendar.IsTradingDay(new DateOnly(2026, 8, 23)));  // Sunday
        Assert.IsFalse(calendar.IsTradingDay(new DateOnly(2026, 8, 24)));  // configured holiday
        Assert.IsTrue(calendar.IsTradingDay(new DateOnly(2026, 8, 25)));   // Tuesday
    }

    [TestMethod]
    public void TheSerializedShape_MatchesWhatTheClientTypeExpects()
    {
        // The endpoint serializes with ASP.NET's web defaults (camelCase). ChartPane reads
        // o.levels / o.series / o.markers / o.bands, so a casing or naming drift here is a silently
        // blank overlay layer on the dashboard rather than an error anywhere.
        var json = System.Text.Json.JsonSerializer.Serialize(
            new ChartOverlaySet(
                [new ChartOverlayLevel("l", "Next target", 142.30m, ChartOverlayKind.Target, 2, true)],
                [new ChartOverlaySeries("s", "Projection", ChartOverlayKind.Projection, true,
                    [new ChartOverlayPoint(FridayBar, 140m)])],
                [new ChartOverlayMarker("m", FridayBar, "T1", ChartOverlayKind.Target)],
                [new ChartOverlayBand("b", "Confidence", ChartOverlayKind.Prediction,
                    [new ChartOverlayBandPoint(FridayBar, 138m, 146m)])]),
            System.Text.Json.JsonSerializerOptions.Web);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        foreach (var key in new[] { "levels", "series", "markers", "bands" })
            Assert.IsTrue(root.TryGetProperty(key, out _), $"missing '{key}'");

        var level = root.GetProperty("levels")[0];
        foreach (var key in new[] { "id", "label", "price", "kind", "weight", "confirmed" })
            Assert.IsTrue(level.TryGetProperty(key, out _), $"level missing '{key}'");

        var point = root.GetProperty("series")[0].GetProperty("points")[0];
        Assert.IsTrue(point.TryGetProperty("time", out _));
        Assert.IsTrue(point.TryGetProperty("value", out _));

        var band = root.GetProperty("bands")[0].GetProperty("points")[0];
        Assert.IsTrue(band.TryGetProperty("lower", out _));
        Assert.IsTrue(band.TryGetProperty("upper", out _));

        // No PascalCase leakage — that is the failure the alert SSE contract already had once.
        Assert.IsFalse(root.TryGetProperty("Levels", out _));
    }

    // ── stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubProvider(string id, ChartOverlaySet set) : IChartOverlayProvider
    {
        public string Id => id;
        public Task<ChartOverlaySet?> GetOverlaysAsync(ChartOverlayRequest r, CancellationToken ct) =>
            Task.FromResult<ChartOverlaySet?>(set);
    }

    private sealed class ThrowingProvider(string id) : IChartOverlayProvider
    {
        public string Id => id;
        public Task<ChartOverlaySet?> GetOverlaysAsync(ChartOverlayRequest r, CancellationToken ct) =>
            throw new InvalidOperationException("model unavailable");
    }

    private sealed class SlowProvider(string id) : IChartOverlayProvider
    {
        public string Id => id;
        public async Task<ChartOverlaySet?> GetOverlaysAsync(ChartOverlayRequest r, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
            return ChartOverlaySet.Empty;
        }
    }
}
