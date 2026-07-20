namespace AgentFox.Plugins.Research;

/// <summary>Provider-neutral request for a read-only web search.</summary>
public sealed record WebSearchRequest(
    string Query,
    int MaxResults = 5,
    string SearchDepth = "basic",
    bool IncludeAnswer = true);

/// <summary>A single provider result. Content is untrusted external text.</summary>
public sealed record WebSearchResult(
    string Title,
    string Url,
    string Content,
    double? Score = null);

/// <summary>Provider-neutral response returned to an AgentFox research tool.</summary>
public sealed record WebSearchResponse(
    string Query,
    IReadOnlyList<WebSearchResult> Results,
    string? Answer,
    string Provider,
    DateTime RetrievedAtUtc);

/// <summary>
/// Read-only web-search capability. Implementations must not expose mutating actions or credentials
/// to callers, and must return source URLs for every result that influenced the response.
/// </summary>
public interface IWebSearchProvider
{
    string Name { get; }

    Task<WebSearchResponse> SearchAsync(
        WebSearchRequest request,
        CancellationToken cancellationToken = default);
}
