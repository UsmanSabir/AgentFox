using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Persistence;
using TradingAgent.Research;

namespace TradingAgent.Analysis;

/// <summary>
/// Loads a symbol's candles and produces the deterministic technical read — once, for every caller.
///
/// <para>
/// Extracted from <c>AnalyzeCandlesTool</c> when the chart endpoint needed the same thing. Both now
/// call this, so the chart cannot drift from what the agent says: the levels drawn on screen are the
/// same objects the specialist quotes. The alternative — a second implementation behind the endpoint —
/// would have been two sources of truth for support and resistance.
/// </para>
///
/// <para>
/// Nothing here is opinionated about presentation. It returns the analyzed series plus the snapshots,
/// and each caller projects what it needs.
/// </para>
/// </summary>
public sealed class CandleAnalysisService
{
    private readonly PsxDataClient _dataClient;
    private readonly CandleHistoryProvider _history;
    private readonly ITradingRepository _repository;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<CandleAnalysisService> _logger;

    public CandleAnalysisService(
        PsxDataClient dataClient,
        CandleHistoryProvider history,
        ITradingRepository repository,
        IOptions<TradingAgentOptions> options,
        ILogger<CandleAnalysisService> logger)
    {
        _dataClient = dataClient;
        _history = history;
        _repository = repository;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Analyzes <paramref name="symbol"/> at <paramref name="intervalMinutes"/>
    /// (<see cref="PsxCandle.DailyIntervalMinutes"/> or an intraday width).
    /// </summary>
    /// <exception cref="CandleAnalysisException">
    /// No usable candles exist. Thrown rather than returned as an empty result because every caller
    /// has to explain the failure to a human, and the reason differs by interval — a missing daily
    /// series usually means a bad ticker, a missing intraday one usually means the market has not
    /// traded it today.
    /// </exception>
    public async Task<CandleAnalysis> AnalyzeAsync(
        string symbol,
        int intervalMinutes = PsxCandle.DailyIntervalMinutes,
        int? lookbackBars = null,
        bool includeLive = true,
        CancellationToken ct = default)
    {
        symbol = PsxDataClient.NormalizeStockSymbol(symbol);
        var scan = _options.Value.Scan;
        var lookback = Math.Clamp(lookbackBars ?? scan.LookbackDays, 5, 5000);
        var technicalOptions = TechnicalOptions.From(scan);

        // Weekly structure needs a far deeper window than the daily read, so ask for whichever is
        // larger. The archive serves it locally; only missing dates reach the portal.
        var weeklySessions = Math.Clamp(scan.WeeklyLookbackWeeks, 12, 600) * 6;
        var sessionsWanted = Math.Max(lookback, weeklySessions);

        var historyTask = _history.GetDailyAsync([symbol], sessionsWanted, includeLive, ct);
        var quoteTask = _dataClient.GetQuoteSummaryAsync(symbol, ct);
        await Task.WhenAll(historyTask, quoteTask);

        var history = await historyTask;
        var quote = await quoteTask;

        if (!history.Series.TryGetValue(symbol, out var fullDaily) || fullDaily.Count == 0)
            throw new CandleAnalysisException(
                $"No candles were returned for {symbol}. " +
                string.Join(" ", history.Warnings.DefaultIfEmpty(
                    "Verify the ticker is listed on the PSX.")));

        var warnings = history.Warnings.ToList();

        // The DAILY read stays scoped to the requested lookback — widening it silently would move
        // every level the caller asked about. Weekly resampling uses the full archived series.
        var dailyCandles = fullDaily.TakeLast(lookback).ToList();
        var daily = TechnicalAnalyzer.Analyze(
            symbol, dailyCandles, technicalOptions, quote.High52Week, quote.Low52Week);
        var multi = MultiTimeframeAnalyzer.Analyze(
            symbol, fullDaily, technicalOptions, scan.ConfluenceTolerancePercent,
            quote.High52Week, quote.Low52Week);

        var sourceUrls = _dataClient.CandleSourceUrls().ToList();

        if (intervalMinutes >= PsxCandle.DailyIntervalMinutes)
        {
            return new CandleAnalysis
            {
                Symbol            = symbol,
                IntervalMinutes   = PsxCandle.DailyIntervalMinutes,
                Candles           = dailyCandles,
                Snapshot          = daily,
                Daily             = daily,
                Multi             = multi,
                WeeklyCandles     = CandleResampler.ToWeekly(fullDaily),
                SessionsAvailable = fullDaily.Count,
                Quote             = quote,
                RetrievedAtUtc    = history.RetrievedAtUtc,
                SourceUrls        = sourceUrls,
                Warnings          = warnings
            };
        }

        var intraday = await LoadIntradayAsync(symbol, intervalMinutes, warnings, ct);
        if (intraday.Count == 0)
            throw new CandleAnalysisException(
                $"No intraday trades are available for {symbol} at " +
                $"{PsxDataClient.IntervalLabel(intervalMinutes)}. The PSX tick feed covers the current " +
                "session only, so this happens before the open or when the symbol has not traded today " +
                "and no earlier session has been archived. Use interval '1D' instead.");

        return new CandleAnalysis
        {
            Symbol            = symbol,
            IntervalMinutes   = intervalMinutes,
            Candles           = intraday,
            Snapshot          = TechnicalAnalyzer.Analyze(symbol, intraday, technicalOptions),
            Daily             = daily,
            Multi             = multi,
            WeeklyCandles     = CandleResampler.ToWeekly(fullDaily),
            SessionsAvailable = fullDaily.Count,
            Quote             = quote,
            RetrievedAtUtc    = DateTime.UtcNow,
            SourceUrls        =
                [.. sourceUrls, $"{_options.Value.PsxDataBaseUrl.TrimEnd('/')}/timeseries/int/{symbol}"],
            Warnings          = warnings
        };
    }

    /// <summary>
    /// Builds the intraday series: archived bars from earlier sessions, plus the current session
    /// rebuilt from the live tick tape. Today is always recomputed rather than read from the archive,
    /// so a bar that was still forming when it was last saved is never treated as final. Completed
    /// bars are written back (when archiving is enabled), which is how multi-session intraday history
    /// accumulates at all — PSX serves the current session only.
    /// </summary>
    private async Task<IReadOnlyList<PsxCandle>> LoadIntradayAsync(
        string symbol, int interval, List<string> warnings, CancellationToken ct)
    {
        var scan = _options.Value.Scan;

        var ticks = await _dataClient.GetIntradayTicksAsync(symbol, ct);
        var live = PsxDataClient.AggregateTicks(symbol, ticks, interval);

        var earliestLive = live.Count > 0 ? live[0].BucketStartUtc : null;
        IReadOnlyList<PsxCandle> archived = [];
        try
        {
            archived = await _repository.GetIntradayBarsAsync(
                symbol, interval, Math.Clamp(scan.IntradayLookbackBars, 20, 5000), earliestLive, ct);
        }
        catch (Exception ex)
        {
            // The archive is an enhancement; losing it degrades history, it must not fail the analysis.
            _logger.LogWarning(ex, "[CandleAnalysis] Intraday archive read failed for {Symbol}.", symbol);
            warnings.Add($"Archived intraday history could not be read ({ex.Message}); " +
                         "analysis uses the current session only.");
        }

        if (scan.ArchiveIntradayBars && live.Count > 0)
        {
            try
            {
                await _repository.SaveIntradayBarsAsync(live, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CandleAnalysis] Intraday archive write failed for {Symbol}.", symbol);
                warnings.Add($"This session's intraday bars could not be archived ({ex.Message}).");
            }
        }

        var merged = archived.Concat(live).OrderBy(b => b.SortKeyUtc).ToList();
        if (merged.Count == 0) return merged;

        // Session count, not bar count, is what makes intraday levels meaningful: one session of 5m
        // bars is 76 bars and still only one day's range, so a bar-count check would stay silent
        // exactly when the levels are least trustworthy.
        var sessions = merged.Select(b => b.Date).Distinct().Count();
        if (sessions < 3)
            warnings.Add(
                $"Intraday history covers only {sessions} session(s) " +
                $"({merged.Count} {PsxDataClient.IntervalLabel(interval)} bars). PSX publishes no " +
                "historical intraday, so this builds up from the sessions this agent has archived. " +
                "Levels drawn from it are weak — trade the daily levels and use these bars only " +
                "for timing.");

        return merged;
    }
}

/// <summary>The analyzed series plus every read derived from it.</summary>
public sealed record CandleAnalysis
{
    public required string Symbol { get; init; }

