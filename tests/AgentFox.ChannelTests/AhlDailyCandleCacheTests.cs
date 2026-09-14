using TradingAgent.AhlAnalytics;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class AhlDailyCandleCacheTests
{
    [TestMethod]
    public async Task ShortResponseExpiresAtRetryBoundary_AndCanRecoverToFullDepth()
    {
        var clock = new TestClock();
        var cache = new AhlDailyCandleCache(clock);
        var loads = 0;
        Task<IReadOnlyList<AhlCandle>> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<IReadOnlyList<AhlCandle>>(
                Enumerable.Range(0, loads == 1 ? 14 : 260).Select(_ => Bar()).ToList());
        }

        await cache.GetAsync("NEW", TimeSpan.FromHours(12), Load, minimumCandles: 260);
        clock.Now += AhlDailyCandleCache.ShortHistoryRetryAfter - TimeSpan.FromSeconds(1);
        await cache.GetAsync("NEW", TimeSpan.FromHours(12), Load, minimumCandles: 260);
        Assert.AreEqual(1, loads);
        clock.Now += TimeSpan.FromSeconds(1);
        var recovered = await cache.GetAsync("NEW", TimeSpan.FromHours(12), Load, minimumCandles: 260);
        Assert.AreEqual(2, loads);
        Assert.AreEqual(260, recovered.Count);
        clock.Now += TimeSpan.FromHours(1);
        await cache.GetAsync("NEW", TimeSpan.FromHours(12), Load, minimumCandles: 260);
        Assert.AreEqual(2, loads, "Full history keeps the configured TTL.");
    }

    [TestMethod]
    public async Task FullHistoryContainingCurrentSessionExpiresAtRetryBoundary()
    {
        var clock = new TestClock();
        var cache = new AhlDailyCandleCache(clock);
        var loads = 0;
        Task<IReadOnlyList<AhlCandle>> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<IReadOnlyList<AhlCandle>>(
                Enumerable.Range(0, 260)
                    .Select(i => Bar(i == 259 ? "2026-09-14 16:00:00" : "2026-09-11 16:00:00"))
                    .ToList());
        }

        await cache.GetAsync("LIVE", TimeSpan.FromHours(12), Load, minimumCandles: 260);
        clock.Now += AhlDailyCandleCache.ShortHistoryRetryAfter - TimeSpan.FromSeconds(1);
        await cache.GetAsync("LIVE", TimeSpan.FromHours(12), Load, minimumCandles: 260);
        Assert.AreEqual(1, loads, "A forming daily response still coalesces nearby reads.");

        clock.Now += TimeSpan.FromSeconds(1);
        await cache.GetAsync("LIVE", TimeSpan.FromHours(12), Load, minimumCandles: 260);
        Assert.AreEqual(2, loads, "Today's forming bar must not stay frozen for twelve hours.");
    }

    [TestMethod]
    public async Task FullSettledHistoryKeepsConfiguredTtl()
    {
        var clock = new TestClock();
        var cache = new AhlDailyCandleCache(clock);
        var loads = 0;
        Task<IReadOnlyList<AhlCandle>> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<IReadOnlyList<AhlCandle>>(
                Enumerable.Range(0, 260).Select(_ => Bar("2026-09-11 16:00:00")).ToList());
        }

        await cache.GetAsync("SETTLED", TimeSpan.FromHours(12), Load, minimumCandles: 260);
        clock.Now += TimeSpan.FromHours(1);
        await cache.GetAsync("SETTLED", TimeSpan.FromHours(12), Load, minimumCandles: 260);

        Assert.AreEqual(1, loads, "Only a forming or short series gets the shortened TTL.");
    }

    [TestMethod]
    public void PktSessionDateUsesTheExchangeDateRatherThanTheUtcDate()
    {
        var beforePktMidnight = new DateTimeOffset(2026, 9, 14, 18, 59, 59, TimeSpan.Zero);
        var afterPktMidnight = beforePktMidnight.AddSeconds(1);

        Assert.AreEqual(new DateOnly(2026, 9, 14), AhlCandleSource.PktSessionDate(beforePktMidnight));
        Assert.AreEqual(new DateOnly(2026, 9, 15), AhlCandleSource.PktSessionDate(afterPktMidnight));
    }

    [TestMethod]
    public async Task ShortHistoryNeverExtendsAShorterConfiguredTtl()
    {
        var clock = new TestClock();
        var cache = new AhlDailyCandleCache(clock);
        var loads = 0;
        Task<IReadOnlyList<AhlCandle>> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<IReadOnlyList<AhlCandle>>([Bar()]);
        }
        await cache.GetAsync("NEW", TimeSpan.FromMinutes(1), Load, minimumCandles: 260);
        clock.Now += TimeSpan.FromMinutes(1);
        await cache.GetAsync("NEW", TimeSpan.FromMinutes(1), Load, minimumCandles: 260);
        Assert.AreEqual(2, loads);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 14, 5, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    public async Task RepeatedSymbolRead_UsesOnePortalLoad()
    {
        var cache = new AhlDailyCandleCache();
        var loads = 0;

        Task<IReadOnlyList<AhlCandle>> Load(CancellationToken _) 
        {
            loads++;
            return Task.FromResult<IReadOnlyList<AhlCandle>>([Bar()]);
        }

        await cache.GetAsync("OGDC", TimeSpan.FromHours(12), Load);
        await cache.GetAsync("ogdc", TimeSpan.FromHours(12), Load);

        Assert.AreEqual(1, loads, "Symbols are case-insensitive and must share the daily cache.");
    }

    [TestMethod]
    public async Task ConcurrentColdReads_AreSingleFlightPerSymbol()
    {
        var cache = new AhlDailyCandleCache();
        var loads = 0;

        async Task<IReadOnlyList<AhlCandle>> Load(CancellationToken ct)
        {
            Interlocked.Increment(ref loads);
            await Task.Delay(20, ct);
            return [Bar()];
        }

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => cache.GetAsync("PPL", TimeSpan.FromHours(12), Load)));

        Assert.AreEqual(1, loads, "Concurrent monitor/UI reads must not duplicate the upstream GET.");
    }

    [TestMethod]
    public async Task EmptyFailureResponse_IsNotCached()
    {
        var cache = new AhlDailyCandleCache();
        var loads = 0;

        Task<IReadOnlyList<AhlCandle>> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult<IReadOnlyList<AhlCandle>>([]);
        }

        await cache.GetAsync("MARI", TimeSpan.FromHours(12), Load);
        await cache.GetAsync("MARI", TimeSpan.FromHours(12), Load);

        Assert.AreEqual(2, loads, "A transient empty/rate-limited result must recover on the next read.");
    }

    private static AhlCandle Bar(string date = "2026-08-20 16:00:00") => new()
    {
        Date = date,
        Open = 100m,
        High = 105m,
        Low = 99m,
        Close = 104m,
        Volume = 1234
    };
}
