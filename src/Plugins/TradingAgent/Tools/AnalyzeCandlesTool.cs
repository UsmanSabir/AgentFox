using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFox.Plugins.Interfaces;
using AgentFox.Plugins.Research;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Analysis;
using TradingAgent.Config;
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
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<AnalyzeCandlesTool> _logger;

    public AnalyzeCandlesTool(
        PsxDataClient dataClient,
        IOptions<TradingAgentOptions> options,
        ILogger<AnalyzeCandlesTool> logger)
    {
        _dataClient = dataClient;
        _options = options;
        _logger = logger;
    }

    public override string Name => "analyze_candles";

    public override string Description =>
        "Read a PSX stock's daily candles (OHLC) and return its support/resistance levels, where the " +
        "price sits in its range (at support, mid-range, at resistance), SMA20/50, RSI14, ATR14, " +
        "volume vs average, and a suggested entry/stop/target with reward:risk. Also flags a " +
        "BREAKDOWN — price at the bottom of its range because it is still falling, which must not be " +
        "treated as a buy. Call this before recommending a buy or sell level for a specific stock.";

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

        try
        {
            _logger.LogInformation("[AnalyzeCandles] {Symbol} over {Days} sessions…", symbol, lookback);

            // The 52-week extremes come from the portal's long EOD series, which reaches further back
            // than any practical candle window, and are fed in as additional levels.
            var historyTask = _dataClient.GetCandleHistoryAsync([symbol], lookback, includeLive);
            var quoteTask = _dataClient.GetQuoteSummaryAsync(symbol);
            await Task.WhenAll(historyTask, quoteTask);

            var history = await historyTask;
            var quote = await quoteTask;

            if (!history.Series.TryGetValue(symbol, out var candles) || candles.Count == 0)
                return ToolResult.Fail(
                    $"No candles were returned for {symbol}. " +
                    string.Join(" ", history.Warnings.DefaultIfEmpty(
                        "Verify the ticker is listed on the PSX.")));

            var snapshot = TechnicalAnalyzer.Analyze(
                symbol,
                candles,
                TechnicalOptions.From(scan),
                quote.High52Week,
                quote.Low52Week);

            var scope = ResearchReferenceScope.Current;
            if (scope is not null)
            {
                foreach (var url in _dataClient.CandleSourceUrls())
                    scope.Add(url, $"PSX candles: {symbol}", "PSX Data Portal");
            }

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                symbol,
                sessions_analyzed = candles.Count,
                snapshot,
                quote,
                recent_candles = candles.TakeLast(20).Select(c => new
                {
                    date = c.Date.ToString("yyyy-MM-dd"),
                    open = c.Open,
                    high = c.High,
                    low = c.Low,
                    close = c.Close,
                    volume = c.Volume,
                    is_live = c.IsLive
                }),
                retrieved_at_utc = history.RetrievedAtUtc,
                source_urls = _dataClient.CandleSourceUrls(),
                warnings = history.Warnings
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AnalyzeCandles] Candle analysis failed for {Symbol}.", symbol);
            return ToolResult.Fail($"Candle analysis failed for {symbol}: {ex.Message}");
        }
    }
}
