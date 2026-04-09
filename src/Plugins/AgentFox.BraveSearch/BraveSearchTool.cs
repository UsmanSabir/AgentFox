using AgentFox.Http;
using AgentFox.Plugins.Interfaces;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;

namespace AgentFox.BraveSearch
{
    //NOTE: No more Free API access
    public class BraveSearchTool(IConfiguration configuration) : BaseTool
    {
        private readonly string _apiKey = configuration["BraveSearch:ApiKey"] ?? throw new InvalidOperationException("BraveSearch:ApiKey is not configured");

        private static readonly HttpClient _httpClient =
            HttpResilienceFactory.Create(TimeSpan.FromMinutes(5));

        public override string Name { get; } = "brave_search";
        public override string Description { get; } = "brave_search";
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

                var response = await GetBraveSearchAsync(query, _apiKey, cts.Token);
                var content = await response.Content.ReadAsStringAsync(cts.Token);
                return ToolResult.Ok($"""
                                      Fetched: {query}
                                      Status: {(int)response.StatusCode} {response.ReasonPhrase}
                                      Content-Type: {response.Content.Headers.ContentType}
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

        public static async Task<HttpResponseMessage> GetBraveSearchAsync(string query, string apiKey,
            CancellationToken ctsToken, int count = 5)
        {
            // Limit results using the 'count' parameter
            string url = $"https://api.search.brave.com/res/v1/web/search" +
                         $"?q={Uri.EscapeDataString(query)}" +
                         $"&count={count}" +
                         $"&offset=0"; // Get the first page

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            
            // Required headers for Brave Search API
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("X-Subscription-Token", apiKey);

            // Optional: Accept-Encoding gzip is recommended for performance
            request.Headers.Add("Accept-Encoding", "gzip");

            var response = await _httpClient.SendAsync(request, ctsToken);
            //response.EnsureSuccessStatusCode();
            return response;//.Content.ReadAsStringAsync();
        }

    }
}
