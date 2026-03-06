namespace AgentFox.Tools;

/// <summary>
/// Interface for all tools that can be called by agents
/// </summary>
public interface ITool
{
    /// <summary>
    /// Unique name of the tool
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Description of what the tool does
    /// </summary>
    string Description { get; }
    
    /// <summary>
    /// Parameter definitions
    /// </summary>
    Dictionary<string, ToolParameter> Parameters { get; }
    
    /// <summary>
    /// Execute the tool with the given arguments
    /// </summary>
    Task<ToolResult> ExecuteAsync(Dictionary<string, object?> arguments);
}

/// <summary>
/// Result of tool execution
/// </summary>
public class ToolResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string? Error { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    
    public static ToolResult Ok(string output) => new() { Success = true, Output = output };
    public static ToolResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Parameter definition for tools
/// </summary>
public class ToolParameter
{
    public string Type { get; set; } = "string";
    public string Description { get; set; } = string.Empty;
    public bool Required { get; set; }
    public object? Default { get; set; }
    public List<string>? EnumValues { get; set; }
}

/// <summary>
/// Registry for managing available tools
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new();
    private readonly object _lock = new();
    
    /// <summary>
    /// Register a tool
    /// </summary>
    public void Register(ITool tool)
    {
        lock (_lock)
        {
            _tools[tool.Name] = tool;
        }
    }
    
    /// <summary>
    /// Unregister a tool
    /// </summary>
    public void Unregister(string name)
    {
        lock (_lock)
        {
            _tools.Remove(name);
        }
    }
    
    /// <summary>
    /// Get a tool by name
    /// </summary>
    public ITool? Get(string name)
    {
        lock (_lock)
        {
            return _tools.TryGetValue(name, out var tool) ? tool : null;
        }
    }
    
    /// <summary>
    /// Get all registered tools
    /// </summary>
    public List<ITool> GetAll()
    {
        lock (_lock)
        {
            return _tools.Values.ToList();
        }
    }
    
    /// <summary>
    /// Get tool definitions for LLM consumption
    /// </summary>
    public List<Models.ToolDefinition> GetDefinitions()
    {
        lock (_lock)
        {
            return _tools.Values.Select(t => new Models.ToolDefinition
            {
                Name = t.Name,
                Description = t.Description,
                Parameters = t.Parameters.ToDictionary(p => p.Key, p => new Models.ToolParameter
                {
                    Type = p.Value.Type,
                    Description = p.Value.Description,
                    Required = p.Value.Required,
                    Default = p.Value.Default
                })
            }).ToList();
        }
    }
    
    /// <summary>
    /// Check if a tool exists
    /// </summary>
    public bool Has(string name)
    {
        lock (_lock)
        {
            return _tools.ContainsKey(name);
        }
    }
}

/// <summary>
/// Base class for easier tool implementation
/// </summary>
public abstract class BaseTool : ITool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract Dictionary<string, ToolParameter> Parameters { get; }
    
    protected abstract Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments);
    
    public async Task<ToolResult> ExecuteAsync(Dictionary<string, object?> arguments)
    {
        try
        {
            // Validate required parameters
            foreach (var param in Parameters.Where(p => p.Value.Required))
            {
                if (!arguments.ContainsKey(param.Key) || arguments[param.Key] == null)
                {
                    return ToolResult.Fail($"Missing required parameter: {param.Key}");
                }
            }
            
            return await ExecuteInternalAsync(arguments);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }
}
