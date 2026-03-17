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
/// Result of tool execution with enhanced metadata
/// </summary>
public class ToolResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string? Error { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    
    // Enhanced metadata for observability
    public string? ToolCallId { get; set; }           // Unique ID for this invocation
    public long ExecutionTimeMs { get; set; }         // Execution duration
    public string? ToolVersion { get; set; }          // Version of the tool
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
    
    public static ToolResult Ok(string output) => new() { Success = true, Output = output };
    public static ToolResult Fail(string error) => new() { Success = false, Error = error };
    
    /// <summary>
    /// Create a success result with execution metadata
    /// </summary>
    public static ToolResult OkWithMetadata(string output, long executionTimeMs, string? toolVersion = null, string? toolCallId = null)
    {
        return new()
        {
            Success = true,
            Output = output,
            ExecutionTimeMs = executionTimeMs,
            ToolVersion = toolVersion,
            ToolCallId = toolCallId,
            ExecutedAt = DateTime.UtcNow
        };
    }
    
    /// <summary>
    /// Create a failure result with execution metadata
    /// </summary>
    public static ToolResult FailWithMetadata(string error, long executionTimeMs, string? toolVersion = null, string? toolCallId = null)
    {
        return new()
        {
            Success = false,
            Error = error,
            ExecutionTimeMs = executionTimeMs,
            ToolVersion = toolVersion,
            ToolCallId = toolCallId,
            ExecutedAt = DateTime.UtcNow
        };
    }
}

/// <summary>
/// Parameter definition for tools with enhanced schema support
/// </summary>
public class ToolParameter
{
    public string Type { get; set; } = "string";
    public string Description { get; set; } = string.Empty;
    public bool Required { get; set; }
    public object? Default { get; set; }
    public List<string>? EnumValues { get; set; }
    
    // Enhanced schema support for OpenClaw compatibility
    public string? JsonSchema { get; set; }        // Full JSON Schema (optional, overrides Type)
    public object? Example { get; set; }           // Example value for documentation
    public string? Pattern { get; set; }           // Regex pattern (for string validation)
    public int? MinLength { get; set; }            // Min string length
    public int? MaxLength { get; set; }            // Max string length
    public decimal? Minimum { get; set; }          // Min numeric value
    public decimal? Maximum { get; set; }          // Max numeric value
}

/// <summary>
/// Registry for managing available tools with integrated hooks and metrics
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new();
    private readonly object _lock = new();
    
    // Event hooks and metrics
    public ToolEventHookRegistry HookRegistry { get; } = new();
    public ToolMetricsCollector MetricsCollector { get; } = new();
    
    /// <summary>
    /// Register a tool
    /// </summary>
    public async Task RegisterAsync(ITool tool)
    {
        lock (_lock)
        {
            _tools[tool.Name] = tool;
        }
        
        // Invoke registration hook
        await HookRegistry.InvokeToolRegisteredAsync(tool.Name, tool.Description);
    }
    
    /// <summary>
    /// Register a tool (synchronous, for backward compatibility)
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
    public async Task UnregisterAsync(string name)
    {
        lock (_lock)
        {
            _tools.Remove(name);
        }
        
        // Invoke unregistration hook
        await HookRegistry.InvokeToolUnregisteredAsync(name);
    }
    
    /// <summary>
    /// Unregister a tool (synchronous, for backward compatibility)
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
                    Default = p.Value.Default,
                    JsonSchema = p.Value.JsonSchema,
                    Example = p.Value.Example
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
/// Base class for easier tool implementation with integrated hooks and metrics
/// </summary>
public abstract class BaseTool : ITool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract Dictionary<string, ToolParameter> Parameters { get; }
    
    /// <summary>
    /// Optional tool version for metrics tracking
    /// </summary>
    public virtual string? ToolVersion => null;
    
    protected abstract Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments);
    
    public async Task<ToolResult> ExecuteAsync(Dictionary<string, object?> arguments)
    {
        var executionId = Guid.NewGuid().ToString();
        var startTime = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            // Validate required parameters
            foreach (var param in Parameters.Where(p => p.Value.Required))
            {
                if (!arguments.ContainsKey(param.Key) || arguments[param.Key] == null)
                {
                    var error = $"Missing required parameter: {param.Key}";
                    startTime.Stop();
                    // Note: Hooks are not called here as there's no registry available in base class
                    return ToolResult.FailWithMetadata(error, startTime.ElapsedMilliseconds, ToolVersion, executionId);
                }
            }
            
            var result = await ExecuteInternalAsync(arguments);
            startTime.Stop();
            
            // Enhance result with metadata
            result.ToolCallId = executionId;
            result.ExecutionTimeMs = startTime.ElapsedMilliseconds;
            result.ToolVersion = ToolVersion;
            result.ExecutedAt = DateTime.UtcNow;
            
            return result;
        }
        catch (Exception ex)
        {
            startTime.Stop();
            var error = $"{Name} execution error: {ex.Message}";
            return ToolResult.FailWithMetadata(error, startTime.ElapsedMilliseconds, ToolVersion, executionId);
        }
    }
}
