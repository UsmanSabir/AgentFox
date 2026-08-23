using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFox.Plugins.Interfaces;
using AgentFox.Plugins.Research;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Analysis;
using TradingAgent.Config;
using TradingAgent.Research;

namespace TradingAgent.Tools;

/// <summary>
/// Researches one PSX stock before a trade decision: pulls live PSX portal data (price, trend,
/// 52-week range, volume), the KSE-100 backdrop, and recent company/market headlines, then has
/// the LLM turn that EVIDENCE into a structured confidence assessment for acting on the tip.
///
/// The verdict is grounded: the model only sees data fetched in this call, the raw evidence is
/// returned alongside the assessment so the agent (and the human) can audit the reasoning, and
/// a failed feed degrades to INSUFFICIENT_DATA instead of a made-up opinion. This research
/// confidence is INDEPENDENT of parse_signal's extraction confidence — the first says "is the
/// tip clearly worded", this one says "does the market picture support acting on it".
/// </summary>
public sealed class ResearchStockTool : BaseTool
{
    private readonly PsxDataClient _dataClient;
    private readonly StockAssessmentService _assessments;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<ResearchStockTool> _logger;

    private static readonly JsonSerializerOptions _snakeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public ResearchStockTool(
        PsxDataClient dataClient,
        StockAssessmentService assessments,
        IOptions<TradingAgentOptions> options,
        ILogger<ResearchStockTool> logger)
    {
        _dataClient = dataClient;
        _assessments = assessments;
        _options = options;
        _logger = logger;
    }

    public override string Name => "research_stock";

    public override string Description =>
        "Research a PSX stock before deciding on a tip: fetches live PSX price/trend/volume data, " +
        "the KSE-100 index backdrop, and recent company & market news, then returns a structured " +
        "confidence assessment (confidence level + score, PROCEED/CAUTION/AVOID recommendation, " +
        "rationale, supporting and risk factors) plus the raw evidence. Call this for EVERY " +
        "actionable signal after parse_signal and include the assessment in the trade proposal.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["symbol"] = new()
        {
            Type        = "string",
            Description = "PSX ticker symbol to research (e.g. OGDC, LUCK).",
            Required    = true
        },
        ["tip_context"] = new()
        {
            Type        = "string",
            Description = "Optional: the tip/signal being evaluated (action, entry, target, stop-loss, " +
                          "raw message) so the assessment can sanity-check it against live data.",
            Required    = false
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var symbol = arguments.GetValueOrDefault("symbol")?.ToString()?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(symbol))
            return ToolResult.Fail("Parameter 'symbol' is required.");

        var tipContext = arguments.GetValueOrDefault("tip_context")?.ToString();

        _logger.LogInformation("[ResearchStock] Researching {Symbol}…", symbol);
        var data = await _dataClient.GatherAsync(symbol);

        // Candle-derived levels, so the analyst judges a tip's entry against actual support and
        // resistance rather than only against the 52-week extremes. Fail-soft: if the OHLC feed is
        // unavailable the assessment proceeds without levels rather than failing the research.
        var technical = await GetTechnicalAsync(symbol, data.Quote);

        // Register the web sources consulted so the chat UI can cite them. Fail-soft: no-op when no
        // scope is open (e.g. the tool is invoked outside an agent turn).
        var scope = ResearchReferenceScope.Current;
        if (scope is not null)
        {
            foreach (var headline in data.CompanyNews.Concat(data.MarketNews))
                scope.Add(headline.Url, headline.Title, headline.Source ?? "News");
            foreach (var portalUrl in data.SourceUrls)
                scope.Add(portalUrl, $"PSX data: {symbol}", "PSX Data Portal");
        }

        var evidence = new
        {
            symbol,
            quote          = data.Quote,
            technical,
            kse100_index   = data.IndexQuote,
            listing_status = data.ListingStatus,
            company_news   = data.CompanyNews,
            market_news    = data.MarketNews,
            retrieved_at_utc = data.RetrievedAtUtc
        };

        // The rubric, the delisted gate, and the fail-conservative fallback all live in
        // StockAssessmentService, shared with the /assess endpoints so one confidence standard applies
        // wherever a verdict is produced.
        var assessment = await _assessments.AssessAsync(new StockAssessmentRequest
        {
            Symbol       = symbol,
            Evidence     = evidence,
            Context      = tipContext,
            ContextLabel = "TIP UNDER EVALUATION",
            IsDelisted   = data.ListingStatus.IsDelisted == true
        });

        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            symbol,
            assessment,
            evidence
        }, _snakeOptions));
    }

    /// <summary>
    /// Loads daily candles and returns the deterministic level/indicator read, or null when the OHLC
    /// feed is unavailable. Uses the same lookback as scan_watchlist so both share the cached
    /// market-wide sessions instead of each warming its own window.
    /// </summary>
    private async Task<TechnicalSnapshot?> GetTechnicalAsync(string symbol, PsxQuoteSummary quote)
    {
        var scan = _options.Value.Scan;
        try
        {
            var history = await _dataClient.GetCandleHistoryAsync([symbol], scan.LookbackDays);
            if (!history.Series.TryGetValue(symbol, out var candles) || candles.Count == 0)
                return null;

            return TechnicalAnalyzer.Analyze(
                symbol, candles, TechnicalOptions.From(scan), quote.High52Week, quote.Low52Week);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ResearchStock] Candle analysis unavailable for {Symbol}.", symbol);
            return null;
        }
    }

}
