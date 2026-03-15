# Sub-Agent Lane Execution System Design

## Overview

This document describes the architecture and implementation of a robust, scalable sub-agent lane execution system for AgentFox, inspired by OpenClaw's approach to managing concurrent background agents without blocking the main application.

## Core Concepts

### Command Lanes
Commands are segregated into different execution lanes to allow independent processing policies:

- **Main Lane**: Primary agent execution, single task focus
- **Subagent Lane**: Dedicated lane for spawned sub-agents, allows concurrent execution
- **Tool Lane**: Long-running tool calls, doesn't block agents
- **Background Lane**: Non-urgent operations (logging, cleanup, etc.)

This segregation allows the main agent to remain responsive while sub-agents execute in parallel.

### Key Components

#### 1. **CommandLane (enum)**
Defines the different execution lanes for command segregation.

#### 2. **ICommand & AgentCommand**
- `ICommand`: Interface for enqueueable commands
- `AgentCommand`: Concrete implementation with message, model, thinking level, and timeout

#### 3. **SubAgentTask**
Encapsulates the execution context of a sub-agent including:
- Session key and run ID
- Current state (Pending, Running, Completed, Failed, TimedOut, Cancelled)
- Execution metadata and lifecycle events
- Cancellation token for graceful shutdown
- Task completion source for awaiting results

#### 4. **CommandQueue**
Lane-aware FIFO queue using `ConcurrentQueue<T>` for thread-safe operations:
- Segregates commands by lane
- Supports priority ordering
- Non-blocking enqueue/dequeue operations

#### 5. **CommandProcessor**
Continuously dequeues and processes commands:
- Registers lane-specific handlers
- Supports configurable batch processing
- Provides statistics and monitoring
- Handles graceful shutdown with cancellation tokens

#### 6. **SubAgentConfiguration**
Policy configuration for sub-agent behavior:
- `MaxSpawnDepth`: Prevents infinite recursion (default: 3)
- `MaxConcurrentSubAgents`: Resource limit (default: 10)
- `MaxChildrenPerAgent`: Per-parent limit (default: 5)
- `DefaultRunTimeoutSeconds`: Execution timeout (default: 300s)
- Model and thinking level defaults

#### 7. **SubAgentManager**
Orchestrates sub-agent lifecycles with policy enforcement:
- Spawns sub-agents with session key generation
- Applies policy checks (depth, concurrency, children limits)
- Tracks active sub-agents with lifecycle events
- Implements timeout handling
- Manages resource cleanup

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                    Main Application                              │
└─────────────────────────────────────────────────────────────────┘
                              │
                    ┌─────────▼────────────┐
                    │ SubAgentManager      │
                    │ (Policy Enforcement) │
                    └─────────┬────────────┘
                              │
                    ┌─────────▼────────────┐
                    │   CommandQueue       │
                    │  (Multi-lane FIFO)   │
                    └─────────┬────────────┘
                              │
        ┌─────────────────────┼─────────────────────┐
        │                     │                     │
   ┌────▼────┐         ┌──────▼──────┐        ┌────▼────┐
   │Main Lane │         │Subagent Lane│        │Tool Lane│
   └────┬────┘         └──────┬──────┘        └────┬────┘
        │                     │                    │
   ┌────▼──────────────────┐  │  ┌────────────────┼──────┐
   │ CommandProcessor      │  │  │                │      │
   │ (Dequeue & Execute)   │  │  │         ┌──────▼──┐   │
   └────┬──────────────────┘  │  │         │Handler  │   │
        │                     │  │         └─────────┘   │
   ┌────▼──────────────────┐  │  │   ┌────────────────┐  │
   │ Handler Delegates     │◄─┘  └──►│ Task Executors │  │
   │ (per lane)            │         └────────────────┘  │
   └──────────────────────┘                              │
                                            ┌────────────▼──┐
                                            │ Background    │
                                            │ Operations    │
                                            └───────────────┘
```

## Usage Flow

### 1. Initialize the System

```csharp
var config = new SubAgentConfiguration
{
    MaxSpawnDepth = 3,
    MaxConcurrentSubAgents = 10,
    MaxChildrenPerAgent = 5,
    DefaultRunTimeoutSeconds = 300
};

var system = new SubAgentLaneSystemIntegration(agentRuntime, config, logger);
system.Initialize();
```

### 2. Execute Main Agent Command

```csharp
var result = await system.ExecuteMainAgentCommandAsync(
    "session:main-agent",
    "agent-1",
    "Solve a complex problem");
```

### 3. Spawn Sub-Agent

```csharp
var spawnResult = await system.SpawnSubAgentAsync(
    "session:main-agent",
    "agent-1",
    "Analyze data and generate report",
    model: "gpt-4",
    thinkingLevel: "high");

if (spawnResult.Success)
{
    // Sub-agent spawned and queued for execution
    var task = spawnResult.Task;
    
    // Wait for completion
    var completion = await task.Completion.Task;
    Console.WriteLine($"Result: {completion.Output}");
}
```

### 4. Monitor and Shutdown

```csharp
// Get statistics
system.PrintStatistics();

