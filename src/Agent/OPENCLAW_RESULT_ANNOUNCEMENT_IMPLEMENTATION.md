# OpenClaw-Inspired Sub-Agent Result Announcement - Implementation Guide

## Overview

The implementation adds a **bidirectional, event-driven result announcement system** to the sub-agent lane architecture, enabling results to flow back to the requesting channel automatically.

## What Was Implemented

### 1. **Extended SubAgentTask** (`Agents/SubAgentTask.cs`)
Added channel context and correlation tracking:
```csharp
public string? OriginatingChannelId { get; set; }      // Channel that spawned this task
public Channel? OriginatingChannel { get; set; }       // Channel reference for announcing results
public string? OriginatingMessageId { get; set; }      // Message ID for correlation
public string CorrelationId { get; set; }              // Unique trace ID
public Dictionary<string, object> SourceMetadata { get; set; }  // User/channel info
```

### 2. **ResultAnnouncementCommand** (`Agents/ResultAnnouncementCommand.cs`)
New command type that flows through the command lanes:
```csharp
public SubAgentCompletionResult? Result { get; set; }   // The result to announce
public Channel? RequesterChannel { get; set; }          // Where to send it
public string CorrelationId { get; set; }               // Link to request
public string? FormattingTemplate { get; set; }         // How to format the message
```

### 3. **Callback System** (`Agents/SubAgentManager.cs`)
Event-driven callbacks that trigger when sub-agents complete:
```csharp
public delegate Task<ResultAnnouncementCommand?> SubAgentResultCallback(
    SubAgentTask task,
    SubAgentCompletionResult result);

public void RegisterResultCallback(SubAgentResultCallback callback)
{
    OnSubAgentFinalized += callback;
}
```

### 4. **Result Handler** (`Agents/SubAgentLaneSystemIntegration.cs`)
Handler that processes ResultAnnouncementCommand and sends to channels:
```csharp
// Automatically routes results back to originating channels
// Formats messages based on result status (Completed, Failed, TimedOut, etc.)
// Handles errors gracefully
```

---

## How It Works

### **Bidirectional Flow**

```
Request Flow:
  Channel Message 
    → ChannelCommand (Main Lane)
    → SubAgentManager.SpawnSubAgentAsync()
    → SubAgentTask (preserves channel context)
    → Enqueued to Subagent Lane

Result Flow:
  SubAgent completes
    → SubAgentManager.OnSubAgentCompleted()
    → Invoke registered callbacks
    → GenerateResultAnnouncementCommand() returns announcement
    → Enqueued to Main Lane
    → ResultHandler processes it
    → Sends result back to originating Channel
```

### **Correlation Flow**

```
Original Request
  ↓ (MessageId: msg_123, ChannelId: whatsapp_1)
  ↓ stored in: SubAgentTask
  ↓
SubAgent Execution
  ↓ CorrelationId: "abc-123-def" links them
  ↓
Result → SubAgentTask has both MessageId and ChannelId
  ↓
ResultAnnouncementCommand created with CorrelationId
  ↓
Sent back to original Channel
```

---

## Integration Example

### **Step 1: Setup the System with Result Callbacks**

```csharp
// Initialize the sub-agent lane system
var config = new SubAgentConfiguration
{
    MaxSpawnDepth = 3,
    MaxConcurrentSubAgents = 10,
    DefaultRunTimeoutSeconds = 300
};

var system = new SubAgentLaneSystemIntegration(agentRuntime, config, logger);
system.Initialize();

// Get access to the SubAgentManager from system
var manager = system.GetSubAgentManager(); // You may need to expose this
```

### **Step 2: Register a Result Callback for Channel Announcements**

```csharp
// Register callback that creates result announcements for channels
manager.RegisterResultCallback(async (task, result) =>
{
    // Only create announcement if this sub-agent was spawned from a channel
    if (task.OriginatingChannel != null && !string.IsNullOrEmpty(task.OriginatingMessageId))
    {
        // Create announcement command to send result back to channel
        var announcement = ResultAnnouncementCommand.CreateChannelAnnouncement(
            result: result,
            channel: task.OriginatingChannel,
            originatingMessageId: task.OriginatingMessageId,
            correlationId: task.CorrelationId,
            channelId: task.OriginatingChannelId ?? string.Empty,
            formattingTemplate: null  // Uses defaults based on status
        );
        
        logger?.LogInformation(
            "Created result announcement (Correlation: {CorrelationId}, Channel: {ChannelId})",
            task.CorrelationId,
            task.OriginatingChannelId);
        
        return announcement;
    }
    
    // Not from a channel, don't announce
    return null;
});
```

### **Step 3: Spawn Sub-Agent from Channel with Context**

