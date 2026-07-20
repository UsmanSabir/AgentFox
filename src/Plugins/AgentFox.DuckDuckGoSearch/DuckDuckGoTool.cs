using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFox.Http;
using AgentFox.Plugins.Interfaces;
using AgentFox.Plugins.Research;

namespace AgentFox.DuckDuckGoSearch;

/// <summary>
/// DuckDuckGo Instant Answer client. This is not a full web-search API, but it can return a
/// sourced abstract and related topics for entity/general-knowledge queries.
/// </summary>
public sealed class DuckDuckGoTool : BaseTool
{
    private static readonly HttpClient Http =
        HttpResilienceFactory.Create(TimeSpan.FromMinutes(5));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public override string Name => "duckduckgo_search";
    public override string Description =>
        "Query DuckDuckGo Instant Answers for sourced abstracts and related topics. " +
        "This is not a full current-web search; use Tavily, Brave, or browse_web for current market/news research.";

    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["query"] = new() { Type = "string", Description = "Instant-answer query", Required = true }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var query = arguments.GetValueOrDefault("query")?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Fail("No query provided");

        try
        {
            var url = "https://api.duckduckgo.com/" +
                      $"?q={Uri.EscapeDataString(query)}&format=json&no_html=1&no_redirect=1";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var response = await Http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return ToolResult.Fail($"DuckDuckGo returned HTTP {(int)response.StatusCode}: {response.ReasonPhrase}.");

            var payload = await response.Content.ReadFromJsonAsync<DuckResponse>(JsonOptions, timeout.Token);
            var parsed = ParseResponse(payload);
            if (string.IsNullOrWhiteSpace(parsed.AbstractText) && parsed.RelatedTopics.Count == 0)
            {
                return ToolResult.Fail(
                    $"DuckDuckGo Instant Answer returned no abstract or related topics for '{query}'. " +
                    "It is not a general web-search endpoint; try Tavily, Brave, or browse_web.");
            }

            var scope = ResearchReferenceScope.Current;
            if (scope is not null)
            {
                scope.Add(parsed.AbstractUrl, parsed.Heading, parsed.AbstractSource ?? "DuckDuckGo");
                foreach (var topic in parsed.RelatedTopics)
                    scope.Add(topic.Url, topic.Text, "DuckDuckGo");
            }

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                query,
                provider = "duckduckgo_instant_answer",
                heading = parsed.Heading,
                abstract_text = parsed.AbstractText,
                abstract_source = parsed.AbstractSource,
                abstract_url = parsed.AbstractUrl,
                related_topics = parsed.RelatedTopics,
                retrieved_at_utc = DateTime.UtcNow
            }, JsonOptions));
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("DuckDuckGo request timed out after 45 seconds.");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"DuckDuckGo request failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    public static ParsedDuckResponse ParseResponse(DuckResponse? payload)
    {
        var topics = Flatten(payload?.RelatedTopics ?? [])
            .Where(topic => !string.IsNullOrWhiteSpace(topic.Text) &&
                            Uri.TryCreate(topic.FirstUrl, UriKind.Absolute, out var uri) &&
                            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Take(8)
            .Select(topic => new DuckTopic(topic.Text!.Trim(), topic.FirstUrl!.Trim()))
            .ToList();

        return new ParsedDuckResponse(
            payload?.Heading?.Trim(),
            payload?.AbstractText?.Trim(),
            payload?.AbstractSource?.Trim(),
            NormalizeHttpUrl(payload?.AbstractUrl),
            topics);
    }

    private static IEnumerable<RelatedTopic> Flatten(IEnumerable<RelatedTopic> topics)
    {
        foreach (var topic in topics)
        {
            if (!string.IsNullOrWhiteSpace(topic.Text) || !string.IsNullOrWhiteSpace(topic.FirstUrl))
                yield return topic;
            foreach (var child in Flatten(topic.SubTopics ?? []))
                yield return child;
        }
    }

    private static string? NormalizeHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? value!.Trim()
            : null;

    public sealed record DuckTopic(string Text, string Url);
    public sealed record ParsedDuckResponse(
        string? Heading,
        string? AbstractText,
        string? AbstractSource,
        string? AbstractUrl,
        IReadOnlyList<DuckTopic> RelatedTopics);
}

public sealed class DuckResponse
{
    [JsonPropertyName("AbstractText")]
    public string? AbstractText { get; set; }

    [JsonPropertyName("AbstractSource")]
    public string? AbstractSource { get; set; }

    [JsonPropertyName("AbstractURL")]
    public string? AbstractUrl { get; set; }

    [JsonPropertyName("Heading")]
    public string? Heading { get; set; }

    [JsonPropertyName("RelatedTopics")]
    public List<RelatedTopic>? RelatedTopics { get; set; }
}

public sealed class RelatedTopic
{
    [JsonPropertyName("Text")]
    public string? Text { get; set; }

    [JsonPropertyName("FirstURL")]
    public string? FirstUrl { get; set; }

    [JsonPropertyName("Topics")]
    public List<RelatedTopic>? SubTopics { get; set; }
}
