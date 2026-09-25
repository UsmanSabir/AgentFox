using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.AhlAnalytics;
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
    private readonly AhlCandleSource _ahl;
    private readonly ITradingRepository _repository;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<CandleAnalysisService> _logger;

    public CandleAnalysisService(
        PsxDataClient dataClient,
        CandleHistoryProvider history,
        AhlCandleSource ahl,
        ITradingRepository repository,
        IOptions<TradingAgentOptions> options,
        ILogger<CandleAnalysisService> logger)
    {
        _dataClient = dataClient;
        _history = history;
        _ahl = ahl;
        _repository = repository;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Analyzes <paramref name="symbol"/> at <paramref name="intervalMinutes"/>
    /// (monthly, weekly, daily, or an intraday width).
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
        bool preferAvailableHistory = false,
        CancellationToken ct = default)
    {
        symbol = PsxDataClient.NormalizeStockSymbol(symbol);
        var scan = _options.Value.Scan;
        var lookback = Math.Clamp(lookbackBars ?? scan.LookbackDays, 5, 5000);
        var technicalOptions = TechnicalOptions.From(scan);

        // Higher-timeframe requests count bars, not daily sessions. Ask the archive for enough daily
        // constituents to fill that request, with headroom for holidays; it returns all it has when
        // the requested history predates the archive.
        var weeklySessions = Math.Clamp(scan.WeeklyLookbackWeeks, 12, 600) * 6;
        var requestedSessions = intervalMinutes switch
        {
            >= CandleInterval.Monthly => Math.Clamp(lookback * 23, 5, 5000),
            >= CandleInterval.Weekly => Math.Clamp(lookback * 6, 5, 5000),
            _ => lookback
        };
        var sessionsWanted = Math.Max(requestedSessions, weeklySessions);

        var historyTask = _history.GetDailyAsync(
            [symbol], sessionsWanted, includeLive,
            allowPortalFallback: !preferAvailableHistory,
            ct: ct);
        // During the first few archived bars, the interactive chart values responsiveness over
        // 52-week portal annotations. The forming/live candle and every deterministic indicator are
        // still computed; only the two optional long-range extrema wait for the normal path.
        var quoteTask = preferAvailableHistory
            ? Task.FromResult(new PsxQuoteSummary { Symbol = symbol })
            : _dataClient.GetQuoteSummaryAsync(symbol, ct);
        await Task.WhenAll(historyTask, quoteTask);

        var history = await historyTask;
        var quote = await quoteTask;

        // THROWN, never defaulted to zeros. An indicator computed from no candles is a number that
        // looks like analysis and is not, and both premium strategy loops catch this per symbol and
        // record it as a coded rejection, so one dataless ticker costs its own review and nothing
        // else's.
        //
        // The fallback sentence names the RECENT LISTING first, because that is the case an operator
        // actually meets and the one the old wording sent them the wrong way on. CONFIRMED 2026-09-11:
        // a stock bought at IPO and genuinely held was reported as "Verify the ticker is listed on the
        // PSX" on every pass — it IS listed, the holding is real, and the exchange simply has not
        // published its history yet. A diagnostic that tells you to check the one thing that is fine
        // is worse than none, and this text reaches the operator: it is interpolated into the run
        // report's rejection and into the exit review's "could not be reviewed".
        if (!history.Series.TryGetValue(symbol, out var fullDaily) || fullDaily.Count == 0)
            throw new CandleAnalysisException(
                $"No price history is available for {symbol}. " +
                string.Join(" ", history.Warnings.DefaultIfEmpty(
                    "A recently listed stock has none until the exchange publishes it, and nothing "
                    + "can be analysed until then; otherwise verify the ticker is listed on the PSX.")));

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

        var weeklyCandles = CandleResampler.ToWeekly(fullDaily);

        if (intervalMinutes >= PsxCandle.DailyIntervalMinutes)
        {
            var requestedCandles = intervalMinutes switch
            {
                >= CandleInterval.Monthly => CandleResampler.ToMonthly(fullDaily).TakeLast(lookback).ToList(),
                >= CandleInterval.Weekly => weeklyCandles.TakeLast(lookback).ToList(),
                _ => dailyCandles
            };
            var snapshot = intervalMinutes == PsxCandle.DailyIntervalMinutes
                ? daily
                : TechnicalAnalyzer.Analyze(
                    symbol, requestedCandles, technicalOptions, quote.High52Week, quote.Low52Week);

            return new CandleAnalysis
            {
                Symbol            = symbol,
                IntervalMinutes   = intervalMinutes,
                Candles           = requestedCandles,
                Snapshot          = snapshot,
                Daily             = daily,
                Multi             = multi,
                WeeklyCandles     = weeklyCandles,
                SessionsAvailable = fullDaily.Count,
                Quote             = quote,
                RetrievedAtUtc    = history.RetrievedAtUtc,
                SourceUrls        = sourceUrls,
                Warnings          = warnings
            };
        }

        var (intraday, intradaySources) = await LoadIntradayAsync(symbol, intervalMinutes, warnings, ct);
        if (intraday.Count == 0)
            throw new CandleAnalysisException(
                $"No intraday trades are available for {symbol} at " +
                $"{PsxDataClient.IntervalLabel(intervalMinutes)}. This happens before the open or when the " +
                "symbol has not traded today, while neither the AHL research portal nor the archive holds " +
                "an earlier session for it. Use interval '1D' instead.");

        return new CandleAnalysis
        {
            Symbol            = symbol,
            IntervalMinutes   = intervalMinutes,
            Candles           = intraday,
            Snapshot          = TechnicalAnalyzer.Analyze(symbol, intraday, technicalOptions),
            Daily             = daily,
            Multi             = multi,
            WeeklyCandles     = weeklyCandles,
            SessionsAvailable = fullDaily.Count,
            Quote             = quote,
            RetrievedAtUtc    = DateTime.UtcNow,
            SourceUrls        = [.. sourceUrls, .. intradaySources],
            Warnings          = warnings
        };
    }

    /// <summary>
    /// Builds the intraday series: earlier sessions from the archive, plus the current session.
    ///
    /// <para>
    /// <b>Today</b> comes from the AHL research portal's one-minute bars when the portal is already
    /// signed in, and from the PSX tick tape otherwise. Both are raw traded prices: the portal's
    /// adjustment factor for the current session is 1.0, the same fact that lets its daily series
    /// carry a live bar. Today is always recomputed rather than read from the archive, so a bar still
    /// forming when it was last saved is never treated as final, and completed bars are written back.
    /// </para>
    ///
    /// <para>
    /// <b>Earlier sessions</b> come from the archive. A session the archive lacks is filled from the
    /// portal's five-day window, which is what lets a fresh install, or a day the agent was down, still
    /// show several sessions. The fill is skipped when those bars carry sub-paisa prices, the portal's
    /// corporate-action adjustment: they would sit on a different scale from the raw archive, and a
    /// series must not change scale partway. Filled bars are never archived, for the same reason
    /// adjusted daily bars never enter <c>daily_bars</c>.
    /// </para>
    /// </summary>
    private async Task<(IReadOnlyList<PsxCandle> Bars, IReadOnlyList<string> SourceUrls)> LoadIntradayAsync(
        string symbol, int interval, List<string> warnings, CancellationToken ct)
    {
        var scan = _options.Value.Scan;
        var now = DateTime.UtcNow;
        var sourceUrls = new List<string>();

        IReadOnlyList<PsxCandle> live;
        var portalToday = await _ahl.GetTodayMinuteBarsAsync(symbol, ct);
        if (portalToday.Count > 0)
        {
            live = CandleResampler.ToIntraday(portalToday, interval, now);
            sourceUrls.Add($"AHL analytics /intraday/{symbol}/1D");
        }
        else
        {
            var ticks = await _dataClient.GetIntradayTicksAsync(symbol, ct);
            live = PsxDataClient.AggregateTicks(symbol, ticks, interval);
            sourceUrls.Add($"{_options.Value.PsxDataBaseUrl.TrimEnd('/')}/timeseries/int/{symbol}");
        }

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

        var filled = await FillMissingSessionsAsync(symbol, interval, archived, live, now, warnings, ct);
        if (filled.Count > 0) sourceUrls.Add($"AHL analytics /intraday/{symbol}/5D");

        // Earlier sessions share one bar budget whatever their source, newest kept, as the archive
        // read alone always did.
        var budget = Math.Clamp(scan.IntradayLookbackBars, 20, 5000);
        var earlier = archived.Concat(filled).OrderBy(b => b.SortKeyUtc).ToList();
        if (earlier.Count > budget) earlier = earlier.GetRange(earlier.Count - budget, budget);

        var merged = earlier.Concat(live).OrderBy(b => b.SortKeyUtc).ToList();
        if (merged.Count == 0) return (merged, sourceUrls);

        // Session count, not bar count, is what makes intraday levels meaningful: one session of 5m
        // bars is 76 bars and still only one day's range, so a bar-count check would stay silent
        // exactly when the levels are least trustworthy.
        var sessions = merged.Select(b => b.Date).Distinct().Count();
        if (sessions < 3)
            warnings.Add(
                $"Intraday history covers only {sessions} session(s) " +
                $"({merged.Count} {PsxDataClient.IntervalLabel(interval)} bars). " +
                "Levels drawn from it are weak — trade the daily levels and use these bars only " +
                "for timing.");

        return (merged, sourceUrls);
    }

    /// <summary>
    /// Earlier sessions the archive does not hold, from the portal's five-day window, at
    /// <paramref name="interval"/>. Empty when the portal is not signed in, has nothing, or its bars
    /// look adjusted (see <see cref="LoadIntradayAsync"/>).
    /// </summary>
    private async Task<IReadOnlyList<PsxCandle>> FillMissingSessionsAsync(
        string symbol, int interval, IReadOnlyList<PsxCandle> archived, IReadOnlyList<PsxCandle> live,
        DateTime now, List<string> warnings, CancellationToken ct)
    {
        var prior = await _ahl.GetPriorSessionMinuteBarsAsync(symbol, ct);
        if (prior.Count == 0) return [];

        var held = archived.Select(b => b.Date).Concat(live.Select(b => b.Date)).ToHashSet();
        var missing = prior.Where(b => !held.Contains(b.Date)).ToList();
        if (missing.Count == 0) return [];

        if (AhlCandleSource.LooksAdjusted(missing))
        {
            warnings.Add(
                $"Earlier intraday sessions for {symbol} were not filled from the AHL research portal: its " +
                "bars are corporate-action adjusted, which would put them on a different price scale from " +
                "the archived sessions.");
            return [];
        }

        return CandleResampler.ToIntraday(missing, interval, now);
    }
}

/// <summary>The analyzed series plus every read derived from it.</summary>
public sealed record CandleAnalysis
{
    public required string Symbol { get; init; }

    public required int IntervalMinutes { get; init; }

    /// <summary>Human label for <see cref="IntervalMinutes"/>: <c>1M</c>, <c>1W</c>, <c>1D</c>, …</summary>
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
