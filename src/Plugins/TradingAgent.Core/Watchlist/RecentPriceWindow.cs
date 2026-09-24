using System.Collections.Concurrent;

namespace TradingAgent.Watchlist;

/// <summary>
/// The recent in-session prices of the symbols a windowed percent trigger is watching, so "sell if it
/// drops 2% from its recent high" has a recent high to measure from.
///
/// <para>
/// <b>Time is counted in TRADING seconds, not wall-clock seconds.</b> Prices are only recorded while
/// the market is open, and a gap between two recordings longer than the caller's <c>maxGap</c> is
/// read as the market having been shut in between and contributes nothing. So a 15-minute window at
/// 09:35 spans yesterday's last minutes and today's first ones, and an overnight gap-down against
/// yesterday's late peak fires at the open. That carry-over is the owner's decision (2026-09-22): the
/// alternative, restarting the window each morning, would let a gap-down alone never fire a
/// protective sell.
/// </para>
///
/// <para>
/// <b>A long in-session gap is read the same way, and that errs toward firing.</b> A feed outage or a
/// paused process longer than <c>maxGap</c> also contributes nothing, so the window then reaches
/// further back than its nominal length and may include an older, higher peak. For a protective sell
/// that is the right direction to be wrong in.
/// </para>
///
/// <para>
/// <b>Retention.</b> Memory only. Per symbol, samples older than <see cref="MaxWindowMinutes"/> of
/// trading time are dropped on every write and the count is capped at <see cref="MaxSamplesPerSymbol"/>;
/// symbols no armed order watches any more are dropped by <see cref="Retain"/>, which the monitor calls
/// on every pass. A restart empties it: the window then refills from the next price, and nothing can
/// fire on a peak that was never observed.
/// </para>
/// </summary>
public sealed class RecentPriceWindow
{
    /// <summary>The longest window an order may ask for, and therefore how much history is kept.</summary>
    public const int MaxWindowMinutes = 240;

    /// <summary>
    /// Recordings closer together than this (in trading time) share one bucket, which keeps its high
    /// and its low. So tick-rate nudges cost no more memory than a slow monitor, and a full
    /// <see cref="MaxWindowMinutes"/> window fits under <see cref="MaxSamplesPerSymbol"/>.
    /// </summary>
    public const int BucketSeconds = 10;

    /// <summary>Hard cap per symbol, whatever happens to the clock.</summary>
    public const int MaxSamplesPerSymbol = 2000;

    /// <summary>
    /// The longest gap between two recordings still counted as trading time, for a monitor running
    /// every <paramref name="monitorIntervalSeconds"/>: two passes, and never under five minutes.
    /// Anything longer is read as the market having been shut in between.
    /// </summary>
    public static TimeSpan MaxGapFor(int monitorIntervalSeconds) =>
        TimeSpan.FromSeconds(Math.Max(2 * Math.Clamp(monitorIntervalSeconds, 30, 3600), 300));

    private readonly ConcurrentDictionary<string, Series> _series = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records one observed price. Call only while the market is open: a price recorded while it is
    /// shut would be counted as trading time.
    /// </summary>
    public void Record(string symbol, decimal price, DateTime utcNow, TimeSpan maxGap)
    {
        if (string.IsNullOrWhiteSpace(symbol) || price <= 0) return;
        var series = _series.GetOrAdd(symbol, _ => new Series());
        lock (series) series.Add(price, utcNow, maxGap);
    }

    /// <summary>
    /// The highest (<paramref name="highest"/>) or lowest price observed in the last
    /// <paramref name="windowMinutes"/> of trading time, or null when nothing has been observed.
    /// </summary>
    public decimal? Extreme(string symbol, int windowMinutes, bool highest)
    {
        if (windowMinutes <= 0 || !_series.TryGetValue(symbol, out var series)) return null;
        lock (series) return series.Extreme(windowMinutes, highest);
    }

    /// <summary>Drops every symbol not in <paramref name="watched"/>.</summary>
    public void Retain(IReadOnlySet<string> watched)
    {
        foreach (var symbol in _series.Keys)
            if (!watched.Contains(symbol))
                _series.TryRemove(symbol, out _);
    }

    private sealed class Series
    {
        private readonly List<(double At, decimal High, decimal Low)> _samples = [];
        private double _clock;
        private DateTime? _lastUtc;

        public void Add(decimal price, DateTime utcNow, TimeSpan maxGap)
        {
            if (_lastUtc is { } last)
            {
                var gap = (utcNow - last).TotalSeconds;
                if (gap > 0 && gap <= maxGap.TotalSeconds) _clock += gap;
            }

            // Never move the recorded time backwards: an out-of-order call would otherwise make the
            // next gap look long and drop a real interval.
            if (_lastUtc is null || utcNow > _lastUtc) _lastUtc = utcNow;

            if (_samples.Count > 0 && _clock - _samples[^1].At < BucketSeconds)
            {
                var bucket = _samples[^1];
                _samples[^1] = (bucket.At, Math.Max(bucket.High, price), Math.Min(bucket.Low, price));
            }
            else
            {
                _samples.Add((_clock, price, price));
            }

            var horizon = _clock - MaxWindowMinutes * 60d;
            var stale = _samples.FindIndex(s => s.At >= horizon);
            if (stale > 0) _samples.RemoveRange(0, stale);
            if (_samples.Count > MaxSamplesPerSymbol)
                _samples.RemoveRange(0, _samples.Count - MaxSamplesPerSymbol);
        }

        public decimal? Extreme(int windowMinutes, bool highest)
        {
            var from = _clock - windowMinutes * 60d;
            decimal? best = null;
            for (var i = _samples.Count - 1; i >= 0 && _samples[i].At >= from; i--)
            {
                var p = highest ? _samples[i].High : _samples[i].Low;
                if (best is not { } b || (highest ? p > b : p < b)) best = p;
            }
            return best;
        }
    }
}