```csharp
// When processing a channel message that spawns a sub-agent:
async Task HandleChannelMessageSpawningSubAgent(
    ChannelMessage message,
    Channel channel,
    string taskDescription)
{
    // Spawn sub-agent with channel context preserved
    var spawnResult = await manager.SpawnSubAgentAsync(
        parentSessionKey: $"channel:{channel.ChannelId}:{message.Id}",
        parentAgentId: "main-agent",
        taskMessage: taskDescription,
        parentSpawnDepth: 0
    );
    
    if (spawnResult.Success && spawnResult.Task != null)
    {
        var task = spawnResult.Task;
        
        // **KEY STEP**: Store channel context in the sub-agent task
        task.OriginatingChannelId = channel.ChannelId;
        task.OriginatingChannel = channel;
        task.OriginatingMessageId = message.Id;
        
        // Store user/request metadata
        task.SourceMetadata = new Dictionary<string, object>
        {
            ["channel_type"] = channel.GetType().Name,
            ["user_id"] = message.SenderId,
            ["user_name"] = message.SenderName,
            ["original_message"] = message.Content,
            ["request_timestamp"] = DateTime.UtcNow
        };
        
        logger?.LogInformation(
            "Sub-agent spawned with channel context (Correlation: {CorrelationId})",
            task.CorrelationId);
        
        // Optionally wait for completion
        try
        {
            var completionResult = await task.Completion.Task
                .ConfigureAwait(false);
                
            logger?.LogInformation(
                "Sub-agent completed with status: {Status}",
                completionResult.Status);
        }
        catch (OperationCanceledException)
        {
            logger?.LogWarning("Sub-agent was cancelled");
        }
    }
}
```

### **Step 4: The Result Flows Back Automatically**

```
Timeline:
├─ T0: Channel message received
├─ T1: Sub-agent spawned with channel context preserved
├─ T2: Sub-agent executes (may take seconds/minutes)
├─ T3: Sub-agent completes with result
├─ T4: Callback invoked → ResultAnnouncementCommand created
├─ T5: Announcement enqueued to Main Lane
├─ T6: ResultHandler picks up announcement
├─ T7: Result formatted and sent back to original channel
└─ T8: User receives result notification
```

---

## Formatting Result Announcements

### **Default Templates (by Status)**

```csharp
Completed   → "✅ Sub-task completed successfully:\n{output}"
Failed      → "❌ Sub-task failed:\n{error}"
TimedOut    → "⏱️ Sub-task timed out after {duration} seconds"
Cancelled   → "⚠️ Sub-task was cancelled"
```

### **Custom Formatting**

```csharp
var announcement = new ResultAnnouncementCommand
{
    Result = completionResult,
    RequesterChannel = channel,
    FormattingTemplate = """
        📊 Report Generated
        Status: {status}
        Time Taken: {duration}s
        Result: {output}
        """
};

_commandQueue.Enqueue(announcement);
```

### **Template Placeholders**

| Placeholder | Description |
|------------|-------------|
| `{status}` | SubAgentState (Completed, Failed, etc.) |
| `{output}` | The result output text |
| `{error}` | Error message (if failed) |
| `{duration}` | Execution duration in seconds |
| `{timestamp}` | Completion timestamp (ISO format) |

---

## Advanced Usage

### **1. Custom Result Callbacks**

```csharp
// Register multiple callbacks for different scenarios
manager.RegisterResultCallback(async (task, result) =>
{
    // Log to external system
    await LogToAnalyticsAsync(task.CorrelationId, result);
    
    // Could return null if not creating announcement
    return null;
});

manager.RegisterResultCallback(async (task, result) =>
{
    // Send notification to admin if failed
    if (result.Status == SubAgentState.Failed)
    {
        var alert = new AlertCommand
        {
            Severity = "high",
            Message = $"Sub-agent {task.SessionKey} failed: {result.Error}"
        };
        return alert;  // Different command type
    }
    
    return null;
});
```

### **2. Conditional Announcements**

```csharp
manager.RegisterResultCallback(async (task, result) =>
{
    // Only announce if from WhatsApp channel
    if (task.SourceMetadata.TryGetValue("channel_type", out var channelType) &&
        channelType?.ToString() == "WhatsAppChannel")
    {
        return ResultAnnouncementCommand.CreateChannelAnnouncement(
            result, 
            task.OriginatingChannel,
            task.OriginatingMessageId,
            task.CorrelationId,
            task.OriginatingChannelId
        );
    }
    
    // For other channels, store result locally instead
    return ResultAnnouncementCommand.CreateLocalAnnouncement(
        result,
        task.CorrelationId,
        task.SessionKey
    );
});
```

### **3. Result Aggregation**

