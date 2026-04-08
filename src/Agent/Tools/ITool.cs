using AgentFox.Plugins.Interfaces;

namespace AgentFox.Tools;

/// <summary>
/// Registry for managing available tools with integrated hooks and metrics
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new();
    private readonly object _lock = new();
    private int _version = 0;

    // Event hooks and metrics
    public ToolEventHookRegistry HookRegistry { get; } = new();
    public ToolMetricsCollector MetricsCollector { get; } = new();

    /// <summary>
    /// Monotonically increasing counter, incremented on every Register or Unregister.
    /// Consumers (e.g. DynamicAgentMiddleware) can compare against a cached value
    /// to detect changes without scanning the full tool list.
    /// </summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>
    /// Register a tool
    /// </summary>
    public async Task RegisterAsync(ITool tool)
    {
        lock (_lock)
        {
            _tools[tool.Name] = tool;
        }
        Interlocked.Increment(ref _version);
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
        Interlocked.Increment(ref _version);
        // Fire-and-forget: hooks are observability-only and already swallow all errors
        _ = HookRegistry.InvokeToolRegisteredAsync(tool.Name, tool.Description);
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
        Interlocked.Increment(ref _version);
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
        Interlocked.Increment(ref _version);
        _ = HookRegistry.InvokeToolUnregisteredAsync(name);
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
