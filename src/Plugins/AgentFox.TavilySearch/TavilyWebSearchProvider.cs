using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFox.Http;
using AgentFox.Plugins.Research;
using Microsoft.Extensions.Configuration;

namespace AgentFox.TavilySearch;

/// <summary>
/// Tavily REST implementation of AgentFox's provider-neutral read-only search contract.
/// Uses the documented JSON endpoint directly so SDK model drift cannot silently turn a valid
/// response into an empty result set.
/// </summary>
public sealed class TavilyWebSearchProvider : IWebSearchProvider
{
    private static readonly HttpClient SharedHttp =
        HttpResilienceFactory.Create(TimeSpan.FromMinutes(5));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IConfiguration _configuration;
    private readonly HttpClient _http;

    public TavilyWebSearchProvider(IConfiguration configuration) : this(configuration, SharedHttp) { }

    public TavilyWebSearchProvider(IConfiguration configuration, HttpClient httpClient)
    {
        _configuration = configuration;
        _http = httpClient;
    }

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

        var apiKey = ResolveApiKey(_configuration);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Tavily search is not configured. Set TAVILY_API_KEY or Plugins:Tavily:ApiKey.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));

        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
        {
            Content = JsonContent.Create(new
            {
                query,
                search_depth = NormalizeDepth(request.SearchDepth),
                max_results = Math.Clamp(request.MaxResults, 1, 10),
                topic = "general",
                include_answer = request.IncludeAnswer,
                include_raw_content = false,
                include_images = false
            }, options: JsonOptions)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Tavily returned HTTP {(int)response.StatusCode}: {ReadError(json) ?? response.ReasonPhrase ?? "request failed"}.");

        var payload = ParseResponse(json, query);
        if (payload.Results.Count == 0)
        {
            var requestId = string.IsNullOrWhiteSpace(payload.RequestId)
                ? string.Empty
                : $" Request ID: {payload.RequestId}.";
            throw new InvalidOperationException(
                $"Tavily returned zero sourced results for '{query}'.{requestId} " +
                "Try a broader query or check the Tavily account quota/status.");
        }

        return new WebSearchResponse(
            payload.Query,
            payload.Results,
            payload.Answer,
            Name,
            DateTime.UtcNow);
    }

    public static ParsedTavilyResponse ParseResponse(string json, string fallbackQuery)
    {
        TavilyResponse? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TavilyResponse>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Tavily returned malformed JSON.", ex);
        }

        if (payload is null)
            throw new InvalidOperationException("Tavily returned an empty response body.");

        var results = (payload.Results ?? [])
            .Where(result => Uri.TryCreate(result.Url, UriKind.Absolute, out var uri) &&
                             (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Select(result => new WebSearchResult(
                result.Title?.Trim() ?? string.Empty,
                result.Url!.Trim(),
                result.Content?.Trim() ?? string.Empty,
                result.Score))
            .ToList();

        return new ParsedTavilyResponse(
            string.IsNullOrWhiteSpace(payload.Query) ? fallbackQuery : payload.Query.Trim(),
            payload.Answer?.Trim(),
            results,
            payload.RequestId);
    }

    internal static string? ResolveApiKey(IConfiguration configuration) =>
        FirstRealKey(
            Environment.GetEnvironmentVariable("TAVILY_API_KEY"),
            configuration["Tavily:ApiKey"],
            configuration["Plugins:Tavily:ApiKey"]);

    private static string? FirstRealKey(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value =>
            !string.IsNullOrWhiteSpace(value) &&
            !value.Contains("your-", StringComparison.OrdinalIgnoreCase) &&
            !value.Contains("your_", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeDepth(string? value) =>
        string.Equals(value?.Trim(), "advanced", StringComparison.OrdinalIgnoreCase)
            ? "advanced"
            : "basic";

    private static string? ReadError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("detail", out var detail)) return null;
            if (detail.ValueKind == JsonValueKind.String) return detail.GetString();
            if (detail.TryGetProperty("error", out var error)) return error.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    public sealed record ParsedTavilyResponse(
        string Query,
        string? Answer,
        IReadOnlyList<WebSearchResult> Results,
        string? RequestId);

    private sealed class TavilyResponse
    {
        public string? Query { get; set; }
        public string? Answer { get; set; }
        public List<TavilyResult>? Results { get; set; }
        public string? RequestId { get; set; }
    }

    private sealed class TavilyResult
    {
        public string? Title { get; set; }
        public string? Url { get; set; }
        public string? Content { get; set; }
        public double? Score { get; set; }
    }
}