```csharp
// Keep track of results for monitoring
private ConcurrentDictionary<string, SubAgentCompletionResult> _resultCache = new();

manager.RegisterResultCallback(async (task, result) =>
{
    // Store in cache
    _resultCache[task.CorrelationId] = result;
    
    // Create announcement
    if (task.OriginatingChannel != null)
    {
        return ResultAnnouncementCommand.CreateChannelAnnouncement(
            result,
            task.OriginatingChannel,
            task.OriginatingMessageId,
            task.CorrelationId,
            task.OriginatingChannelId
        );
    }
    
    return null;
});

// Later, you can query results by correlation ID
public SubAgentCompletionResult? GetResult(string correlationId)
{
    _resultCache.TryGetValue(correlationId, out var result);
    return result;
}
```

---

## Key Architectural Benefits

### **1. Separation of Concerns**
- SubAgentTask: Execution context
- SubAgentManager: Lifecycle management
- ResultAnnouncementCommand: Result routing
- Callbacks: Custom announcement logic

### **2. Extensibility**
- Multiple callbacks can be registered
- Each callback can decide whether to announce/store/ignore
- Different command types can be returned

### **3. Consistency with OpenClaw**
- Commands flow through lanes (requests and results)
- Events trigger callbacks for side effects
- Context propagates with tasks

### **4. Composability**
- Results flow like commands through the same system
- Same CommandProcessor, same lanes, same reliability
- No special result handling code needed

### **5. Observability**
- Correlation IDs link requests to responses
- Source metadata enriches audit trails
- All steps logged for debugging

---

## Error Handling

### **Channel Send Failures**

```csharp
// The handler gracefully handles channel send errors
private async Task HandleResultAnnouncementAsync(
    ResultAnnouncementCommand announcementCmd,
    CancellationToken ct)
{
    try
    {
        // Send to channel
        if (announcementCmd.RequesterChannel != null)
        {
            try
            {
                await announcementCmd.RequesterChannel.SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to announce to channel");
                // Result is already in completion result, just logging failure to send
            }
        }
    }
    catch (Exception ex)
    {
        _logger?.LogError(ex, "Error handling result announcement");
    }
}
```

### **Callback Failures**

```csharp
// Errors in callbacks don't crash the system
private async Task InvokeResultCallbacksAsync(SubAgentTask task, SubAgentCompletionResult result)
{
    foreach (var del in invocationList)
    {
        try
        {
            var announcementCmd = await callback.Invoke(task, result);
            if (announcementCmd != null)
            {
                _commandQueue.Enqueue(announcementCmd);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Error in result callback: {ex.Message}");
            // Continue with next callback
        }
    }
}
```

---

## Testing the Implementation

```csharp
// Test: Sub-agent result flows back to channel
[Fact]
public async Task SubAgentResultAnnouncement_ChannelReceivesResult()
{
    // Setup
    var channelMessages = new List<string>();
    var mockChannel = new MockChannel();
    mockChannel.OnSendMessage += msg => channelMessages.Add(msg);
    
    var manager = new SubAgentManager(commandQueue, agentRuntime);
    
    // Register callback
    manager.RegisterResultCallback(async (task, result) =>
    {
        if (task.OriginatingChannel != null)
        {
            return ResultAnnouncementCommand.CreateChannelAnnouncement(
                result, task.OriginatingChannel, task.OriginatingMessageId,
                task.CorrelationId, task.OriginatingChannelId);
        }
        return null;
    });
    
    // Spawn sub-agent with channel context
    var spawnResult = await manager.SpawnSubAgentAsync(
        "session:main", "agent-1", "Do something");
    
    var task = spawnResult.Task;
    task.OriginatingChannel = mockChannel;
    task.OriginatingMessageId = "msg_123";
    task.OriginatingChannelId = "channel_1";
    
    // Complete sub-agent
    var result = SubAgentCompletionResult.Success("Task completed!");
    manager.OnSubAgentCompleted(task.RunId, result);
    
    // Wait for announcement to be processed
    await Task.Delay(100);
    
    // Verify
    Assert.Single(channelMessages);
    Assert.Contains("Task completed!", channelMessages[0]);
}
```

---

## Summary

The OpenClaw-inspired implementation provides:

✅ **Bidirectional result flow** - Results return through the same system as requests
✅ **Channel context preservation** - Originating channel remains trackable
✅ **Automatic announcements** - Results flow back without manual intervention
✅ **Correlation tracking** - Full request → response traceability
✅ **Extensible callbacks** - Custom announcement logic via registered handlers
✅ **Graceful error handling** - Failures don't crash the system
✅ **Composable architecture** - Results flow like commands through lanes

This creates a complete, production-ready sub-agent result announcement system that aligns with OpenClaw's event-driven architecture.
