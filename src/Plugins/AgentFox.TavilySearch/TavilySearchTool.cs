using System.Text.Json;
using AgentFox.Http;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Configuration;
using Tavily;

namespace AgentFox.TavilySearch;

//https://app.tavily.com/playground
public class TavilySearchTool(IConfiguration configuration) : BaseTool
{
    private readonly string _apiKey = configuration["Tavily:ApiKey"] ?? throw new InvalidOperationException("Tavily:ApiKey is not configured");

    private static readonly HttpClient _httpClient =
        HttpResilienceFactory.Create(TimeSpan.FromMinutes(5));

    public override string Name { get; } = "tavily_search";
    public override string Description { get; } = "tavily_search";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["query"] = new() { Type = "string", Description = "Search query", Required = true },
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var query = arguments["query"]?.ToString();
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Fail("No query provided");

        var timeoutSeconds = 45;
        try
        {

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var apiKey =
                Environment.GetEnvironmentVariable("TAVILY_API_KEY") ??
                _apiKey ?? throw new InvalidOperationException("TAVILY_API_KEY environment variable is not found.");

            using var client = new TavilyClient(_httpClient);
            var depth = configuration["Tavily:SearchDepth"];
            var includeAnswer= configuration.GetValue<bool>("Tavily:IncludeAnswer", true);
            var maxResults= configuration.GetValue<int>("Tavily:MaxResults", 5);
            var searchDepth = depth?.ToLower().Trim() == "advanced" ? SearchRequestSearchDepth.Advanced : SearchRequestSearchDepth.Basic;
            var searchRequest = new SearchRequest()
            {
                ApiKey = apiKey,
                Query = query,
                IncludeImages = false,
                MaxResults = maxResults,
                SearchDepth = searchDepth,
                IncludeAnswer = includeAnswer
            };

            var searchResult = await client.SearchAsync(searchRequest, cts.Token);
            var content = JsonSerializer.Serialize(searchResult.Results);

            //foreach (var result in searchResult.Results)
            //{
            //    Console.WriteLine($"Title: {result.Title}");
            //    Console.WriteLine($"Content: {result.Content}");
            //    Console.WriteLine($"Score: {result.Score}");
            //    Console.WriteLine($"Url: {result.Url}");
            //    Console.WriteLine();
            //}

            return ToolResult.Ok($"""
                                      Fetched: {query}
                                      Status: 200 {(string.IsNullOrWhiteSpace(searchResult.Answer) ? string.Empty:  searchResult.Answer)}
                                      ═════════════════════════════════════

                                      {content}
                                      """);
        }
        catch (TaskCanceledException)
        {
            return ToolResult.Fail($"Request timeout after {timeoutSeconds} seconds");
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("Request was cancelled");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to fetch URL: {ex.GetType().Name} - {ex.Message}");
        }
    }
}