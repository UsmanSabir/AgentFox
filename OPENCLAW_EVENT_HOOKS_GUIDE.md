# OpenClaw-Inspired Event Hooks & Metrics - Implementation Guide

## Overview

The event hooks system provides OpenClaw-inspired lifecycle event coverage for the entire tool and skill ecosystem. This enables:

- **Observability**: Monitor tool execution in real-time
- **Metrics Collection**: Automatic performance tracking
- **Custom Workflows**: React to tool lifecycle events
- **Debugging**: Trace tool invocations with correlation IDs
- **Compliance**: Audit tool usage and skill enablement

## Architecture

```
┌─────────────────────────────────────────────────┐
│      ToolEventHookRegistry                      │
│  (Centralized Event Hub)                        │
├─────────────────────────────────────────────────┤
│                                                 │
│  Tool Hooks:              Skill Hooks:          │
│  • OnToolPreExecute       • OnSkillPreEnable    │
│  • OnToolPostExecute      • OnSkillPostEnable   │
│  • OnToolError            • OnSkillError        │
│  • OnToolRegistered       • OnSkillDisabled     │
│  • OnToolUnregistered                           │
│                                                 │
└─────────────────────────────────────────────────┘
         ↑                              ↑
         │                              │
    Integrated into          Integrated into
    ToolRegistry             SkillRegistry
```

## Component Reference

### 1. Event Hook Interfaces

```csharp
// Tool Execution Hooks
public delegate Task ToolPreExecuteHook(string toolName, Dictionary<string, object?> arguments, string executionId);
public delegate Task ToolPostExecuteHook(string toolName, ToolResult result, long executionTimeMs, string executionId);
public delegate Task ToolErrorHook(string toolName, string error, long executionTimeMs, string executionId);
public delegate Task ToolRegisteredHook(string name, string description);
public delegate Task ToolUnregisteredHook(string name);

// Skill Lifecycle Hooks
public delegate Task SkillPreEnableHook(string skillName);
public delegate Task SkillPostEnableHook(string skillName, int toolCount);
public delegate Task SkillErrorHook(string skillName, string error);
public delegate Task SkillDisabledHook(string skillName);
```

### 2. ToolEventHookRegistry

Central registry for all event subscriptions:

```csharp
public class ToolEventHookRegistry
{
    // Tool hooks
    public event ToolPreExecuteHook? OnToolPreExecute;
    public event ToolPostExecuteHook? OnToolPostExecute;
    public event ToolErrorHook? OnToolError;
    public event ToolRegisteredHook? OnToolRegistered;
    public event ToolUnregisteredHook? OnToolUnregistered;
    
    // Skill hooks
    public event SkillPreEnableHook? OnSkillPreEnable;
    public event SkillPostEnableHook? OnSkillPostEnable;
    public event SkillErrorHook? OnSkillError;
    public event SkillDisabledHook? OnSkillDisabled;
    
    // Invocation methods (safe, swallow errors)
    public async Task InvokeToolPreExecuteAsync(string toolName, Dictionary<string, object?> arguments, string executionId)
    public async Task InvokeToolPostExecuteAsync(string toolName, ToolResult result, long executionTimeMs, string executionId)
    public async Task InvokeToolErrorAsync(string toolName, string error, long executionTimeMs, string executionId)
    public async Task InvokeToolRegisteredAsync(string name, string description)
    public async Task InvokeToolUnregisteredAsync(string name)
    public async Task InvokeSkillPreEnableAsync(string skillName)
    public async Task InvokeSkillPostEnableAsync(string skillName, int toolCount)
    public async Task InvokeSkillErrorAsync(string skillName, string error)
    public async Task InvokeSkillDisabledAsync(string skillName)
    
    public void ClearAllHooks()
}
```

### 3. Tool Metrics & Tracking

```csharp
// Single tool metrics
public class ToolExecutionMetrics
{
    public string ToolName { get; set; }
    public int ExecutionCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public double AverageExecutionTimeMs { get; set; }
    public long MinExecutionTimeMs { get; set; }
    public long MaxExecutionTimeMs { get; set; }
    public DateTime FirstExecutedAt { get; set; }
    public DateTime LastExecutedAt { get; set; }
    public string? ToolVersion { get; set; }
    public double SuccessRate { get; }  // (SuccessCount / ExecutionCount) * 100
}

// Metrics collector
public class ToolMetricsCollector
{
    public void RecordSuccess(string toolName, long executionTimeMs, string? version = null)
    public void RecordFailure(string toolName, long executionTimeMs, string? version = null)
    public ToolExecutionMetrics? GetMetrics(string toolName)
    public List<ToolExecutionMetrics> GetAllMetrics()
    public List<ToolExecutionMetrics> GetMetricsByUsage()     // Ordered by call count
    public List<ToolExecutionMetrics> GetMetricsByFailureRate()  // Ordered by failure rate
    public List<ToolExecutionMetrics> GetMetricsBySlowest()   // Ordered by duration
    public (int TotalTools, int TotalExecutions, double AverageSuccessRate) GetSummaryStatistics()
    public void ResetMetrics(string toolName)
    public void ClearAll()
}
```

