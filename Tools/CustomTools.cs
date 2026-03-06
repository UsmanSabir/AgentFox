using AgentFox.Models;

namespace AgentFox.Tools;

/// <summary>
/// Tool for searching the web (simulated)
/// </summary>
public class WebSearchTool : BaseTool
{
    public override string Name => "web_search";
    public override string Description => "Search the web for information";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["query"] = new() { Type = "string", Description = "Search query", Required = true },
        ["num_results"] = new() { Type = "number", Description = "Number of results to return", Required = false, Default = 5 }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var query = arguments["query"]?.ToString();
        if (string.IsNullOrEmpty(query))
            return Task.FromResult(ToolResult.Fail("No query provided"));
        
        // Simulated web search results
        var results = $"""
            Web Search Results for: {query}
            ═════════════════════════════════════
            
            1. Example Result 1
               https://example.com/1
               Description of the first result...
            
            2. Example Result 2
               https://example.com/2
               Description of the second result...
            
            3. Example Result 3
               https://example.com/3
               Description of the third result...
            
            Note: This is a simulated web search. 
            Integrate with a real search API for actual results.
            """;
        
        return Task.FromResult(ToolResult.Ok(results));
    }
}

/// <summary>
/// Tool for fetching URLs (simulated)
/// </summary>
public class FetchUrlTool : BaseTool
{
    public override string Name => "fetch_url";
    public override string Description => "Fetch content from a URL";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["url"] = new() { Type = "string", Description = "URL to fetch", Required = true }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var url = arguments["url"]?.ToString();
        if (string.IsNullOrEmpty(url))
            return Task.FromResult(ToolResult.Fail("No URL provided"));
        
        // Simulated fetch
        var result = $"""
            Fetched: {url}
            ═════════════════════════════════════
            
            <html>
                <body>
                    <h1>Simulated Page Content</h1>
                    <p>This is a placeholder for actual URL content.</p>
                </body>
            </html>
            
            Note: This is a simulated URL fetch.
            Integrate with HttpClient for actual fetching.
            """;
        
        return Task.FromResult(ToolResult.Ok(result));
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
        var format = arguments["format"]?.ToString();
        
        var result = string.IsNullOrEmpty(format)
            ? $"UTC: {now:yyyy-MM-dd HH:mm:ss.fff}\nLocal: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\nUnix: {new DateTimeOffset(now).ToUnixTimeSeconds()}"
            : now.ToString(format);
        
        return Task.FromResult(ToolResult.Ok(result));
    }
}
