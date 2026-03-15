# Sub-Agent Lane Execution System for AgentFox

A production-ready, robust implementation of a sub-agent lane execution system inspired by OpenClaw, enabling concurrent background agent execution without blocking the main application.

## Overview

The Sub-Agent Lane Execution System is a sophisticated command-queuing and execution framework that:

- ✅ **Prevents Main Thread Blocking**: Sub-agents execute in dedicated lanes
- ✅ **Enforces Resource Limits**: Configurable depth, concurrency, and per-parent limits
- ✅ **Handles Timeouts Gracefully**: Automatic timeout detection and cancellation
- ✅ **Thread-Safe**: Uses `ConcurrentQueue` and `ConcurrentDictionary` throughout
- ✅ **Scalable**: Independent lane processing with no bottlenecks
- ✅ **Observable**: Comprehensive statistics and monitoring
- ✅ **Testable**: Dependency injection friendly with mockable interfaces

## Quick Start

### Basic Usage

```csharp
using AgentFox.Agents;
using Microsoft.Extensions.Logging;

// 1. Create configuration (or use defaults)
var config = new SubAgentConfiguration
{
    MaxSpawnDepth = 3,
    MaxConcurrentSubAgents = 10
};

// 2. Initialize the system
var system = new SubAgentLaneSystemIntegration(agentRuntime, config, logger);
system.Initialize();

// 3. Spawn a sub-agent
var result = await system.SpawnSubAgentAsync(
    parentSessionKey: "session:main-agent",
    parentAgentId: "main-agent-1",
    taskMessage: "Analyze dataset and generate insights");

if (result.Success)
{
    var task = result.Task;
    var completion = await task.Completion.Task;
    Console.WriteLine($"Sub-agent output: {completion.Output}");
}

// 4. Shutdown gracefully
await system.ShutdownAsync();
```

## Core Components

### 1. CommandLane (Enum)
Segregates commands into different execution tracks:
- **Main**: Primary agent execution
- **Subagent**: Spawned sub-agents (concurrent)
- **Tool**: Long-running tool calls
- **Background**: Non-urgent operations

### 2. CommandQueue (Interface + Implementation)
Lane-aware FIFO queue with:
- `ConcurrentQueue<T>` for thread-safety
- Per-lane queuing and dequeuing
- Priority-based lane selection
- Statistics tracking

### 3. CommandProcessor
Continuously processes queued commands:
- Configurable batch sizes
- Lane-specific handlers
- Graceful shutdown with cancellation tokens
- Performance metrics

### 4. SubAgentManager
Orchestrates sub-agent lifecycle:
- Spawns sub-agents with unique session keys
- Applies policy constraints (depth, concurrency, children)
- Tracks state transitions
- Handles timeouts and cleanup

### 5. SubAgentConfiguration
Policy enforcement configuration:
- `MaxSpawnDepth`: Prevent infinite recursion (default: 3)
- `MaxConcurrentSubAgents`: Limit concurrent load (default: 10)
- `MaxChildrenPerAgent`: Per-parent limit (default: 5)
- `DefaultRunTimeoutSeconds`: Execution timeout (default: 300)
- Model and thinking level defaults

## Architecture

```
┌─────────────────────────────────────────┐
│      Main Application / Main Agent       │
└───────────────────┬─────────────────────┘
                    │
        ┌───────────▼───────────┐
        │  SubAgentManager      │
        │  - Policy checks      │
        │  - Lifecycle events   │
        └───────────┬───────────┘
                    │
        ┌───────────▼───────────┐
        │   CommandQueue        │
        │  Main | Subagent      │
        │  Tool | Background    │
        └───────────┬───────────┘
                    │
        ┌───────────▼───────────┐
        │ CommandProcessor      │
        │ - Dequeue & Execute   │
        │ - Lane Handlers       │
        │ - Metrics             │
        └───────────┬───────────┘
                    │
    ┌───────────────┼────────────────┐
    │               │                │
    ▼               ▼                ▼
Main Handler  Subagent Handler   Tool Handler
    │               │                │
    ▼               ▼                ▼
Execute Agent  Execute Subagent   Execute Tool
```

## Policy Enforcement

### Spawn Depth Check
Prevents infinite recursion:
```
MaxSpawnDepth = 3
Depth 0: Main agent ✓
Depth 1: First-level sub-agent ✓
Depth 2: Second-level sub-agent ✓
Depth 3: Third-level sub-agent ✓
Depth 4: Spawn attempt → REJECTED ✗
```

### Concurrency Check
Prevents resource exhaustion:
```
MaxConcurrentSubAgents = 10
Active count < 10 → ALLOWED ✓
Active count ≥ 10 → REJECTED ✗
```