### 4. Enhanced ToolResult

```csharp
public class ToolResult
{
    // Original fields
    public bool Success { get; set; }
    public string Output { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
    
    // Enhanced metadata
    public string? ToolCallId { get; set; }        // Unique execution ID for tracing
    public long ExecutionTimeMs { get; set; }      // Execution duration in milliseconds
    public string? ToolVersion { get; set; }       // Tool version at execution time
    public DateTime ExecutedAt { get; set; }       // Execution timestamp
    
    // Factory methods
    public static ToolResult Ok(string output)
    public static ToolResult Fail(string error)
    public static ToolResult OkWithMetadata(string output, long executionTimeMs, string? toolVersion = null, string? toolCallId = null)
    public static ToolResult FailWithMetadata(string error, long executionTimeMs, string? toolVersion = null, string? toolCallId = null)
}
```

### 5. Enhanced Tool Parameter

```csharp
public class ToolParameter
{
    // Original fields
    public string Type { get; set; }          // "string", "number", "boolean", etc.
    public string Description { get; set; }
    public bool Required { get; set; }
    public object? Default { get; set; }
    public List<string>? EnumValues { get; set; }
    
    // Enhanced schema support
    public string? JsonSchema { get; set; }   // Full JSON Schema (overrides Type)
    public object? Example { get; set; }      // Example value for documentation
    public string? Pattern { get; set; }      // Regex pattern for validation
    public int? MinLength { get; set; }       // Min string length
    public int? MaxLength { get; set; }       // Max string length
    public decimal? Minimum { get; set; }     // Min numeric value
    public decimal? Maximum { get; set; }     // Max numeric value
}
```

## Usage Examples

### Example 1: Real-Time Execution Monitoring

```csharp
var toolRegistry = new ToolRegistry();
var hookRegistry = toolRegistry.HookRegistry;

// Subscribe to execution events
hookRegistry.OnToolPreExecute += async (toolName, args, executionId) =>
{
    Console.WriteLine($"[{executionId}] Executing {toolName} with args: {string.Join(", ", args.Keys)}");
    await Task.CompletedTask;
};

hookRegistry.OnToolPostExecute += async (toolName, result, timeMs, executionId) =>
{
    Console.WriteLine($"[{executionId}] {toolName} completed in {timeMs}ms - Success: {result.Success}");
    await Task.CompletedTask;
};

hookRegistry.OnToolError += async (toolName, error, timeMs, executionId) =>
{
    Console.WriteLine($"[{executionId}] {toolName} FAILED after {timeMs}ms: {error}");
    await Task.CompletedTask;
};

// Now execute tools normally - hooks are invoked automatically
var tool = toolRegistry.Get("shell");
var result = await tool.ExecuteAsync(new { command = "echo hello" });
// Output: [uuid-here] Executing shell with args: command
//         [uuid-here] shell completed in 45ms - Success: True
```

### Example 2: Metrics Collection & Analysis

```csharp
var metrics = toolRegistry.MetricsCollector;

// After tools have been executed...

var stats = metrics.GetSummaryStatistics();
Console.WriteLine($"Total tools: {stats.TotalTools}");
Console.WriteLine($"Total executions: {stats.TotalExecutions}");
Console.WriteLine($"Average success rate: {stats.AverageSuccessRate:F1}%");

// Find slowest tools
var slowest = metrics.GetMetricsBySlowest();
foreach (var metric in slowest.Take(5))
{
    Console.WriteLine($"{metric.ToolName}:");
    Console.WriteLine($"  Calls: {metric.ExecutionCount}");
    Console.WriteLine($"  Avg time: {metric.AverageExecutionTimeMs:F0}ms");
    Console.WriteLine($"  Success rate: {metric.SuccessRate:F1}%");
}

// Find unreliable tools
var unreliable = metrics.GetMetricsByFailureRate().Where(m => m.SuccessRate < 80);
foreach (var metric in unreliable)
{
    Console.WriteLine($"WARNING: {metric.ToolName} has {100 - metric.SuccessRate:F1}% failure rate");
}
```

