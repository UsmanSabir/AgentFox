using AgentFox.Plugins.Interfaces;
using AgentFox.Plugins.Research;

namespace AgentFox.TavilySearch;

/// <summary>General-agent compatibility tool backed by the shared Tavily provider.</summary>
public sealed class TavilySearchTool(IWebSearchProvider provider) : BaseTool
{
    public override string Name => "tavily_search";
    public override string Description => "Search the current web with Tavily and return result URLs and snippets.";

    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["query"] = new() { Type = "string", Description = "Search query", Required = true }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var query = arguments.GetValueOrDefault("query")?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Fail("No query provided");

        try
        {
            var response = await provider.SearchAsync(new WebSearchRequest(query));
            var scope = ResearchReferenceScope.Current;
            if (scope is not null)
            {
                foreach (var result in response.Results)
                    scope.Add(result.Url, result.Title, response.Provider);
            }

            return ToolResult.Ok($"""
                                  Fetched: {query}
                                  Provider: {response.Provider}
                                  Answer: {response.Answer}
                                  ═════════════════════════════════════

                                  {string.Join("\n\n", response.Results.Select(r => $"{r.Title}\n{r.Url}\n{r.Content}"))}
                                  """);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to fetch URL: {ex.GetType().Name} - {ex.Message}");
        }
    }
}
