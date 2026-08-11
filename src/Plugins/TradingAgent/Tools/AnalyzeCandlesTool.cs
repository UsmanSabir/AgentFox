using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFox.Plugins.Interfaces;
using AgentFox.Plugins.Research;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Analysis;
using TradingAgent.Config;
using TradingAgent.Persistence;
using TradingAgent.Research;

namespace TradingAgent.Tools;

/// <summary>
/// Reads one PSX symbol's daily candles and returns the deterministic technical picture: support and
/// resistance levels drawn from swing pivots and range extremes, where the last price sits between
/// them, standard indicators (SMA20/50, RSI14, ATR14, volume vs average), and a level-anchored
/// entry/stop/target with its reward:risk ratio.
///
/// Every number is computed from the fetched candles by <see cref="TechnicalAnalyzer"/> — no model
/// is involved in producing them, so the specialist can quote them directly. Use this to answer
/// "is X near support or resistance right now"; use scan_watchlist to find such stocks across the
/// configured symbol list.
/// </summary>
public sealed class AnalyzeCandlesTool : BaseTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly PsxDataClient _dataClient;
    private readonly CandleHistoryProvider _history;
    private readonly ITradingRepository _repository;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<AnalyzeCandlesTool> _logger;

    public AnalyzeCandlesTool(
        PsxDataClient dataClient,
        CandleHistoryProvider history,
        ITradingRepository repository,
        IOptions<TradingAgentOptions> options,
        ILogger<AnalyzeCandlesTool> logger)
    {
        _dataClient = dataClient;
        _history = history;
        _repository = repository;
        _options = options;
        _logger = logger;
    }

    public override string Name => "analyze_candles";

    public override string Description =>
        "Read a PSX stock's candles (OHLC) and return its support/resistance levels, range position, " +
        "SMA20/50, RSI14, ATR14, volume vs average, and a suggested entry/stop/target with reward:risk. " +
        "A daily call (interval 1D, the default) ALSO returns the WEEKLY read plus the levels both " +
        "timeframes confirm and whether they agree — weekly structure is what makes a level reliable. " +
        "Set interval to 60m/30m/15m/5m for intraday entry timing; an intraday call returns the daily " +
        "levels as context. Also flags a BREAKDOWN — price at the bottom of its range because it is " +
        "still falling — on either timeframe. Call this before recommending a buy or sell level.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["symbol"] = new()
        {
            Type = "string",
            Description = "PSX ticker symbol, e.g. OGDC or LUCK.",
            Required = true
        },
        ["lookback_days"] = new()
        {
            Type = "integer",
            Description = "Trading sessions of candle history to analyze (5-250). Defaults to the " +
                          "configured scan lookback.",
            Required = false
        },
        ["include_live"] = new()
        {
            Type = "boolean",
            Description = "Include the current session's forming candle from the live market watch " +
                          "(default true). Set false to analyze settled closes only.",
            Required = false
        },
        ["interval"] = new()
        {
            Type = "string",
            Description = "Candle width: '1D' (default, daily) or intraday '60m', '30m', '15m', '5m'. " +
                          "Intraday uses the current session's full tick tape plus any archived earlier " +
                          "sessions, and returns the daily levels alongside as context. PSX publishes no " +
                          "historical intraday, so intraday history only covers sessions already archived.",
            EnumValues = ["1D", "60m", "30m", "15m", "5m"],
            Required = false
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var raw = arguments.GetValueOrDefault("symbol")?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return ToolResult.Fail("Parameter 'symbol' is required.");

        string symbol;
        try
        {
            symbol = PsxDataClient.NormalizeStockSymbol(raw);
        }
        catch (ArgumentException)
        {
            // Report the caller's own parameter, not the internal one the normalizer names.
            return ToolResult.Fail($"'{raw}' is not a valid PSX ticker symbol.");
        }

        var scan = _options.Value.Scan;
        var lookback = ToolArgs.Int(arguments, "lookback_days") ?? scan.LookbackDays;
        var includeLive = ToolArgs.Bool(arguments, "include_live") ?? true;

        var intervalLabel = ToolArgs.Text(arguments, "interval");
        var interval = PsxDataClient.ResolveInterval(intervalLabel);
        if (interval is null)
            return ToolResult.Fail(
                $"Interval '{intervalLabel}' is not supported. Use 1D, 60m, 30m, 15m, or 5m.");

        try
        {
            _logger.LogInformation("[AnalyzeCandles] {Symbol} at {Interval} over {Days} sessions…",
                symbol, PsxDataClient.IntervalLabel(interval.Value), lookback);

            // Weekly structure needs far more history than the daily read does, so ask for whichever
            // window is larger. The archive serves it locally; only missing dates hit the portal.
            var weeklySessions = Math.Clamp(scan.WeeklyLookbackWeeks, 12, 600) * 6;
            var sessionsWanted = Math.Max(lookback, weeklySessions);

            var historyTask = _history.GetDailyAsync([symbol], sessionsWanted, includeLive);
            var quoteTask = _dataClient.GetQuoteSummaryAsync(symbol);
            await Task.WhenAll(historyTask, quoteTask);

            var history = await historyTask;
            var quote = await quoteTask;

            if (!history.Series.TryGetValue(symbol, out var fullDaily) || fullDaily.Count == 0)
                return ToolResult.Fail(
                    $"No candles were returned for {symbol}. " +
                    string.Join(" ", history.Warnings.DefaultIfEmpty(
                        "Verify the ticker is listed on the PSX.")));

            var technicalOptions = TechnicalOptions.From(scan);

            // The DAILY read stays scoped to the requested lookback — widening it silently would move
            // every level the caller asked about. Weekly resampling uses the full archived series.
            var dailyCandles = fullDaily.TakeLast(lookback).ToList();
            var multi = MultiTimeframeAnalyzer.Analyze(
                symbol, fullDaily, technicalOptions, scan.ConfluenceTolerancePercent,
                quote.High52Week, quote.Low52Week);
            var daily = TechnicalAnalyzer.Analyze(
                symbol, dailyCandles, technicalOptions, quote.High52Week, quote.Low52Week);

            var warnings = history.Warnings.ToList();
            var scope = ResearchReferenceScope.Current;
            if (scope is not null)
            {
                foreach (var url in _dataClient.CandleSourceUrls())
                    scope.Add(url, $"PSX candles: {symbol}", "PSX Data Portal");
            }

            // ── Daily + weekly ────────────────────────────────────────────────
            if (interval.Value >= PsxCandle.DailyIntervalMinutes)
            {
                return ToolResult.Ok(JsonSerializer.Serialize(new
                {
                    symbol,
                    interval = "1D",
                    sessions_analyzed = dailyCandles.Count,
                    sessions_available = fullDaily.Count,
                    snapshot = daily,
                    // Weekly is the structural timeframe: a daily level the weekly chart also
                    // recognises is structure, one it does not is often just a recent swing.
                    weekly = multi.Weekly,
                    weekly_bars = multi.WeeklyBars,
                    timeframe_alignment = multi.Alignment,
                    weekly_breakdown = multi.WeeklyBreakdown,
                    entry_level_confirmed_weekly = multi.EntryLevelConfirmedWeekly,
                    confirmed_supports = multi.ConfirmedSupports,
                    confirmed_resistances = multi.ConfirmedResistances,
                    multi_timeframe_notes = multi.Notes,
                    quote,
                    recent_candles = Project(dailyCandles.TakeLast(20)),
                    recent_weekly_candles = Project(CandleResampler.ToWeekly(fullDaily).TakeLast(12)),
                    retrieved_at_utc = history.RetrievedAtUtc,
                    source_urls = _dataClient.CandleSourceUrls(),
                    warnings
                }, JsonOptions));
            }

            // ── Intraday ──────────────────────────────────────────────────────
            var intraday = await LoadIntradayAsync(symbol, interval.Value, warnings);
            if (intraday.Count == 0)
                return ToolResult.Fail(
                    $"No intraday trades are available for {symbol} at " +
                    $"{PsxDataClient.IntervalLabel(interval.Value)}. The PSX tick feed covers the current " +
                    "session only, so this happens before the open or when the symbol has not traded today " +
                    "and no earlier session has been archived. Use interval '1D' instead.");

            var snapshot = TechnicalAnalyzer.Analyze(symbol, intraday, technicalOptions);
            var sessions = intraday.Select(b => b.Date).Distinct().OrderBy(d => d).ToList();

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                symbol,
                interval = PsxDataClient.IntervalLabel(interval.Value),
                bars_analyzed = intraday.Count,
                sessions_covered = sessions.Select(d => d.ToString("yyyy-MM-dd")),
                snapshot,
                // The levels that matter are the daily and weekly ones; the intraday series is for
                // timing the entry against them. Reporting intraday alone invites trading noise as if
                // it were structure.
                weekly_context = multi.Weekly is null ? null : new
                {
                    multi.Weekly.Zone,
                    multi.Weekly.Setup,
                    multi.Weekly.NearestSupport,
                    multi.Weekly.NearestResistance,
                    alignment = multi.Alignment,
                    confirmed_supports = multi.ConfirmedSupports,
                    confirmed_resistances = multi.ConfirmedResistances,
                    notes = multi.Notes
                },
                daily_context = new
                {
                    daily.Zone,
                    daily.Setup,
                    daily.NearestSupport,
                    daily.PercentAboveSupport,
                    daily.NearestResistance,
                    daily.PercentBelowResistance,
                    daily.RangeLow,
                    daily.RangeHigh,
                    daily.Rsi14,
                    daily.Atr14,
                    daily.SuggestedEntry,
                    daily.SuggestedStop,
                    daily.SuggestedTarget,
                    daily.RewardRiskRatio,
                    daily.Reasons
                },
                quote,
                recent_candles = Project(intraday.TakeLast(30)),
                retrieved_at_utc = DateTime.UtcNow,
                source_urls = _dataClient.CandleSourceUrls()
                    .Append($"{_options.Value.PsxDataBaseUrl.TrimEnd('/')}/timeseries/int/{symbol}"),
                warnings
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AnalyzeCandles] Candle analysis failed for {Symbol}.", symbol);
            return ToolResult.Fail($"Candle analysis failed for {symbol}: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the intraday series: archived bars from earlier sessions, plus the current session
    /// rebuilt from the live tick tape. Today is always recomputed rather than read from the archive,
    /// so a bar that was still forming when it was last saved is never treated as final. Completed
    /// bars are written back (when archiving is enabled), which is how multi-session intraday history
    /// accumulates at all — PSX serves the current session only.
    /// </summary>
    private async Task<IReadOnlyList<PsxCandle>> LoadIntradayAsync(
        string symbol, int interval, List<string> warnings)
    {
        var scan = _options.Value.Scan;

        var ticks = await _dataClient.GetIntradayTicksAsync(symbol);
        var live = PsxDataClient.AggregateTicks(symbol, ticks, interval);

        var earliestLive = live.Count > 0 ? live[0].BucketStartUtc : null;
        IReadOnlyList<PsxCandle> archived = [];
        try
        {
            archived = await _repository.GetIntradayBarsAsync(
                symbol, interval, Math.Clamp(scan.IntradayLookbackBars, 20, 5000), earliestLive);
        }
        catch (Exception ex)
        {
            // The archive is an enhancement; losing it degrades history, it must not fail the analysis.
            _logger.LogWarning(ex, "[AnalyzeCandles] Intraday archive read failed for {Symbol}.", symbol);
            warnings.Add($"Archived intraday history could not be read ({ex.Message}); " +
                         "analysis uses the current session only.");
        }

        if (scan.ArchiveIntradayBars && live.Count > 0)
        {
            try
            {
                await _repository.SaveIntradayBarsAsync(live);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AnalyzeCandles] Intraday archive write failed for {Symbol}.", symbol);
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
                "Levels drawn from it are weak — trade the daily_context levels and use these bars only " +
                "for timing.");

        return merged;
    }

    private static IEnumerable<object> Project(IEnumerable<PsxCandle> candles) =>
        candles.Select(c => new
        {
            date = c.Date.ToString("yyyy-MM-dd"),
            time_utc = c.BucketStartUtc?.ToString("O"),
            open = c.Open,
            high = c.High,
            low = c.Low,
            close = c.Close,
            volume = c.Volume,
            is_live = c.IsLive
        });
}
