using AgentFox.Http;
using AgentFox.Plugins.Research;
using Microsoft.Extensions.Configuration;
using Tavily;

namespace AgentFox.TavilySearch;

/// <summary>Tavily-backed implementation of AgentFox's provider-neutral read-only search contract.</summary>
public sealed class TavilyWebSearchProvider : IWebSearchProvider
{
    private static readonly HttpClient Http =
        HttpResilienceFactory.Create(TimeSpan.FromMinutes(5));

    private readonly IConfiguration _configuration;

    public TavilyWebSearchProvider(IConfiguration configuration) => _configuration = configuration;

    public string Name => "tavily";

    public async Task<WebSearchResponse> SearchAsync(
        WebSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var query = request.Query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Search query is required.", nameof(request));
        if (query.Length > 1000)
            throw new ArgumentException("Search query is too long (maximum 1000 characters).", nameof(request));

        var apiKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY")
            ?? _configuration["Tavily:ApiKey"]
            ?? _configuration["Plugins:Tavily:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Tavily search is not configured.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));

        using var client = new TavilyClient(httpClient: Http, apiKey: apiKey);
        var depth = string.Equals(request.SearchDepth?.Trim(), "advanced", StringComparison.OrdinalIgnoreCase)
            ? CreateSearchRequestSearchDepth.Advanced
            : CreateSearchRequestSearchDepth.Basic;
        var searchRequest = new CreateSearchRequest
        {
            Query = query,
            IncludeImages = false,
            MaxResults = Math.Clamp(request.MaxResults, 1, 10),
            SearchDepth = depth,
            IncludeAnswer = request.IncludeAnswer
        };

        var response = await client.CreateSearchAsync(searchRequest, cancellationToken: timeout.Token);
        var results = response.Results
            .Where(result => !string.IsNullOrWhiteSpace(result.Url))
            .Select(result => new WebSearchResult(
                result.Title ?? string.Empty,
                result.Url!,
                result.Content ?? string.Empty,
                result.Score))
            .ToList();

        return new WebSearchResponse(
            response.Query ?? query,
            results,
            response.Answer,
            Name,
            DateTime.UtcNow);
    }
}
