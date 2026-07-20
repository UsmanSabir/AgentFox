using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentFox.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;

namespace TradingAgent.Research;

/// <summary>
/// Price/volume summary for one symbol computed from the PSX data portal's EOD and intraday
/// time series. All fields are nullable: a null means "the feed did not provide it", and the
/// research prompt tells the model to treat that as unknown.
/// </summary>
public sealed record PsxQuoteSummary
{
    public string Symbol { get; init; } = "";
    public decimal? LastPrice { get; init; }
    public decimal? PreviousClose { get; init; }
    public decimal? DayChangePercent { get; init; }
    public decimal? WeekChangePercent { get; init; }
    public decimal? MonthChangePercent { get; init; }
    public decimal? High52Week { get; init; }
    public decimal? Low52Week { get; init; }
    public long? LastVolume { get; init; }
    public long? AverageDailyVolume30D { get; init; }
    public string? Error { get; init; }
}

public sealed record NewsHeadline(string Title, string? Source, DateTime? PublishedUtc, string? Url = null);

/// <summary>
/// Listing status of a security on the PSX. Derived from the portal's company page, which renders a
/// status badge (e.g. DELISTED) next to the company name. <see cref="IsDelisted"/> is nullable:
/// null means the status could not be determined (page unreachable/unparseable), and the research
/// step treats that as unknown rather than assuming the stock is tradable.
/// </summary>
public sealed record PsxListingStatus
{
    public string Symbol { get; init; } = "";
    public bool? IsDelisted { get; init; }
    public string? StatusLabel { get; init; }
    public string? Error { get; init; }
}

/// <summary>Everything gathered for one research request, ready to hand to the LLM analyst step.</summary>
public sealed record StockResearchData
{
    public PsxQuoteSummary Quote { get; init; } = new();
    public PsxQuoteSummary IndexQuote { get; init; } = new();
    public PsxListingStatus ListingStatus { get; init; } = new();
    public IReadOnlyList<NewsHeadline> CompanyNews { get; init; } = [];
    public IReadOnlyList<NewsHeadline> MarketNews { get; init; } = [];
    public DateTime RetrievedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>The web endpoints consulted for this gather (PSX portal series + company page), for citation.</summary>
    public IReadOnlyList<string> SourceUrls { get; init; } = [];
}

/// <summary>Read-only evidence gathered for a PSX index query.</summary>
public sealed record IndexResearchData
{
    public string Index { get; init; } = "";
    public PsxQuoteSummary Quote { get; init; } = new();
    public DateTime RetrievedAtUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<string> SourceUrls { get; init; } = [];
}

/// <summary>
/// Fetches market data from the official PSX data portal (dps.psx.com.pk) and recent headlines
/// from Google News RSS. No API key required for either. Every fetch is independent and
/// fail-soft: a dead feed degrades the research evidence, it never throws out of
/// <see cref="GatherAsync"/>.
/// </summary>
public sealed class PsxDataClient
{
    private const string Kse100Symbol = "KSE100";

    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<PsxDataClient> _logger;
    private readonly HttpClient _http;

    public PsxDataClient(IOptions<TradingAgentOptions> options, ILogger<PsxDataClient> logger)
    {
        _options = options;
        _logger = logger;
        _http = HttpResilienceFactory.Create(TimeSpan.FromSeconds(25));
        // Google News RSS (and some CDNs in front of the PSX portal) reject requests without a UA.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; AgentFox-TradingResearch/1.0)");
    }

    public async Task<StockResearchData> GatherAsync(string symbol, CancellationToken ct = default)
    {
        symbol = symbol.Trim().ToUpperInvariant();

        var quoteTask   = GetQuoteSummaryAsync(symbol, ct);
        var indexTask   = GetQuoteSummaryAsync(Kse100Symbol, ct);
        var listingTask = GetListingStatusAsync(symbol, ct);
        var newsTask    = _options.Value.ResearchNewsEnabled
            ? GetNewsAsync($"\"{symbol}\" PSX Pakistan stock", ct)
            : Task.FromResult<IReadOnlyList<NewsHeadline>>([]);
        var marketTask  = _options.Value.ResearchNewsEnabled
            ? GetNewsAsync("Pakistan Stock Exchange KSE-100", ct)
            : Task.FromResult<IReadOnlyList<NewsHeadline>>([]);

        await Task.WhenAll(quoteTask, indexTask, listingTask, newsTask, marketTask);

        var baseUrl = _options.Value.PsxDataBaseUrl.TrimEnd('/');
        var sourceUrls = new List<string>
        {
            $"{baseUrl}/timeseries/eod/{symbol}",
            $"{baseUrl}/timeseries/int/{symbol}",
            $"{baseUrl}/company/{symbol}",
            $"{baseUrl}/timeseries/eod/{Kse100Symbol}"
        };
        if (_options.Value.ResearchNewsEnabled)
        {
            sourceUrls.Add(NewsFeedUrl($"\"{symbol}\" PSX Pakistan stock"));
            sourceUrls.Add(NewsFeedUrl("Pakistan Stock Exchange KSE-100"));
        }

        return new StockResearchData
        {
            Quote          = await quoteTask,
            IndexQuote     = await indexTask,
            ListingStatus  = await listingTask,
            CompanyNews    = await newsTask,
            MarketNews     = await marketTask,
            RetrievedAtUtc = DateTime.UtcNow,
            SourceUrls     = sourceUrls
        };
    }

