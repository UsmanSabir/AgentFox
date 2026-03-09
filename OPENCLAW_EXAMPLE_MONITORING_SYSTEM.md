# OpenClaw Event Hooks - Practical Example

This example demonstrates a complete, production-ready implementation using all the new event hooks, metrics, and OpenClaw-aligned features.

## Complete Example: Intelligent Tool Monitoring System

```csharp
using AgentFox.Tools;
using AgentFox.Skills;
using System.Diagnostics;

/// <summary>
/// Production-ready tool monitoring system using OpenClaw event hooks
/// </summary>
public class IntelligentToolMonitor
{
    private readonly ToolRegistry _toolRegistry;
    private readonly SkillRegistry _skillRegistry;
    private readonly ToolMetricsCollector _metrics;
    private readonly List<ToolExecutionEvent> _executionLog = new();
    
    public IntelligentToolMonitor(ToolRegistry toolRegistry, SkillRegistry skillRegistry)
    {
        _toolRegistry = toolRegistry;
        _skillRegistry = skillRegistry;
        _metrics = toolRegistry.MetricsCollector;
        
        SetupEventHooks();
    }
    
    private void SetupEventHooks()
    {
        var hooks = _toolRegistry.HookRegistry;
        
        // Log all tool executions
        hooks.OnToolPreExecute += LogToolPreExecute;
        hooks.OnToolPostExecute += LogToolPostExecute;
        hooks.OnToolError += LogToolError;
        
        // Track tool registration
        hooks.OnToolRegistered += LogToolRegistered;
        hooks.OnToolUnregistered += LogToolUnregistered;
        
        // Track skill lifecycle
        hooks.OnSkillPreEnable += LogSkillPreEnable;
        hooks.OnSkillPostEnable += LogSkillPostEnable;
        hooks.OnSkillError += LogSkillError;
        hooks.OnSkillDisabled += LogSkillDisabled;
    }
    
    // Event handlers
    private async Task LogToolPreExecute(string toolName, Dictionary<string, object?> arguments, string executionId)
    {
        var evt = new ToolExecutionEvent
        {
            ExecutionId = executionId,
            ToolName = toolName,
            EventType = "pre_execute",
            Timestamp = DateTime.UtcNow,
            ArgumentCount = arguments.Count
        };
        _executionLog.Add(evt);
        
        await Task.CompletedTask;
    }
    
    private async Task LogToolPostExecute(string toolName, ToolResult result, long executionTimeMs, string executionId)
    {
        var evt = new ToolExecutionEvent
        {
            ExecutionId = executionId,
            ToolName = toolName,
            EventType = result.Success ? "post_execute_success" : "post_execute_fail",
            Timestamp = DateTime.UtcNow,
            ExecutionTime = executionTimeMs,
            OutputLength = result.Output.Length,
            HasError = !result.Success
        };
        _executionLog.Add(evt);
        
        await Task.CompletedTask;
    }
    
    private async Task LogToolError(string toolName, string error, long executionTimeMs, string executionId)
    {
        var evt = new ToolExecutionEvent
        {
            ExecutionId = executionId,
            ToolName = toolName,
            EventType = "error",
            Timestamp = DateTime.UtcNow,
            ExecutionTime = executionTimeMs,
            ErrorMessage = error,
            HasError = true
        };
        _executionLog.Add(evt);
        
        await Task.CompletedTask;
    }
    
    private async Task LogToolRegistered(string name, string description)
    {
        System.Diagnostics.Debug.WriteLine($"[TOOL] Registered: {name}");
        await Task.CompletedTask;
    }
    
    private async Task LogToolUnregistered(string name)
    {
        System.Diagnostics.Debug.WriteLine($"[TOOL] Unregistered: {name}");
        await Task.CompletedTask;
    }
    
    private async Task LogSkillPreEnable(string skillName)
    {
        System.Diagnostics.Debug.WriteLine($"[SKILL] Enabling: {skillName}");
        await Task.CompletedTask;
    }
    
    private async Task LogSkillPostEnable(string skillName, int toolCount)
    {
        System.Diagnostics.Debug.WriteLine($"[SKILL] Enabled: {skillName} with {toolCount} tools");
        await Task.CompletedTask;
    }
    
    private async Task LogSkillError(string skillName, string error)
    {
        System.Diagnostics.Debug.WriteLine($"[SKILL] Error enabling {skillName}: {error}");
        await Task.CompletedTask;
    }
    
    private async Task LogSkillDisabled(string skillName)
    {
        System.Diagnostics.Debug.WriteLine($"[SKILL] Disabled: {skillName}");
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// Generate a comprehensive monitoring report
    /// </summary>
    public void GenerateReport()
    {
        Console.WriteLine("\n╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║           INTELLIGENT TOOL MONITORING REPORT                 ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");
        
        // Section 1: Overall Metrics
        var stats = _metrics.GetSummaryStatistics();
        int totalTools = stats.TotalTools;
        int totalExecutions = stats.TotalExecutions;
        double avgSuccessRate = stats.AverageSuccessRate;
        
        Console.WriteLine("📊 OVERALL METRICS");
        Console.WriteLine("─────────────────────────────────────────────────────────────");
        Console.WriteLine($"  Total Tools:            {totalTools}");
        Console.WriteLine($"  Total Executions:       {totalExecutions}");
        Console.WriteLine($"  Average Success Rate:   {avgSuccessRate:F1}%\n");
        
        // Section 2: Top Performers
        Console.WriteLine("⭐ TOP PERFORMERS (Most Used Tools)");
        Console.WriteLine("─────────────────────────────────────────────────────────────");
        var topTools = _metrics.GetMetricsByUsage().Take(5);
        foreach (var metric in topTools)
        {
            Console.WriteLine($"  {metric.ToolName}:");
            Console.WriteLine($"    Calls:          {metric.ExecutionCount}");
            Console.WriteLine($"    Success Rate:   {metric.SuccessRate:F1}%");
            Console.WriteLine($"    Avg Time:       {metric.AverageExecutionTimeMs:F0}ms");
            Console.WriteLine($"    Min/Max Time:   {metric.MinExecutionTimeMs}ms / {metric.MaxExecutionTimeMs}ms");
        }
        Console.WriteLine();
        
        // Section 3: Underperformers
        var unreliable = _metrics.GetMetricsByFailureRate().Where(m => m.SuccessRate < 95).ToList();
        if (unreliable.Count > 0)
        {
            Console.WriteLine("⚠️  UNRELIABLE TOOLS (Success Rate < 95%)");
            Console.WriteLine("─────────────────────────────────────────────────────────────");
            foreach (var metric in unreliable.Take(5))
            {
                Console.WriteLine($"  {metric.ToolName}:");
                Console.WriteLine($"    Success Rate:   {metric.SuccessRate:F1}%");
                Console.WriteLine($"    Failures:       {metric.FailureCount}/{metric.ExecutionCount}");
            }
            Console.WriteLine();
        }
        
        // Section 4: Slow Tools
        Console.WriteLine("🐢 PERFORMANCE BOTTLENECKS (Slowest Tools)");
        Console.WriteLine("─────────────────────────────────────────────────────────────");
        var slowest = _metrics.GetMetricsBySlowest().Take(5);
        foreach (var metric in slowest)
        {
            Console.WriteLine($"  {metric.ToolName}:");
            Console.WriteLine($"    Average Time:   {metric.AverageExecutionTimeMs:F0}ms");
            Console.WriteLine($"    Calls:          {metric.ExecutionCount}");
        }
        Console.WriteLine();
        
        // Section 5: Enabled Skills
        var enabledSkills = _skillRegistry.GetEnabledSkills();
        Console.WriteLine("🎯 ACTIVE SKILLS");
        Console.WriteLine("─────────────────────────────────────────────────────────────");
        foreach (var skill in enabledSkills)
        {
            Console.WriteLine($"  • {skill.Name}");
            if (skill.Dependencies.Count > 0)
            {
                Console.WriteLine($"    Dependencies: {string.Join(", ", skill.Dependencies)}");
            }
        }
        Console.WriteLine();
        
        // Section 6: Recent Events
        Console.WriteLine("📝 RECENT EXECUTION EVENTS (Last 10)");
        Console.WriteLine("─────────────────────────────────────────────────────────────");
        var recentEvents = _executionLog.OrderByDescending(e => e.Timestamp).Take(10);
        foreach (var evt in recentEvents)
        {
            var status = evt.HasError ? "❌" : "✓";
            Console.WriteLine($"  {status} [{evt.Timestamp:HH:mm:ss.fff}] {evt.ToolName}");
            Console.WriteLine($"     Event: {evt.EventType}");
            if (evt.ExecutionTime.HasValue)
            {
                Console.WriteLine($"     Time: {evt.ExecutionTime}ms");
            }
            if (!string.IsNullOrEmpty(evt.ErrorMessage))
            {
                Console.WriteLine($"     Error: {evt.ErrorMessage}");
            }
        }
        Console.WriteLine();
        
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");
    }
    
    /// <summary>
    /// Get health status of the tool ecosystem
    /// </summary>
    public HealthStatus GetHealthStatus()
    {
        var stats = _metrics.GetSummaryStatistics();
        double avgSuccessRate = stats.AverageSuccessRate;
        
        var unreliableCount = _metrics.GetMetricsByFailureRate()
            .Count(m => m.SuccessRate < 90);
        
        string status = avgSuccessRate >= 95 ? "healthy" :
                       avgSuccessRate >= 80 ? "degraded" :
                       "critical";
        
        return new HealthStatus
        {
            Status = status,
            AverageSuccessRate = avgSuccessRate,
            TotalTools = stats.TotalTools,
            UnreliableToolCount = unreliableCount,
            TotalExecutions = stats.TotalExecutions,
            LastCheckedAt = DateTime.UtcNow
        };
    }
}

/// <summary>
/// Tool execution event for logging
/// </summary>
public class ToolExecutionEvent
{
    public string ExecutionId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public long? ExecutionTime { get; set; }
    public int ArgumentCount { get; set; }
    public int OutputLength { get; set; }
    public bool HasError { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Health status of the tool ecosystem
/// </summary>
public class HealthStatus
{
    public string Status { get; set; } = string.Empty;
    public double AverageSuccessRate { get; set; }
    public int TotalTools { get; set; }
    public int UnreliableToolCount { get; set; }
    public int TotalExecutions { get; set; }
    public DateTime LastCheckedAt { get; set; }
}
```

