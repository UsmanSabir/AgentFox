using System.Collections.Concurrent;

namespace TradingAgent.AhlAnalytics;

/// <summary>
/// Per-symbol, single-flight cache for daily AHL candles. Kept separate from the HTTP client so the
/// concurrency and failure-caching rules can be tested without a broker login or a real portal.
/// </summary>
internal sealed class AhlDailyCandleCache
{
    internal static readonly TimeSpan ShortHistoryRetryAfter = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _clock;

    public AhlDailyCandleCache(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<AhlCandle>> GetAsync(
        string symbol,
        TimeSpan ttl,
        Func<CancellationToken, Task<IReadOnlyList<AhlCandle>>> loader,
        CancellationToken ct = default,
        int minimumCandles = 0)
    {
        if (TryGetFresh(symbol, ttl, minimumCandles, out var cached)) return cached;

        var gate = _gates.GetOrAdd(symbol, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (TryGetFresh(symbol, ttl, minimumCandles, out cached)) return cached;

            var loaded = await loader(ct);
            // Never cache an outage or a rate-limit response masquerading as an empty series.
            if (loaded.Count > 0)
                _entries[symbol] = new Entry(_clock.GetUtcNow(), loaded);
            return loaded;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetFresh(string symbol, TimeSpan ttl, int minimumCandles, out IReadOnlyList<AhlCandle> candles)
    {
        if (_entries.TryGetValue(symbol, out var entry))
        {
            // A short response is not proof of listing age. Bound retries for callers asking for
            // deeper history without changing the normal TTL or bypassing single-flight loading.
            if (entry.Candles.Count < minimumCandles && ttl > ShortHistoryRetryAfter)
                ttl = ShortHistoryRetryAfter;
            if (_clock.GetUtcNow() - entry.StoredAt < ttl)
            {
                candles = entry.Candles;
                return true;
            }
        }

        candles = [];
        return false;
    }

    private sealed record Entry(DateTimeOffset StoredAt, IReadOnlyList<AhlCandle> Candles);
}