    /// <summary>
    /// Fetches one PSX index by its official portal symbol (for example KSE30 or KSE100).
    /// This path is deliberately separate from stock research so an index question does not
    /// trigger a company-page lookup or a stock listing assessment.
    /// </summary>
    public async Task<IndexResearchData> GatherIndexAsync(string index, CancellationToken ct = default)
    {
        index = NormalizePortalSymbol(index, "index");
        var quote = await GetQuoteSummaryAsync(index, ct);
        var baseUrl = _options.Value.PsxDataBaseUrl.TrimEnd('/');

        return new IndexResearchData
        {
            Index = index,
            Quote = quote,
            RetrievedAtUtc = DateTime.UtcNow,
            SourceUrls =
            [
                $"{baseUrl}/indices",
                $"{baseUrl}/timeseries/eod/{index}",
                $"{baseUrl}/timeseries/int/{index}"
            ]
        };
    }

    // ── PSX data portal ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a quote summary from the portal's EOD series (trend, 52-week range) topped up with the
    /// latest intraday tick when available. Series shape: {"status":1,"data":[[unixTs, price, volume],…]}.
    /// </summary>
    public async Task<PsxQuoteSummary> GetQuoteSummaryAsync(string symbol, CancellationToken ct = default)
    {
        try
        {
            var eod = await FetchSeriesAsync($"timeseries/eod/{symbol}", ct);
            var intraday = await FetchSeriesAsync($"timeseries/int/{symbol}", ct);

            if (eod.Count == 0 && intraday.Count == 0)
                return new PsxQuoteSummary
                {
                    Symbol = symbol,
                    Error = "The PSX data portal returned no price data for this symbol — verify the ticker."
                };

            // Both series are ordered newest-first by the portal; sort defensively anyway.
            eod = eod.OrderByDescending(p => p.Ts).ToList();
            intraday = intraday.OrderByDescending(p => p.Ts).ToList();

            var lastTick   = intraday.FirstOrDefault() ?? eod.First();
            var lastPrice  = lastTick.Price;

            // "Previous close" = most recent EOD strictly older than the last tick's day.
            var lastDay    = DateTimeOffset.FromUnixTimeSeconds(lastTick.Ts).UtcDateTime.Date;
            var prevClose  = eod.FirstOrDefault(p =>
                DateTimeOffset.FromUnixTimeSeconds(p.Ts).UtcDateTime.Date < lastDay)?.Price;

            var yearAgo    = DateTimeOffset.UtcNow.AddYears(-1).ToUnixTimeSeconds();
            var yearSeries = eod.Where(p => p.Ts >= yearAgo).ToList();

            return new PsxQuoteSummary
            {
                Symbol                = symbol,
                LastPrice             = lastPrice,
                PreviousClose         = prevClose,
                DayChangePercent      = PercentChange(prevClose, lastPrice),
                WeekChangePercent     = PercentChange(CloseDaysAgo(eod, 7), lastPrice),
                MonthChangePercent    = PercentChange(CloseDaysAgo(eod, 30), lastPrice),
                High52Week            = yearSeries.Count > 0 ? yearSeries.Max(p => p.Price) : null,
                Low52Week             = yearSeries.Count > 0 ? yearSeries.Min(p => p.Price) : null,
                LastVolume            = eod.FirstOrDefault()?.Volume,
                AverageDailyVolume30D = AverageVolume(eod, 30)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PsxData] Quote fetch failed for {Symbol}.", symbol);
            return new PsxQuoteSummary { Symbol = symbol, Error = $"PSX data fetch failed: {ex.Message}" };
        }
    }

    private sealed record SeriesPoint(long Ts, decimal Price, long Volume);

    private async Task<List<SeriesPoint>> FetchSeriesAsync(string path, CancellationToken ct)
    {
        var baseUrl = _options.Value.PsxDataBaseUrl.TrimEnd('/');
        using var response = await _http.GetAsync($"{baseUrl}/{path}", ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var points = new List<SeriesPoint>(data.GetArrayLength());
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2) continue;
            var cells = row.EnumerateArray().ToArray();
            if (!TryDecimal(cells[1], out var price)) continue;
            if (!TryLong(cells[0], out var ts)) continue;
            TryLong(cells.Length > 2 ? cells[2] : default, out var volume);
            points.Add(new SeriesPoint(ts, price, volume));
        }