### Example 3: Skill Lifecycle Events

```csharp
var skillRegistry = new SkillRegistry(toolRegistry);
var hookRegistry = skillRegistry.HookRegistry;

// Subscribe to skill events
hookRegistry.OnSkillPreEnable += async (skillName) =>
{
    Console.WriteLine($"Enabling skill: {skillName}");
    await Task.CompletedTask;
};

hookRegistry.OnSkillPostEnable += async (skillName, toolCount) =>
{
    Console.WriteLine($"✓ Skill {skillName} enabled with {toolCount} tools");
    await Task.CompletedTask;
};

hookRegistry.OnSkillError += async (skillName, error) =>
{
    Console.WriteLine($"✗ Error enabling {skillName}: {error}");
    await Task.CompletedTask;
};

hookRegistry.OnSkillDisabled += async (skillName) =>
{
    Console.WriteLine($"Skill {skillName} disabled");
    await Task.CompletedTask;
};

// Enable a skill
await skillRegistry.EnableSkillAsync("git");
// Output: Enabling skill: git
//         ✓ Skill git enabled with 6 tools

// Disable a skill
await skillRegistry.DisableSkillAsync("git");
// Output: Skill git disabled
```

### Example 4: Dependency Resolution

```csharp
var skillRegistry = new SkillRegistry(toolRegistry);

// Check dependencies for a skill
var deps = skillRegistry.GetDependencyTree("complex_skill");
Console.WriteLine($"Dependencies for complex_skill: {string.Join(", ", deps)}");

// Enable a skill - dependencies are automatically enabled
try
{
    await skillRegistry.EnableSkillAsync("complex_skill");
    // This will also enable any dependencies automatically
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"Failed to enable skill: {ex.Message}");
}

// Get all enabled skills
var enabled = skillRegistry.GetEnabledSkills();
Console.WriteLine($"Enabled skills: {string.Join(", ", enabled.Select(s => s.Name))}");

// Check if a specific skill is enabled
if (skillRegistry.IsSkillEnabled("git"))
{
    Console.WriteLine("Git skill is active");
}
```

### Example 5: Tool Registration Tracking

```csharp
var toolRegistry = new ToolRegistry();
var hookRegistry = toolRegistry.HookRegistry;

// Track tool registration
hookRegistry.OnToolRegistered += async (name, description) =>
{
    Console.WriteLine($"Tool registered: {name}");
    Console.WriteLine($"  Description: {description}");
    await Task.CompletedTask;
};

hookRegistry.OnToolUnregistered += async (name) =>
{
    Console.WriteLine($"Tool unregistered: {name}");
    await Task.CompletedTask;
};

// Register a tool
var myTool = new MyCustomTool();
await toolRegistry.RegisterAsync(myTool);
// Output: Tool registered: my_tool
//         Description: Does something useful
```

### Example 6: Comprehensive Event Logging

