using AgentFox.Http;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentFox.Plugins.Interfaces;
using AgentFox.Plugins.Security;
using Microsoft.Extensions.Configuration;
using ToolParameter = AgentFox.Plugins.Interfaces.ToolParameter;

namespace AgentFox.Tools;

/// <summary>
/// Tool for searching the web. Tries Tavily, then Brave (whichever has a key configured —
/// same env vars/config keys as the AgentFox.TavilySearch / AgentFox.BraveSearch plugins), and
/// falls back to keyless Google News RSS (same approach as TradingAgent's PsxDataClient) when
/// neither API key is configured or both requests fail.
/// </summary>
public class WebSearchTool : BaseTool
{
    private static readonly HttpClient _httpClient =
        HttpResilienceFactory.Create(TimeSpan.FromSeconds(45));

    private readonly IConfiguration _configuration;

    public WebSearchTool(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public override string Name => "web_search";
    public override string Description =>
        "Search the web for current information. Uses Tavily or Brave when an API key is configured, " +
        "otherwise falls back to keyless Google News search.";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["query"] = new() { Type = "string", Description = "Search query", Required = true },
        ["num_results"] = new() { Type = "number", Description = "Number of results to return", Required = false, Default = 5 }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var query = arguments["query"]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(query))
            return ToolResult.Fail("No query provided");

        var numResults = arguments.GetValueOrDefault("num_results") is double n
            ? Math.Clamp((int)n, 1, 10)
            : 5;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var errors = new List<string>();

        var tavilyKey = FirstRealKey(
            Environment.GetEnvironmentVariable("TAVILY_API_KEY"),
            _configuration["Tavily:ApiKey"],
            _configuration["Plugins:Tavily:ApiKey"]);
        if (tavilyKey is not null)
        {
            try { return await SearchTavilyAsync(query, numResults, tavilyKey, timeout.Token); }
            catch (Exception ex) { errors.Add($"Tavily: {ex.Message}"); }
        }

        var braveKey = FirstRealKey(
            Environment.GetEnvironmentVariable("BRAVE_SEARCH_API_KEY"),
            _configuration["BraveSearch:ApiKey"],
            _configuration["Plugins:BraveSearch:ApiKey"]);
        if (braveKey is not null)
        {
            try { return await SearchBraveAsync(query, numResults, braveKey, timeout.Token); }
            catch (Exception ex) { errors.Add($"Brave: {ex.Message}"); }
        }