### Children Limit Check
Prevents individual agents from spawning too much:
```
MaxChildrenPerAgent = 5
Agent's children < 5 → ALLOWED ✓
Agent's children ≥ 5 → REJECTED ✗
```

## Lifecycle Events

### Sub-Agent States

```
Pending    - Queued for execution
Running    - Currently executing
Completed  - Finished successfully
Failed     - Execution failed
TimedOut   - Exceeded timeout
Cancelled  - Cancelled by request
```

### State Transitions

```
Pending ──┬──→ Running ──┬──→ Completed ✓
          │              │
          │              ├──→ Failed ✗
          │              │
          │              ├──→ TimedOut ✗
          │              │
          │              └──→ Cancelled ✗
          │
          └──→ Cancelled (direct) ✗
```

### Lifecycle Callbacks

The system notifies you of state changes:

```csharp
// Sub-agent starts
manager.OnSubAgentStarted(runId);

// Sub-agent completes
var result = SubAgentCompletionResult.Success(output, agentResult);
manager.OnSubAgentCompleted(runId, result);

// Access completion
var completion = await subAgentTask.Completion.Task;
```

## Configuration Best Practices

```csharp
var config = new SubAgentConfiguration
{
    // Prevent runaway recursion - OpenClaw typically uses 3
    MaxSpawnDepth = 3,
    
    // Limit concurrent load based on system resources
    // 4-core system: 8-16
    // 8-core system: 16-32
    MaxConcurrentSubAgents = 10,
    
    // Per-parent limit prevents single agent from spawning too many
    MaxChildrenPerAgent = 5,
    
    // Timeout based on expected task duration
    // Quick tasks: 60-120s
    // Complex analysis: 300-600s
    // Long-running: 600-1800s
    DefaultRunTimeoutSeconds = 300,
    
    // Use appropriate model for workload
    DefaultModel = "gpt-4",
    DefaultThinkingLevel = "high",
    
    // Auto-cleanup reduces memory leaks
    AutoCleanupCompleted = true,
    CleanupDelayMilliseconds = 5000,
    
    // Enable for debugging
    EnableVerboseLogging = false
};
```

## Advanced Usage

### Direct Manager Usage

For fine-grained control:

```csharp
var queue = new CommandQueue();
var manager = new SubAgentManager(queue, agentRuntime, config, logger);

// Spawn sub-agent
var result = await manager.SpawnSubAgentAsync(
    "session:main",
    "agent-1",
    "Task description",
    parentSpawnDepth: 0);

// Monitor
var stats = manager.GetStatistics();
Console.WriteLine($"Active: {stats.RunningSubAgents}");
Console.WriteLine($"Failed: {stats.FailedSubAgents}");

// Cleanup
await manager.ForceCleanupAllAsync();
manager.Dispose();
```

### Custom Command Handlers

Register handlers for specific lanes:

```csharp
commandProcessor.RegisterLaneHandler(CommandLane.Subagent, 
    async (command, ct) =>
    {
        // Custom execution logic
        await ExecuteSubAgentAsync(command, ct);
    });
```

### Monitoring and Statistics

```csharp
// Command processor stats
var procStats = commandProcessor.GetStatistics();
Console.WriteLine($"Processed: {procStats.TotalProcessed}");
Console.WriteLine($"Failed: {procStats.TotalFailed}");
Console.WriteLine($"Queued: {procStats.QueuedCommands}");

// Sub-agent manager stats
var mgmtStats = subAgentManager.GetStatistics();
Console.WriteLine($"Running: {mgmtStats.RunningSubAgents}");
Console.WriteLine($"Pending: {mgmtStats.PendingSubAgents}");
Console.WriteLine($"Completed: {mgmtStats.CompletedSubAgents}");
Console.WriteLine($"Failed: {mgmtStats.FailedSubAgents}");
Console.WriteLine($"Timed Out: {mgmtStats.TimedOutSubAgents}");
```

## Examples

Nine comprehensive examples are provided in `SubAgentLaneSystemExamples.cs`:

1. **Basic Setup** - Default configuration example
2. **Multiple Sub-Agents** - Spawning several sub-agents concurrently
3. **Policy Enforcement** - Demonstrating policy rejections
4. **Monitoring** - Tracking sub-agent states and statistics
5. **Custom Queue** - Direct queue manipulation and priority handling
6. **Timeout Handling** - Observing timeout behavior
7. **Direct Manager** - Using SubAgentManager directly
8. **Configuration** - Validation and best practices
9. **Priority Ordering** - Lane-based command priority
10. **Interactive** - Interactive CLI demo

Run examples:

```csharp
// Example 1: Basic setup
await SubAgentLaneSystemExamples.Example1_BasicSetup(agentRuntime, logger);

// Example 2: Multiple sub-agents
await SubAgentLaneSystemExamples.Example2_MultipleSubAgents(agentRuntime, logger);

// Interactive demo
await SubAgentLaneSystemExamples.SubAgentLaneSystemInteractiveExample
    .RunInteractiveExampleAsync(agentRuntime, logger);
```

## Integration Points

### With IAgentRuntime

The system integrates seamlessly with AgentFox's runtime:

```csharp
// Run main agent
var result = await agentRuntime.ExecuteAsync(agent, task);

// Spawn sub-agent
var subAgent = agentRuntime.SpawnSubAgent(parentAgent, config);

// Access tools
var toolRegistry = agentRuntime.ToolRegistry;
```

### With Logging

Full Microsoft.Extensions.Logging support:

```csharp
ILogger logger = logFactory.CreateLogger<SubAgentManager>();

var manager = new SubAgentManager(queue, runtime, config, logger);

// All operations logged at appropriate levels:
// Info: Spawn requests, completion
// Warning: Policy failures, timeouts
// Error: Execution exceptions
// Debug: Detailed state changes
```

## Testing

### Unit Testing

Mock the interfaces:

```csharp
var mockQueue = new Mock<ICommandQueue>();
var mockRuntime = new Mock<IAgentRuntime>();

var manager = new SubAgentManager(
    mockQueue.Object,
    mockRuntime.Object,
    config);

// Test policy enforcement, etc.
```

### Integration Testing

Use real implementations with test agents:

```csharp
var queue = new CommandQueue();
var manager = new SubAgentManager(queue, testRuntime, config);

var result = await manager.SpawnSubAgentAsync(...);
Assert.True(result.Success);
```

## Performance Characteristics

### Thread Safety
- Lock-free with concurrent collections
- No blocking operations in hot paths
- Suitable for high-throughput scenarios

### Scalability
- Independent lanes prevent bottlenecks
- Configurable concurrency limits
- Efficient cleanup and resource management

### Monitoring
- O(1) statistics retrieval
- Non-intrusive observability
- Minimal logging overhead

## Comparison with OpenClaw

| Feature | OpenClaw | AgentFox |
|---------|----------|---------|
| Lane System | ✓ | ✓ |
| Depth Limiting | ✓ | ✓ |
| Concurrency Limits | ✓ | ✓ |
| Session Keys | ✓ | ✓ |
| Timeout Handling | ✓ | ✓ |
| Language | Python | C# |
| Type Safety | Partial | Full |
| Async/Await | asyncio | Task-based |
| Thread-Safe | GIL-based | Concurrent Collections |

## Files Reference

### Core Architecture
- `CommandLane.cs` - Lane definitions
- `ICommand.cs` - Command interface
- `AgentCommand.cs` - Command implementation
- `SubAgentTask.cs` - Task lifecycle model
- `SubAgentConfiguration.cs` - Configuration with validation
- `CommandQueue.cs` - Multi-lane queue
- `CommandProcessor.cs` - Command processing engine

### Integration
- `SubAgentManager.cs` - Main orchestrator
- `SubAgentLaneSystemIntegration.cs` - Integration facade

### Examples & Documentation
- `SubAgentLaneSystemExamples.cs` - 9+ code examples
- `SUBAGENT_LANE_SYSTEM_DESIGN.md` - Architecture guide
- `README.md` - This file

## Troubleshooting

### Sub-agents not spawning
- Check policy constraints: `MaxSpawnDepth`, `MaxConcurrentSubAgents`
- Verify configuration validation passes
- Check logs for policy rejection reasons

### Commands not being processed
- Verify `CommandProcessor.Start()` was called
- Check lane handlers are registered
- Look for exceptions in handler logs

### Memory leaks
- Ensure `AutoCleanupCompleted = true` in config
- Call `Dispose()` on manager when done
- Call `ShutdownAsync()` on system before exit

### Timeouts occurring
- Increase `DefaultRunTimeoutSeconds` if tasks are slow
- Verify execution environment has sufficient resources
- Check system logs for performance issues

## Future Enhancements

- Persistence layer for sub-agent state
- Metrics export (Prometheus, Application Insights)
- Circuit breaker for failing sub-agents
- Automatic retry policies
- Load balancing across multiple processors
- Sub-agent result caching

## License

Same as AgentFox project

## Contributing

See AgentFox contributing guidelines

## Support

For issues, questions, or improvements:
1. Check the examples in `SubAgentLaneSystemExamples.cs`
2. Review `SUBAGENT_LANE_SYSTEM_DESIGN.md` for architecture details
3. Examine source code documentation
4. Check AgentFox issue tracker