```csharp
public class ToolEventLogger
{
    private readonly ToolEventHookRegistry _hooks;
    
    public ToolEventLogger(ToolRegistry toolRegistry)
    {
        _hooks = toolRegistry.HookRegistry;
        await SetupHooksAsync();
    }
    
    private async Task SetupHooksAsync()
    {
        _hooks.OnToolPreExecute += LogToolPreExecute;
        _hooks.OnToolPostExecute += LogToolPostExecute;
        _hooks.OnToolError += LogToolError;
        _hooks.OnToolRegistered += LogToolRegistered;
        _hooks.OnToolUnregistered += LogToolUnregistered;
        _hooks.OnSkillPreEnable += LogSkillPreEnable;
        _hooks.OnSkillPostEnable += LogSkillPostEnable;
        _hooks.OnSkillError += LogSkillError;
        _hooks.OnSkillDisabled += LogSkillDisabled;
        
        await Task.CompletedTask;
    }
    
    private async Task LogToolPreExecute(string toolName, Dictionary<string, object?> args, string executionId)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[PRE-EXEC] {DateTime.UtcNow:O} | Tool: {toolName} | ID: {executionId} | Args: {args.Count}");
        await Task.CompletedTask;
    }
    
    private async Task LogToolPostExecute(string toolName, ToolResult result, long timeMs, string executionId)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[POST-EXEC] {DateTime.UtcNow:O} | Tool: {toolName} | ID: {executionId} | Time: {timeMs}ms | Status: {(result.Success ? "OK" : "FAIL")}");
        await Task.CompletedTask;
    }
    
    private async Task LogToolError(string toolName, string error, long timeMs, string executionId)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[ERROR] {DateTime.UtcNow:O} | Tool: {toolName} | ID: {executionId} | Error: {error}");
        await Task.CompletedTask;
    }
    
    private async Task LogToolRegistered(string name, string description)
    {
        System.Diagnostics.Debug.WriteLine($"[REGISTER] {DateTime.UtcNow:O} | Tool: {name}");
        await Task.CompletedTask;
    }
    
    private async Task LogToolUnregistered(string name)
    {
        System.Diagnostics.Debug.WriteLine($"[UNREGISTER] {DateTime.UtcNow:O} | Tool: {name}");
        await Task.CompletedTask;
    }
    
    private async Task LogSkillPreEnable(string skillName)
    {
        System.Diagnostics.Debug.WriteLine($"[SKILL-ENABLE] {DateTime.UtcNow:O} | Skill: {skillName} | Starting...");
        await Task.CompletedTask;
    }
    
    private async Task LogSkillPostEnable(string skillName, int toolCount)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[SKILL-ENABLED] {DateTime.UtcNow:O} | Skill: {skillName} | Tools: {toolCount}");
        await Task.CompletedTask;
    }
    
    private async Task LogSkillError(string skillName, string error)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[SKILL-ERROR] {DateTime.UtcNow:O} | Skill: {skillName} | Error: {error}");
        await Task.CompletedTask;
    }
    
    private async Task LogSkillDisabled(string skillName)
    {
        System.Diagnostics.Debug.WriteLine($"[SKILL-DISABLED] {DateTime.UtcNow:O} | Skill: {skillName}");
        await Task.CompletedTask;
    }
}

// Usage
var toolRegistry = new ToolRegistry();
var logger = new ToolEventLogger(toolRegistry);
// Now all events are logged automatically
```

## Integration Points

### In ToolRegistry
```csharp
public class ToolRegistry
{
    public ToolEventHookRegistry HookRegistry { get; } = new();
    public ToolMetricsCollector MetricsCollector { get; } = new();
    
    public async Task RegisterAsync(ITool tool)
    {
        // Register the tool
        // Invoke OnToolRegistered hook
    }
    
    public async Task UnregisterAsync(string name)
    {
        // Unregister the tool
        // Invoke OnToolUnregistered hook
    }
}
```

### In BaseTool
```csharp
public abstract class BaseTool : ITool
{
    public virtual string? ToolVersion => null;
    
    public async Task<ToolResult> ExecuteAsync(Dictionary<string, object?> arguments)
    {
        var executionId = Guid.NewGuid().ToString();
        var timer = Stopwatch.StartNew();
        
        // Tool execution with metadata
        result.ToolCallId = executionId;
        result.ExecutionTimeMs = timer.ElapsedMilliseconds;
        result.ToolVersion = ToolVersion;
        
        return result;
    }
}
```

### In SkillRegistry
```csharp
public class SkillRegistry
{
    public ToolEventHookRegistry HookRegistry { get; } = new();
    
    public async Task EnableSkillAsync(string name)
    {
        await HookRegistry.InvokeSkillPreEnableAsync(name);
        // ... enable logic ...
        await HookRegistry.InvokeSkillPostEnableAsync(name, tools.Count);
    }
}
```

## Best Practices

1. **Hook Safety**: Hooks swallow exceptions to prevent breaking tool execution
2. **Non-Blocking**: Keep hook implementations fast and async-friendly
3. **Correlation**: Use `executionId` to correlate pre/post/error events
4. **Metrics**: Use `ToolMetricsCollector` for built-in performance tracking
5. **Versioning**: Tools should implement `ToolVersion` property for tracking
6. **Dependency Management**: Always use `EnableSkillAsync` which handles dependencies

## Migration Guide

### From Previous Version

Old way:
```csharp
var skill = skillRegistry.Get("git");
await skill.InitializeAsync();
skillRegistry.EnableSkillAsync("git");
```

New way with dependency resolution:
```csharp
// Just call this - dependencies handled automatically
await skillRegistry.EnableSkillAsync("git");
```

## OpenClaw Alignment

✅ **Aligned with OpenClaw:**
- Event-driven architecture for extensibility
- Callback system for result announcement
- Correlation tracking via execution IDs
- Metrics collection for observability
- Dependency resolution for capability management
- Hook registry for lifecycle customization

