using Microsoft.Extensions.Logging;
using TradingAgent.Market;

namespace TradingAgent.Chart;

/// <summary>
/// Asks every registered <see cref="IChartOverlayProvider"/> what it wants drawn, and guarantees the
/// chart renders regardless of what they do.
///
/// <para>
/// Two responsibilities, both of which exist so a provider cannot get them wrong:
/// </para>
/// <list type="number">
///   <item><description>
///     It computes the FUTURE session timestamps a projection needs. A provider left to do this
///     itself would add 86,400 seconds per step and draw next week's target on a Saturday or on a
///     configured market holiday.
///   </description></item>
///   <item><description>
///     It isolates failure. The chart is a read path: a provider that throws, or that takes longer
///     than the budget, is dropped for that request with a logged warning and the chart still
///     renders. A slow model must never be able to stop a user seeing prices.
///   </description></item>
/// </list>
/// </summary>
public sealed class ChartOverlayCollector
{
    /// <summary>
    /// How long ALL providers together may take. Deliberately short: this runs inside a chart
    /// request that a person is waiting on, and no overlay is worth making the price chart feel slow.
    /// </summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many future sessions to offer a projection. Enough for a short-horizon target without
    /// implying the chart can be trusted weeks out; the client widens its visible range to match
    /// whatever is actually returned.
    /// </summary>
    internal const int ProjectionSessions = 10;

    private readonly IReadOnlyList<IChartOverlayProvider> _providers;
    private readonly IMarketCalendar _calendar;
    private readonly ILogger<ChartOverlayCollector> _logger;

    public ChartOverlayCollector(
        IEnumerable<IChartOverlayProvider> providers,
        IMarketCalendar calendar,
        ILogger<ChartOverlayCollector> logger)
    {
        _providers = providers.ToList();
        _calendar = calendar;
        _logger = logger;
    }

    /// <summary>True when nothing is registered — the community edition's normal state.</summary>
    public bool HasProviders => _providers.Count > 0;

    public async Task<ChartOverlaySet> CollectAsync(
        string symbol,
        string interval,
        IReadOnlyList<long> barTimes,
        CancellationToken ct)
    {
        if (_providers.Count == 0 || barTimes.Count == 0)
            return ChartOverlaySet.Empty;

        var request = new ChartOverlayRequest(
            symbol,
            interval,
            barTimes[0],
            barTimes[^1],
            barTimes.Count,
            NextSessionTimes(barTimes[^1], interval));

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);

        var sets = new List<ChartOverlaySet>(_providers.Count);
        foreach (var provider in _providers)
        {
            try
            {
                var set = await provider.GetOverlaysAsync(request, budget.Token);
                if (set is not null && !set.IsEmpty)
                    sets.Add(set);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The CALLER went away (browser navigated). Stop, and let the endpoint handle it.
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "[Trading] Chart overlay provider {Provider} exceeded the {Budget}s budget for "
                    + "{Symbol}; its overlays are omitted from this chart.",
                    provider.Id, Budget.TotalSeconds, symbol);
                break;      // the budget is shared, so the ones after it would fail too
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[Trading] Chart overlay provider {Provider} failed for {Symbol}; its overlays "
                    + "are omitted from this chart.",
                    provider.Id, symbol);
            }
        }

        return sets.Count == 0 ? ChartOverlaySet.Empty : ChartOverlaySet.Merge(sets);
    }

    /// <summary>
    /// The next <see cref="ProjectionSessions"/> trading timestamps after the last bar.
    ///
    /// <para>
    /// Daily and coarser series step one SESSION at a time, skipping weekends and configured
    /// holidays. Intraday series are left empty rather than guessed at: stepping an intraday bar
    /// forward correctly means modelling session start/end and the Friday break, and an
    /// almost-right intraday projection is worse than none — it would draw bars in the middle of a
    /// closed market and read as data.
    /// </para>
    /// </summary>
    internal IReadOnlyList<long> NextSessionTimes(long lastBarTime, string interval)
    {
        if (!IsDailyOrCoarser(interval))
            return [];

        var times = new List<long>(ProjectionSessions);
        var cursor = DateTimeOffset.FromUnixTimeSeconds(lastBarTime).UtcDateTime;
        var timeOfDay = cursor.TimeOfDay;
        var date = DateOnly.FromDateTime(cursor);

        // Bounded independently of ProjectionSessions: a misconfiguration that marks everything a
        // holiday must terminate rather than spin.
        for (var step = 0; step < ProjectionSessions * 6 && times.Count < ProjectionSessions; step++)
        {
            date = date.AddDays(1);
            if (!_calendar.IsTradingDay(date))
                continue;

            times.Add(new DateTimeOffset(
                date.ToDateTime(TimeOnly.MinValue).Add(timeOfDay),
                TimeSpan.Zero).ToUnixTimeSeconds());
        }
        return times;
    }

    private static bool IsDailyOrCoarser(string interval) =>
        interval.Equals("1D", StringComparison.OrdinalIgnoreCase)
        || interval.Equals("1W", StringComparison.OrdinalIgnoreCase)
        || interval.Equals("1M", StringComparison.OrdinalIgnoreCase);
}
