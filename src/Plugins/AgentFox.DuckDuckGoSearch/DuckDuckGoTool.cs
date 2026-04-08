using System.Net.Http.Json;
using AgentFox.Http;
using AgentFox.Plugins.Interfaces;

namespace AgentFox.DuckDuckGoSearch
{
    public class DuckDuckGoTool : BaseTool
    {
        private static readonly HttpClient _httpClient =
            HttpResilienceFactory.Create(TimeSpan.FromMinutes(5));

        public override string Name { get; } = "duckduckgo_search";
        public override string Description { get; } = "duckduckgo_search";
        public override Dictionary<string, ToolParameter> Parameters { get; } = new()
        {
            ["query"] = new() { Type = "string", Description = "Search query", Required = true },
        };

        protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
        {
            var query = arguments["query"]?.ToString();
            if (string.IsNullOrEmpty(query))
                return ToolResult.Fail("No query provided");

            // Validate URL format
            if (string.IsNullOrWhiteSpace(query))
                return ToolResult.Fail($"Invalid URL format: {query}");

            var timeoutSeconds = 45;

            try
            {
                // 'q' is query, 'format=json' is required, 'no_html=1' removes tags
                string url = $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(query)}&format=json&no_html=1";

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                var response = await _httpClient.GetAsync(url, cts.Token);
                //return response?.AbstractText ?? "No info found.";

                //using var response = await _httpClient.GetAsync(uri, cts.Token);

                if (!response.IsSuccessStatusCode)
                    return ToolResult.Fail($"HTTP Error {(int)response.StatusCode}: {response.ReasonPhrase}");

                var contentObject = await response.Content.ReadFromJsonAsync<dynamic>(cts.Token);//.ReadAsStringAsync(cts.Token);
                var content = contentObject?.AbstractText?.ToString() ?? "No info found.";
                if (content.Length > 10 * 1024 * 1024)
                    content = content[..(10 * 1024 * 1024)] + "\n... (content truncated - exceeds 10MB limit)";

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
    }
}