    public required int IntervalMinutes { get; init; }

    /// <summary>Human label for <see cref="IntervalMinutes"/>: <c>1D</c>, <c>60m</c>, …</summary>
    public string Interval => PsxDataClient.IntervalLabel(IntervalMinutes);

    /// <summary>The bars the snapshot was computed from, oldest first.</summary>
    public required IReadOnlyList<PsxCandle> Candles { get; init; }

    /// <summary>Technical read of <see cref="Candles"/> — the requested interval.</summary>
    public required TechnicalSnapshot Snapshot { get; init; }

    /// <summary>
    /// Technical read of the DAILY series. Same object as <see cref="Snapshot"/> for a daily request;
    /// for an intraday one it is the higher-timeframe context an intraday entry must be traded against.
    /// </summary>
    public required TechnicalSnapshot Daily { get; init; }

    /// <summary>Daily + weekly confluence: which levels both timeframes recognise, and whether they agree.</summary>
    public required MultiTimeframeView Multi { get; init; }

    /// <summary>Weekly bars resampled from the full daily history.</summary>
    public required IReadOnlyList<PsxCandle> WeeklyCandles { get; init; }

    /// <summary>Daily sessions available in the archive, which may exceed the analyzed window.</summary>
    public required int SessionsAvailable { get; init; }

    public PsxQuoteSummary? Quote { get; init; }
    public required DateTime RetrievedAtUtc { get; init; }
    public IReadOnlyList<string> SourceUrls { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>No usable candles exist for the request; the message is safe to show a user.</summary>
public sealed class CandleAnalysisException(string message) : Exception(message);
