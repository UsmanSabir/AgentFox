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

    private readonly CandleAnalysisService _analysis;
    private readonly PsxDataClient _dataClient;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<AnalyzeCandlesTool> _logger;

    public AnalyzeCandlesTool(
        CandleAnalysisService analysis,
        PsxDataClient dataClient,
        IOptions<TradingAgentOptions> options,
        ILogger<AnalyzeCandlesTool> logger)
    {
        _analysis = analysis;
        _dataClient = dataClient;
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

            // Loading and analysis live in CandleAnalysisService so the chart endpoint draws the very
            // same levels this tool quotes. This method is now just the agent-facing projection.
            var analysis = await _analysis.AnalyzeAsync(symbol, interval.Value, lookback, includeLive);

            var multi = analysis.Multi;
            var daily = analysis.Daily;
            var quote = analysis.Quote;
            var warnings = analysis.Warnings.ToList();

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
                    sessions_analyzed = analysis.Candles.Count,
                    sessions_available = analysis.SessionsAvailable,
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
                    recent_candles = Project(analysis.Candles.TakeLast(20)),
                    recent_weekly_candles = Project(analysis.WeeklyCandles.TakeLast(12)),
                    retrieved_at_utc = analysis.RetrievedAtUtc,
                    source_urls = analysis.SourceUrls,
                    warnings
                }, JsonOptions));
            }

            // ── Intraday ──────────────────────────────────────────────────────
            var intraday = analysis.Candles;
            var sessions = intraday.Select(b => b.Date).Distinct().OrderBy(d => d).ToList();

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                symbol,
                interval = analysis.Interval,
                bars_analyzed = intraday.Count,
                sessions_covered = sessions.Select(d => d.ToString("yyyy-MM-dd")),
                snapshot = analysis.Snapshot,
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
                retrieved_at_utc = analysis.RetrievedAtUtc,
                source_urls = analysis.SourceUrls,
                warnings
            }, JsonOptions));
        }
        catch (CandleAnalysisException ex)
        {
            // Expected "there is nothing to analyze" case; the message already explains why.
            return ToolResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AnalyzeCandles] Candle analysis failed for {Symbol}.", symbol);
            return ToolResult.Fail($"Candle analysis failed for {symbol}: {ex.Message}");
        }
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