        return points;
    }

    private static string NormalizePortalSymbol(string value, string parameterName)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length is < 1 or > 24 || !Regex.IsMatch(normalized, "^[A-Z0-9_-]+$"))
            throw new ArgumentException($"Invalid PSX {parameterName} symbol.", parameterName);
        return normalized;
    }

    private static bool TryDecimal(JsonElement el, out decimal value)
    {
        value = 0m;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static bool TryLong(JsonElement el, out long value)
    {
        value = 0;
        if (el.ValueKind == JsonValueKind.Number)
        {
            if (el.TryGetInt64(out value)) return true;
            if (el.TryGetDecimal(out var d)) { value = (long)d; return true; }
        }
        return el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out value);
    }

    private static decimal? CloseDaysAgo(List<SeriesPoint> eodNewestFirst, int days)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
        return eodNewestFirst.FirstOrDefault(p => p.Ts <= cutoff)?.Price;
    }

    private static long? AverageVolume(List<SeriesPoint> eodNewestFirst, int days)
    {
        var window = eodNewestFirst.Take(days).Where(p => p.Volume > 0).ToList();
        return window.Count == 0 ? null : (long)window.Average(p => p.Volume);
    }

    private static decimal? PercentChange(decimal? from, decimal? to) =>
        from is > 0 && to is not null ? Math.Round((to.Value - from.Value) / from.Value * 100m, 2) : null;

    // ── Listing status (PSX company page) ──────────────────────────────────────

    /// <summary>
    /// Fetches the PSX company page and derives listing status (delisted or not). Fail-soft: an
    /// unreachable page yields <see cref="PsxListingStatus.IsDelisted"/> = null (unknown), never an
    /// exception, so it degrades the research evidence rather than aborting <see cref="GatherAsync"/>.
    /// </summary>
    public async Task<PsxListingStatus> GetListingStatusAsync(string symbol, CancellationToken ct = default)
    {
        try
        {
            var baseUrl = _options.Value.PsxDataBaseUrl.TrimEnd('/');
            var html = await _http.GetStringAsync($"{baseUrl}/company/{symbol}", ct);
            return ParseListingStatus(symbol, html);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PsxData] Listing-status fetch failed for {Symbol}.", symbol);
            return new PsxListingStatus
            {
                Symbol = symbol,
                IsDelisted = null,
                Error = $"Listing-status fetch failed: {ex.Message}"
            };
        }
    }

    // The portal renders a status badge next to the company name for non-normal listings, e.g.
    // <div class="tag tag--skim tag--del">DELISTED</div>. Match the delisted modifier class (guarding
    // against longer tokens like "tag--delta") corroborated by the visible DELISTED label.
    private static readonly Regex DelistedClassRegex =
        new(@"tag--del(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DelistedLabelRegex =
        new(@">\s*DELISTED\s*<", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses a PSX company page for delisted status. Pure/deterministic so it can be unit-tested
    /// without network access. Returns IsDelisted = null when the page is empty/unusable.
    /// </summary>
    public static PsxListingStatus ParseListingStatus(string symbol, string? html)
    {
        symbol = symbol.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(html))
            return new PsxListingStatus
            {
                Symbol = symbol,
                IsDelisted = null,
                Error = "The PSX company page returned no content — listing status is unknown."
            };

        var delisted = DelistedClassRegex.IsMatch(html) || DelistedLabelRegex.IsMatch(html);

        return new PsxListingStatus
        {
            Symbol = symbol,
            IsDelisted = delisted,
            StatusLabel = delisted ? "DELISTED" : null
        };
    }

    // ── News (Google News RSS — keyless) ──────────────────────────────────────

    private async Task<IReadOnlyList<NewsHeadline>> GetNewsAsync(string query, CancellationToken ct)
    {
        try
        {
            var url = NewsFeedUrl(query);
            var xml = await _http.GetStringAsync(url, ct);
            return ParseNewsFeed(xml, Math.Max(1, _options.Value.ResearchHeadlineCount));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PsxData] News fetch failed for query '{Query}'.", query);
            return [];
        }
    }

    /// <summary>Builds the keyless Google News RSS search URL for a query.</summary>
    public static string NewsFeedUrl(string query) =>
        "https://news.google.com/rss/search?q=" + Uri.EscapeDataString(query) + "&hl=en-PK&gl=PK&ceid=PK:en";

    /// <summary>
    /// Parses a Google News RSS document into headlines. Pure/deterministic so it can be unit-tested
    /// without network access. Each item's &lt;link&gt; is captured as <see cref="NewsHeadline.Url"/>.
    /// Returns an empty list for unparseable XML.
    /// </summary>
    public static IReadOnlyList<NewsHeadline> ParseNewsFeed(string xml, int max)
    {
        try
        {
            var feed = XDocument.Parse(xml);
            return feed.Descendants("item")
                .Take(Math.Max(1, max))
                .Select(item => new NewsHeadline(
                    item.Element("title")?.Value.Trim() ?? "",
                    item.Elements().FirstOrDefault(e => e.Name.LocalName == "source")?.Value.Trim(),
                    DateTime.TryParse(item.Element("pubDate")?.Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
                        ? dt : null,
                    item.Element("link")?.Value.Trim()))
                .Where(h => h.Title.Length > 0)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
