using System.Net.Http.Headers;
using System.Text.Json;
using AgentFox.Http;
using AgentFox.Plugins.Interfaces;
using AgentFox.Plugins.Research;
using Microsoft.Extensions.Configuration;

namespace AgentFox.BraveSearch;

/// <summary>Read-only Brave Web Search tool that returns parsed, sourced results.</summary>
public sealed class BraveSearchTool : BaseTool
{
    private static readonly HttpClient Http =
        HttpResilienceFactory.Create(TimeSpan.FromMinutes(5));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly IConfiguration _configuration;
    private readonly string _apiKey;

    public BraveSearchTool(IConfiguration configuration)
    {
        _configuration = configuration;
        _apiKey = ResolveApiKey(configuration)
            ?? throw new InvalidOperationException(
                "Brave Search is not configured. Set BRAVE_SEARCH_API_KEY or Plugins:BraveSearch:ApiKey.");
    }

    public override string Name => "brave_search";
    public override string Description =>
        "Search the current web with Brave and return parsed titles, URLs, and snippets with source references.";

    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["query"] = new() { Type = "string", Description = "Search query", Required = true }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var query = arguments.GetValueOrDefault("query")?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Fail("No query provided");
        if (query.Length > 400)
            return ToolResult.Fail("Brave search query is too long (maximum 400 characters).");

        var count = Math.Clamp(
            _configuration.GetValue<int?>("BraveSearch:MaxResults")
            ?? _configuration.GetValue<int?>("Plugins:BraveSearch:MaxResults")
            ?? 5,
            1,
            20);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var response = await GetBraveSearchAsync(query, _apiKey, timeout.Token, count);
            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                return ToolResult.Fail(
                    $"Brave returned HTTP {(int)response.StatusCode}: " +
                    $"{ReadError(json) ?? response.ReasonPhrase ?? "request failed"}.");

            var results = ParseResults(json);
            if (results.Count == 0)
                return ToolResult.Fail($"Brave returned zero web results for '{query}'.");

            var scope = ResearchReferenceScope.Current;
            if (scope is not null)
            {
                foreach (var result in results)
                    scope.Add(result.Url, result.Title, "Brave Search");
            }

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                query,
                provider = "brave",
                retrieved_at_utc = DateTime.UtcNow,
                results
            }, JsonOptions));
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("Brave search timed out after 45 seconds.");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Brave search failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    public static async Task<HttpResponseMessage> GetBraveSearchAsync(
        string query,
        string apiKey,
        CancellationToken cancellationToken,
        int count = 5)
    {
        var url = "https://api.search.brave.com/res/v1/web/search" +
                  $"?q={Uri.EscapeDataString(query)}" +
                  $"&count={Math.Clamp(count, 1, 20)}" +
                  "&offset=0&result_filter=web&text_decorations=false";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Subscription-Token", apiKey.Trim());
        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public static IReadOnlyList<BraveWebResult> ParseResults(string json)
    {
        BraveResponse? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BraveResponse>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Brave returned malformed JSON.", ex);
        }

        return (payload?.Web?.Results ?? [])
            .Where(result => Uri.TryCreate(result.Url, UriKind.Absolute, out var uri) &&
                             (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Select(result => new BraveWebResult(
                result.Title?.Trim() ?? string.Empty,
                result.Url!.Trim(),
                result.Description?.Trim() ?? string.Empty,
                result.Age))
            .ToList();
    }

    internal static string? ResolveApiKey(IConfiguration configuration) =>
        FirstRealKey(
            Environment.GetEnvironmentVariable("BRAVE_SEARCH_API_KEY"),
            configuration["BraveSearch:ApiKey"],
            configuration["Plugins:BraveSearch:ApiKey"]);

    private static string? FirstRealKey(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value =>
            !string.IsNullOrWhiteSpace(value) &&
            !value.Contains("your-", StringComparison.OrdinalIgnoreCase) &&
            !value.Contains("your_", StringComparison.OrdinalIgnoreCase));

    private static string? ReadError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("message", out var message)) return message.GetString();
            if (doc.RootElement.TryGetProperty("detail", out var detail)) return detail.ToString();
        }
        catch (JsonException) { }
        return null;
    }

    public sealed record BraveWebResult(string Title, string Url, string Description, string? Age);

    private sealed class BraveResponse
    {
        public BraveWeb? Web { get; set; }
    }

    private sealed class BraveWeb
    {
        public List<BraveResult>? Results { get; set; }
    }

    private sealed class BraveResult
    {
        public string? Title { get; set; }
        public string? Url { get; set; }
        public string? Description { get; set; }
        public string? Age { get; set; }
    }
}
