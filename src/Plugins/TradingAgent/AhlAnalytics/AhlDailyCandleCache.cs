using System.Collections.Concurrent;

namespace TradingAgent.AhlAnalytics;

/// <summary>
/// Per-symbol, single-flight cache for daily AHL candles. Kept separate from the HTTP client so the
/// concurrency and failure-caching rules can be tested without a broker login or a real portal.
/// </summary>
internal sealed class AhlDailyCandleCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<AhlCandle>> GetAsync(
        string symbol,
        TimeSpan ttl,
        Func<CancellationToken, Task<IReadOnlyList<AhlCandle>>> loader,
        CancellationToken ct = default)
    {
        if (TryGetFresh(symbol, ttl, out var cached)) return cached;

        var gate = _gates.GetOrAdd(symbol, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (TryGetFresh(symbol, ttl, out cached)) return cached;

            var loaded = await loader(ct);
            // Never cache an outage or a rate-limit response masquerading as an empty series.
            if (loaded.Count > 0)
                _entries[symbol] = new Entry(DateTimeOffset.UtcNow, loaded);
            return loaded;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetFresh(string symbol, TimeSpan ttl, out IReadOnlyList<AhlCandle> candles)
    {
        if (_entries.TryGetValue(symbol, out var entry)
            && DateTimeOffset.UtcNow - entry.StoredAt < ttl)
        {
            candles = entry.Candles;
            return true;
        }

        candles = [];
        return false;
    }

    private sealed record Entry(DateTimeOffset StoredAt, IReadOnlyList<AhlCandle> Candles);
}
