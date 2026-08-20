using TradingAgent.AhlAnalytics;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class AhlDailyCandleCacheTests
{
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

    private static AhlCandle Bar() => new()
    {
        Date = "2026-08-20 16:00:00",
        Open = 100m,
        High = 105m,
        Low = 99m,
        Close = 104m,
        Volume = 1234
    };
}
