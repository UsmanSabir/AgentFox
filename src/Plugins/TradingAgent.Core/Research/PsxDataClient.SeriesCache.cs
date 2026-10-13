using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace TradingAgent.Research;

/// <summary>
/// The portal's time series, cached in memory.
///
/// <para>
/// <b>Why, measured 2026-10-13.</b> Every candle analysis asked for a quote summary, and every summary was two
/// uncached requests (<c>/timeseries/eod</c> and <c>/timeseries/int</c>) — about 300 an hour from one agent, and
/// five agents sharing one hosting machine made ~1,500 an hour from a single address. The portal stopped
/// answering that address on 2026-10-12 (its data pages close the connection with no reply) while still serving
/// a home connection. An analysis only reads the 52-week range from the summary, which comes from the end-of-day
/// series alone.
/// </para>
///
/// <para>
/// <b>Lifetimes.</b> End-of-day: <see cref="EodSeriesTtl"/>; it gains one row a day. Intraday:
/// <see cref="IntradaySeriesTtl"/>. A failure is remembered for <see cref="SeriesFailureTtl"/> per series, so a
/// portal that is refusing is asked once in that window instead of on every analysis, and is reported as
/// <see cref="PsxPortalUnavailableException"/> meanwhile. Concurrent callers share one request.
/// </para>
///
/// <para>
/// <b>Retention.</b> Memory only, one entry per series path, at most <see cref="MaxCachedSeries"/>: expired
/// entries are swept on every new entry and the oldest are dropped past the cap. A restart drops all of it.
/// </para>
/// </summary>
public sealed partial class PsxDataClient
{
    internal static readonly TimeSpan EodSeriesTtl = TimeSpan.FromHours(2);
    internal static readonly TimeSpan IntradaySeriesTtl = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan SeriesFailureTtl = TimeSpan.FromMinutes(5);
    internal const int MaxCachedSeries = 1000;

    private readonly ConcurrentDictionary<string, SeriesEntry> _series = new(StringComparer.OrdinalIgnoreCase);

    private sealed class SeriesEntry(DateTime createdUtc)
    {
        public DateTime CreatedUtc { get; } = createdUtc;
        /// <summary>Unset (max) while the request is in flight, so concurrent callers share it.</summary>
        public DateTime ExpiresUtc = DateTime.MaxValue;
        public Lazy<Task<List<SeriesPoint>>> Fetch = null!;
    }

    private async Task<List<SeriesPoint>> CachedSeriesAsync(string path, TimeSpan ttl, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var created = false;
        var entry = _series.AddOrUpdate(path,
            _ => { created = true; return NewSeriesEntry(path, ttl, now); },
            (_, old) =>
            {
                if (old.ExpiresUtc > now) return old;
                created = true;
                return NewSeriesEntry(path, ttl, now);
            });
        if (created) TrimSeriesCache(now);

        var fetch = entry.Fetch.Value;
        // A remembered failure is reported as such, so a caller does not log the portal's refusal again on every
        // analysis while nothing is being asked.
        if (!created && fetch.IsFaulted && entry.ExpiresUtc != DateTime.MaxValue)
            throw new PsxPortalUnavailableException(path, entry.ExpiresUtc, fetch.Exception!.GetBaseException());

        try
        {
            return await fetch.WaitAsync(ct);
        }
        catch (Exception ex) when (!created && ex is not OperationCanceledException)
        {
            // Joined someone else's request that failed: same refusal, already reported by its owner.
            throw new PsxPortalUnavailableException(path, entry.ExpiresUtc, ex);
        }
    }

    private SeriesEntry NewSeriesEntry(string path, TimeSpan ttl, DateTime now)
    {
        var entry = new SeriesEntry(now);
        entry.Fetch = new Lazy<Task<List<SeriesPoint>>>(() => FetchAndStampAsync(entry, path, ttl));
        return entry;
    }

    private async Task<List<SeriesPoint>> FetchAndStampAsync(SeriesEntry entry, string path, TimeSpan ttl)
    {
        try
        {
            // Not the caller's token: other callers share this request, and one leaving must not cancel theirs.
            var points = await FetchSeriesAsync(path, CancellationToken.None);
            entry.ExpiresUtc = DateTime.UtcNow + ttl;
            return points;
        }
        catch (Exception ex)
        {
            entry.ExpiresUtc = DateTime.UtcNow + SeriesFailureTtl;
            ExceptionDispatchInfo.Capture(ex).Throw();
            throw;
        }
    }

    private void TrimSeriesCache(DateTime now)
    {
        foreach (var (key, value) in _series)
            if (value.ExpiresUtc <= now) _series.TryRemove(new KeyValuePair<string, SeriesEntry>(key, value));
        var excess = _series.Count - MaxCachedSeries;
        if (excess <= 0) return;
        foreach (var (key, value) in _series.OrderBy(e => e.Value.CreatedUtc).Take(excess).ToList())
            _series.TryRemove(new KeyValuePair<string, SeriesEntry>(key, value));
    }
}

/// <summary>
/// The portal refused this series within the last few minutes and has not been asked again; see
/// <see cref="PsxDataClient"/>'s series cache. Carries the original refusal.
/// </summary>
public sealed class PsxPortalUnavailableException(string path, DateTime retryAfterUtc, Exception inner)
    : Exception($"The PSX data portal did not answer {path}; it is asked again after {retryAfterUtc:HH:mm} UTC. ({inner.Message})", inner)
{
    public DateTime RetryAfterUtc { get; } = retryAfterUtc;
}