        try
        {
            return await SearchGoogleNewsAsync(query, numResults, errors, timeout.Token);
        }
        catch (Exception ex)
        {
            errors.Add($"Google News: {ex.Message}");
            return ToolResult.Fail($"All web search providers failed for '{query}'. " + string.Join(" | ", errors));
        }
    }

    private static async Task<ToolResult> SearchTavilyAsync(
        string query, int numResults, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
        {
            Content = JsonContent.Create(new
            {
                query,
                search_depth = "basic",
                max_results = numResults,
                topic = "general",
                include_answer = true,
                include_raw_content = false,
                include_images = false
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var answer = root.TryGetProperty("answer", out var a) ? a.GetString() : null;
        var results = root.TryGetProperty("results", out var r)
            ? r.EnumerateArray().Select(e => (
                Title: e.GetProperty("title").GetString() ?? "",
                Url: e.GetProperty("url").GetString() ?? "",
                Content: e.GetProperty("content").GetString() ?? "")).ToList()
            : [];

        if (results.Count == 0)
            throw new InvalidOperationException($"zero results for '{query}'");

        return ToolResult.Ok(FormatResults(query, "tavily", answer, results));
    }

    private static async Task<ToolResult> SearchBraveAsync(
        string query, int numResults, string apiKey, CancellationToken ct)
    {
        var url = "https://api.search.brave.com/res/v1/web/search" +
                  $"?q={Uri.EscapeDataString(query)}" +
                  $"&count={Math.Clamp(numResults, 1, 20)}" +
                  "&offset=0&result_filter=web&text_decorations=false";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Subscription-Token", apiKey);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.TryGetProperty("web", out var web) &&
                      web.TryGetProperty("results", out var arr)
            ? arr.EnumerateArray().Select(e => (
                Title: e.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                Url: e.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                Content: e.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "")).ToList()
            : [];

        if (results.Count == 0)
            throw new InvalidOperationException($"zero results for '{query}'");

        return ToolResult.Ok(FormatResults(query, "brave", null, results));
    }

    /// <summary>
    /// Keyless fallback via Google News RSS — same endpoint/UA workaround as
    /// TradingAgent.Research.PsxDataClient. Headline-only, not a general web search,
    /// but requires no API key.
    /// </summary>
    private static async Task<ToolResult> SearchGoogleNewsAsync(
        string query, int numResults, List<string> priorErrors, CancellationToken ct)
    {
        var url = "https://news.google.com/rss/search?q=" + Uri.EscapeDataString(query) + "&hl=en-US&gl=US&ceid=US:en";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Google News RSS (and some CDNs) reject requests without a User-Agent.
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; AgentFox-WebSearch/1.0)");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        var xml = await response.Content.ReadAsStringAsync(ct);
        var feed = XDocument.Parse(xml);
        var results = feed.Descendants("item")
            .Take(Math.Max(1, numResults))
            .Select(item => (
                Title: item.Element("title")?.Value.Trim() ?? "",
                Url: item.Element("link")?.Value.Trim() ?? "",
                Content: item.Elements().FirstOrDefault(e => e.Name.LocalName == "source")?.Value.Trim() ?? ""))
            .Where(r => r.Title.Length > 0)
            .ToList();

        if (results.Count == 0)
            return ToolResult.Fail(
                $"No results for '{query}' from any provider. " +
                (priorErrors.Count > 0 ? string.Join(" | ", priorErrors) + " | " : "") +
                "Google News returned no items. Configure TAVILY_API_KEY or BRAVE_SEARCH_API_KEY for general web search.");

        var note = priorErrors.Count > 0
            ? $"\nNote: fell back to Google News after: {string.Join(" | ", priorErrors)}\n"
            : "\nNote: this is a keyless Google News fallback (headlines only). " +
              "Configure TAVILY_API_KEY or BRAVE_SEARCH_API_KEY for general web search.\n";

        return ToolResult.Ok(FormatResults(query, "google_news", null, results) + note);
    }

    private static string FormatResults(
        string query, string provider, string? answer, List<(string Title, string Url, string Content)> results)
    {
        var body = string.Join("\n\n", results.Select(r => $"{r.Title}\n{r.Url}\n{r.Content}"));
        var answerLine = string.IsNullOrWhiteSpace(answer) ? "" : $"Answer: {answer}\n";
        return $"""
            Web Search Results for: {query}
            Provider: {provider}
            {answerLine}═════════════════════════════════════

            {body}
            """;
    }

    private static string? FirstRealKey(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v =>
            !string.IsNullOrWhiteSpace(v) &&
            !v.Contains("your-", StringComparison.OrdinalIgnoreCase) &&
            !v.Contains("your_", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Tool for fetching URLs with HttpClient, error handling, and retry logic
/// </summary>
public class FetchUrlTool : BaseTool
{
    // Resilient client: 3 retries with exponential back-off + circuit-breaker.
    // Per-request timeout is enforced via CancellationTokenSource below.
    private static readonly HttpClient _httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = HttpResilienceFactory.Create(TimeSpan.FromMinutes(5));
        // Many sites/CDNs return 406/403 for requests with no User-Agent — .NET's HttpClient
        // sends none by default. Mimic a browser (same workaround as PsxDataClient's news fetch).
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        return client;
    }

    public override string Name => "fetch_url";
    public override string Description => "Fetch content from a URL";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["url"] = new() { Type = "string", Description = "URL to fetch", Required = true },
        ["timeout_seconds"] = new() { Type = "number", Description = "Timeout in seconds (optional)", Required = false, Default = 30 }
    };

    // ~24k chars ≈ ~6k tokens — safe for most local models (8k–32k context)
    // while leaving room for the system prompt, tool definitions, and reply.
    private const int MaxContentChars = 24_000;

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        //TODO: use html to markdown converter like https://github.com/mysticmind/reversemarkdown-net or https://github.com/baynezy/Html2Markdown
        var url = arguments["url"]?.ToString();
        if (string.IsNullOrEmpty(url))
            return ToolResult.Fail("No URL provided");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return ToolResult.Fail($"Invalid URL format: {url}");

        // Exfiltration gate: if a live credential ever does reach the model, it must not be
        // able to post it back out through a URL it controls.
        if (SecretGuard.ContainsSecret(url))
            return ToolResult.Fail(
                "Request refused: the URL contains a credential. Secrets must never be sent to " +
                "third-party endpoints.");

        var timeoutSeconds = arguments.GetValueOrDefault("timeout_seconds") is double timeout
            ? (int)timeout
            : 30;
        timeoutSeconds = Math.Max(5, Math.Min(300, timeoutSeconds));

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var response = await _httpClient.GetAsync(uri, cts.Token);

            if (!response.IsSuccessStatusCode)
                return ToolResult.Fail($"HTTP Error {(int)response.StatusCode}: {response.ReasonPhrase}");

            var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
            var raw = await response.Content.ReadAsStringAsync(cts.Token);

            // Strip HTML tags and collapse whitespace so the model sees readable text,
            // not markup — this reduces token count by 60-80% for typical web pages.
            var isHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                         || raw.TrimStart().StartsWith('<');
            var content = isHtml ? StripHtml(raw) : raw;

            var truncated = false;
            if (content.Length > MaxContentChars)
            {
                content = content[..MaxContentChars];
                truncated = true;
            }

            var footer = truncated ? $"\n\n[Content truncated at {MaxContentChars:N0} chars]" : string.Empty;

            return ToolResult.Ok($"""
                Fetched: {url}
                Status: {(int)response.StatusCode} {response.ReasonPhrase}
                Content-Type: {contentType}
                ═════════════════════════════════════

                {content}{footer}
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

    /// <summary>
    /// Strips HTML/XML tags, decodes common entities, and collapses whitespace.
    /// Produces plain readable text suitable for LLM consumption.
    /// </summary>
    private static string StripHtml(string html)
    {
        // Remove <script> and <style> blocks entirely
        var clean = Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
        // Remove all remaining tags
        clean = Regex.Replace(clean, @"<[^>]+>", " ");
        // Decode common HTML entities
        clean = clean
            .Replace("&amp;",  "&")
            .Replace("&lt;",   "<")
            .Replace("&gt;",   ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;",  "'")
            .Replace("&nbsp;", " ");
        // Collapse whitespace and blank lines
        clean = Regex.Replace(clean, @"[ \t]+", " ");
        clean = Regex.Replace(clean, @"\n{3,}", "\n\n");
        return clean.Trim();
    }
}

/// <summary>
/// Tool for calculating expressions
/// </summary>
public class CalculatorTool : BaseTool
{
    public override string Name => "calculate";
    public override string Description => "Evaluate a mathematical expression";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["expression"] = new() { Type = "string", Description = "Mathematical expression to evaluate", Required = true }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var expression = arguments["expression"]?.ToString();
        if (string.IsNullOrEmpty(expression))
            return Task.FromResult(ToolResult.Fail("No expression provided"));
        
        try
        {
            // WARNING: This is a simple evaluator - not safe for production!
            // In production, use a proper expression parser
            var result = EvaluateExpression(expression);
            return Task.FromResult(ToolResult.Ok($"Result: {result}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Evaluation error: {ex.Message}"));
        }
    }
    
    private double EvaluateExpression(string expression)
    {
        // Simple evaluation - supports +, -, *, /, and parentheses
        expression = expression.Replace(" ", "");
        
        return ParseAddSub(expression, 0, out _);
    }
    
    private double ParseAddSub(string expr, int pos, out int newPos)
    {
        var left = ParseMulDiv(expr, pos, out pos);
        
        while (pos < expr.Length && (expr[pos] == '+' || expr[pos] == '-'))
        {
            var op = expr[pos];
            pos++;
            var right = ParseMulDiv(expr, pos, out pos);
            
            if (op == '+')
                left = left + right;
            else
                left = left - right;
        }
        
        newPos = pos;
        return left;
    }
    
    private double ParseMulDiv(string expr, int pos, out int newPos)
    {
        var left = ParsePrimary(expr, pos, out pos);
        
        while (pos < expr.Length && (expr[pos] == '*' || expr[pos] == '/'))
        {
            var op = expr[pos];
            pos++;
            var right = ParsePrimary(expr, pos, out pos);
            
            if (op == '*')
                left = left * right;
            else
                left = left / right;
        }
        
        newPos = pos;
        return left;
    }
    
    private double ParsePrimary(string expr, int pos, out int newPos)
    {
        if (pos < expr.Length && expr[pos] == '(')
        {
            pos++;
            var result = ParseAddSub(expr, pos, out pos);
            if (pos < expr.Length && expr[pos] == ')')
                pos++;
            newPos = pos;
            return result;
        }
        
        return ParseNumber(expr, pos, out newPos);
    }
    
    private double ParseNumber(string expr, int pos, out int newPos)
    {
        var start = pos;
        while (pos < expr.Length && (char.IsDigit(expr[pos]) || expr[pos] == '.'))
            pos++;
        
        var numStr = expr[start..pos];
        newPos = pos;
        return double.Parse(numStr);
    }
}

/// <summary>
/// Tool for generating UUIDs
/// </summary>
public class UuidTool : BaseTool
{
    public override string Name => "uuid";
    public override string Description => "Generate a UUID";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["count"] = new() { Type = "number", Description = "Number of UUIDs to generate", Required = false, Default = 1 }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var count = Convert.ToInt32(arguments.GetValueOrDefault("count") ?? 1);
        count = Math.Min(100, Math.Max(1, count));
        
        var uuids = new List<string>();
        for (int i = 0; i < count; i++)
        {
            uuids.Add(Guid.NewGuid().ToString());
        }
        
        return Task.FromResult(ToolResult.Ok(string.Join("\n", uuids)));
    }
}

/// <summary>
/// Tool for getting current timestamp
/// </summary>
public class TimestampTool : BaseTool
{
    public override string Name => "timestamp";
    public override string Description => "Get current timestamp";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["format"] = new() { Type = "string", Description = "Format string (optional)", Required = false }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var now = DateTime.UtcNow;
        var format = arguments.GetValueOrDefault("format")?.ToString();
        
        var result = string.IsNullOrEmpty(format)
            ? $"UTC: {now:yyyy-MM-dd HH:mm:ss.fff}\nLocal: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\nUnix: {new DateTimeOffset(now).ToUnixTimeSeconds()}"
            : now.ToString(format);
        
        return Task.FromResult(ToolResult.Ok(result));
    }
}