## Usage in Agent Runtime

```csharp
// Initialize the system
var toolRegistry = new ToolRegistry();
var skillRegistry = new SkillRegistry(toolRegistry);

// Set up intelligent monitoring
var monitor = new IntelligentToolMonitor(toolRegistry, skillRegistry);

// Enable skills with dependency resolution
await skillRegistry.EnableSkillAsync("git");
await skillRegistry.EnableSkillAsync("docker");

// Run your agent tasks...
// Tools will be automatically monitored

// Generate monitoring report
monitor.GenerateReport();

// Check health status
var health = monitor.GetHealthStatus();
if (health.Status == "critical")
{
    Console.WriteLine($"⚠️  WARNING: {health.UnreliableToolCount} tools are unreliable!");
    // Take corrective action
}
```

## Expected Output

```
╔══════════════════════════════════════════════════════════════╗
║           INTELLIGENT TOOL MONITORING REPORT                 ║
╚══════════════════════════════════════════════════════════════╝

📊 OVERALL METRICS
─────────────────────────────────────────────────────────────
  Total Tools:            12
  Total Executions:       234
  Average Success Rate:   96.3%

⭐ TOP PERFORMERS (Most Used Tools)
─────────────────────────────────────────────────────────────
  shell:
    Calls:          45
    Success Rate:   98.5%
    Avg Time:       123ms
    Min/Max Time:   15ms / 2341ms
  
  read_file:
    Calls:          38
    Success Rate:   100.0%
    Avg Time:       45ms
    Min/Max Time:   12ms / 234ms

🐢 PERFORMANCE BOTTLENECKS (Slowest Tools)
─────────────────────────────────────────────────────────────
  git_push:
    Average Time:   3234ms
    Calls:          8

📝 RECENT EXECUTION EVENTS (Last 10)
─────────────────────────────────────────────────────────────
  ✓ [14:32:01.234] shell
     Event: post_execute_success
     Time: 45ms
  ✓ [14:31:59.876] read_file
     Event: post_execute_success
     Time: 12ms

╚══════════════════════════════════════════════════════════════╝
```

## Key Benefits

1. **Real-Time Observability**: See what tools are running and how long they take
2. **Performance Insights**: Identify bottlenecks and optimize critical paths
3. **Reliability Tracking**: Monitor tool success rates and identify flaky tools
4. **Audit Trail**: Complete event log of all tool and skill lifecycle events
5. **Health Monitoring**: Automated health status checks
6. **Debugging**: Execute IDs for correlating related events across the system

## OpenClaw Integration Points

✅ **Event-Driven**: Hooks for every lifecycle event  
✅ **Metrics**: Built-in performance tracking  
✅ **Correlation**: Execution IDs linking related events  
✅ **Dependency Management**: Automatic skill dependency resolution  
✅ **Observability**: Complete visibility into tool ecosystem  