// Graceful shutdown
await system.ShutdownAsync();
```

## Policy Enforcement

The system enforces policies to prevent resource exhaustion and prevent infinite recursion:

### Spawn Depth Check
```
Policy: MaxSpawnDepth = 3
Current Depth ≥ MaxSpawnDepth → REJECT

Example:
- Depth 0: Main agent ✓
- Depth 1: First-level sub-agent ✓
- Depth 2: Second-level sub-agent ✓
- Depth 3: Third-level sub-agent, attempt depth 4 ✗ REJECTED
```

### Concurrency Check
```
Policy: MaxConcurrentSubAgents = 10
Active Count ≥ MaxConcurrentSubAgents → REJECT

Prevents resource exhaustion and maintains system stability.
```

### Children Limit Check
```
Policy: MaxChildrenPerAgent = 5
Agent's Children Count ≥ MaxChildrenPerAgent → REJECT

Prevents individual agents from spawning too many children.
```

## Lifecycle Events

### Sub-Agent States

```
Pending → Running → Completed ✓
                 → Failed ✗
                 → TimedOut ✗
                 → Cancelled ✗
```

### Lifecycle Notifications

1. **OnSubAgentStarted**: Called when execution begins
2. **OnSubAgentCompleted**: Called when execution finishes (any state)
3. **Automatic Cleanup**: Optional delayed cleanup of completed tasks

## Error Handling

### Policy Violations
When spawn policies are violated:
- Request is rejected immediately
- No resource allocation occurs
- Clear error message provided

### Timeout Handling
When timeout occurs:
- Sub-agent execution is cancelled
- Cancellation token is signaled
- Completion result marked as `SubAgentState.TimedOut`
- Resources are cleaned up

### Exception Handling
- All exceptions are caught and logged
- Completion result marked as `SubAgentState.Failed`
- Error details included in completion result

## Performance Characteristics

### Thread Safety
- `ConcurrentQueue<T>` for command queueing
- `ConcurrentDictionary<T>` for sub-agent tracking
- Non-blocking operations throughout

### Scalability
- Independent lanes prevent bottlenecks
- Concurrent processing with configurable limits
- Efficient cleanup and resource management

### Monitoring
- Per-lane command count tracking
- Sub-agent state statistics
- Processor uptime and throughput metrics
- Automatic timeout detection

## Configuration Best Practices

```csharp
var config = new SubAgentConfiguration
{
    // Prevent runaway recursion
    MaxSpawnDepth = 3,
    
    // Limit concurrent load based on system resources
    // Example: For 4-core system, use 8-16
    MaxConcurrentSubAgents = 10,
    
    // Reasonable per-agent limit
    MaxChildrenPerAgent = 5,
    
    // Timeout based on expected task durations
    // Longer for complex analysis, shorter for quick tasks
    DefaultRunTimeoutSeconds = 300,
    
    // Use powerful model for sub-agents if budget allows
    DefaultModel = "gpt-4",
    DefaultThinkingLevel = "high",
    
    // Auto-cleanup reduces memory leaks
    AutoCleanupCompleted = true,
    CleanupDelayMilliseconds = 5000
};
```

## Integration with Agent Runtime

The system integrates with `IAgentRuntime` to:
1. **Spawn sub-agents**: `agentRuntime.SpawnSubAgent(parent, config)`
2. **Execute agents**: `agentRuntime.ExecuteAsync(agent, task)`
3. **Access tool registry**: `agentRuntime.ToolRegistry`

## Comparison with OpenClaw

### Similarities
- Command queueing system with priority lanes
- Sub-agent spawning with depth limiting
- Concurrency and resource constraints
- Graceful timeout and cancellation handling
- Session-based tracking

### C# Specific Enhancements
- Strong typing with interfaces and generics
- Dependency injection friendly design
- Async/await patterns throughout
- Thread-safe collections
- Comprehensive logging integration

## Advanced Topics

### Custom Lane Handlers
Register custom handlers for each lane:

```csharp
commandProcessor.RegisterLaneHandler(CommandLane.Custom, async (command, ct) =>
{
    // Custom execution logic
    await ExecuteCustomLogicAsync(command, ct);
});
```

### Metrics and Observability
Access comprehensive statistics:

```csharp
var stats = subAgentManager.GetStatistics();
Console.WriteLine($"Active: {stats.RunningSubAgents}");
Console.WriteLine($"Failed: {stats.FailedSubAgents}");
Console.WriteLine($"Timed Out: {stats.TimedOutSubAgents}");
```

### Graceful Shutdown
System ensures all sub-agents are properly cleaned up:

```csharp
await system.ShutdownAsync();
// Cancels in-flight operations
// Cleans up resources
// Waits for graceful termination
```

## Testing Considerations

1. **Unit Testing**: Mock `ICommandQueue` and `IAgentRuntime`
2. **Integration Testing**: Use in-memory implementations
3. **Load Testing**: Verify concurrency limits work correctly
4. **Timeout Testing**: Verify timeout behavior with delayed handlers

## References

- OpenClaw Project: Sub-agent spawning and lane management patterns
- Microsoft.Extensions.Logging: Logging infrastructure
- System.Threading: Concurrency primitives
