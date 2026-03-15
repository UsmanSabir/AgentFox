# Sub-Agent Result Announcement Mechanism - Review

## Current State Analysis

### How Sub-Agents Currently Announce Results

#### 1. **Result Flow**
```
Sub-Agent ExecuteAsync() 
    → AgentResult { Success, Output, Error, Duration }
    ↓
SubAgentLaneSystemIntegration handler 
    → wraps in SubAgentCompletionResult
    ↓
SubAgentManager.OnSubAgentCompleted(runId, result)
    → task.Completion.SetResult(result)
    → Decrement child count, schedule cleanup
    ↓
SubAgentTask.Completion.Task 
    → Parent awaits this (if spawning directly)
```

#### 2. **Current Issue: Missing Channel Context Routing**

When a channel message spawns a sub-agent, the result flow **breaks**:

```
ChannelMessage 
    → ChannelCommand in Main Lane
    → Channel handler calls gateway
    → Gateway may spawn SubAgent
    ↓
    result ends up in: SubAgentTask.Completion
    ↓
    ❌ NO mechanism to route back to originating CHANNEL
```

**Critical Gap**: Sub-agent completion results are trapped in `SubAgentTask.Completion.SetResult()` with no way to announce them back to the requesting channel.

#### 3. **Current Architecture (as implemented)**

**Session Keys track the hierarchy:**
- Main agent: `"agent:main-agent-id"`
- Sub-agent: `"agent:main-agent-id:subagent:guid"`

**Commands are enqueued:**
- AgentCommand with SessionKey, RunId, Lane, Message
- Handlers execute and notify completion

**Results stored in:**
- `SubAgentTask.Completion` (TaskCompletionSource)
- Parent agent can await this **only if it holds the reference**

**Missing piece:**
- No callback/messaging system to announce results
- No channel reference stored with sub-agent
- No event system to notify listeners

---

## OpenClaw-Inspired Mechanisms (Intent)

### What OpenClaw Does for Sub-Agent Results

OpenClaw (based on system documentation mentions) provides:

1. **Result Channels/Lanes for Announcing Back**
   - Results don't just end at TaskCompletionSource
   - Results are "announced" to listeners/subscribers
   - Similar to how messages flow THROUGH lanes

2. **Callback/Hook System**
   - Sub-agent completion triggers callbacks
   - Callbacks can re-enqueue announcement messages back to requester
   - Creates a circular message flow

3. **Context Propagation**
   - Original channel/requester context travels with sub-agent
   - Result handler knows where to send the answer
   - Metadata ties originator to completion

---

## Issues Identified

### **Issue #1: No Channel Awareness in Sub-Agents**
- SubAgentTask has no reference to originating channel
- When channel spawns sub-agent, that channel reference is lost
- Result arrives but nowhere to send it

```csharp
// Current SubAgentTask:
public class SubAgentTask
{
    public string SessionKey { get; set; }           // e.g., "agent:parent:subagent:guid"
    public string ParentAgentId { get; set; }        // Parent agent ID
    // ❌ NO: public Channel? OriginatingChannel { get; set; }
    // ❌ NO: public string? RequesterChannelId { get; set; }
}
```

### **Issue #2: No Result Callback System**
- OnSubAgentCompleted only updates TaskCompletionSource
- No way to register "announce result back" callbacks
- Result event is not published to listeners

```csharp
// Current SubAgentManager.OnSubAgentCompleted:
_subAgentManager.OnSubAgentCompleted(runId, result)
{
    task.Completion.SetResult(result);  // ← Only this
    // ❌ NO: RaiseResultAnnouncedEvent()?
    // ❌ NO: InvokeResultCallbacks()?
    // ❌ NO: EnqueueResultAnnouncement()?
}
```

### **Issue #3: Unidirectional Result Flow**
- Results don't route back through lanes like commands do
- They're trapped in TaskCompletionSource
- No way to stage results for transmission back

```
Commands: Agent → Lane Queue → Handler → Execution → Result
Results:  Agent → ??? 
          (Should be: Result → Lane/Channel → Requester)
```

### **Issue #4: Missing Metadata/Correlation**
- Sub-agent doesn't track "who asked for this"
- No correlation ID linking request → sub-agent → response
- Makes it impossible to route result to correct listener

---

## OpenClaw-Inspired Solution Architecture

### **1. Extended SubAgentTask (Add Channel Context)**

```csharp
public class SubAgentTask
{
    public string SessionKey { get; set; }
    public string RunId { get; set; }
    public string ParentAgentId { get; set; }
    
    // ✅ NEW: Store originating context
    public string? OriginatingChannelId { get; set; }
    public Channel? OriginatingChannel { get; set; }
    public string? OriginatingMessageId { get; set; }
    
    // ✅ NEW: Correlation tracking
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
    
    // ✅ NEW: Metadata about the request source
    public Dictionary<string, object> SourceMetadata { get; set; } = new();
    
    // Existing...
    public SubAgentState State { get; set; }
    public TaskCompletionSource<SubAgentCompletionResult> Completion { get; set; }
}
```

### **2. Create Result Announcement Command**

```csharp
/// <summary>
/// Command to announce a sub-agent result back to the requesting channel/agent
/// Inspired by OpenClaw's result routing mechanism
/// </summary>
public class ResultAnnouncementCommand : ICommand
{
    public string RunId { get; set; } = Guid.NewGuid().ToString();
    public string SessionKey { get; set; }  // "channel:channelId:messageId"
    public CommandLane Lane { get; set; } = CommandLane.Main;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int Priority { get; set; } = 0;
    
    /// <summary>
    /// The sub-agent result to announce
    /// </summary>
    public SubAgentCompletionResult Result { get; set; }
    
    /// <summary>
    /// The channel to announce back to
    /// </summary>
    public Channel? RequesterChannel { get; set; }
    
    /// <summary>
    /// Correlation ID linking back to original request
    /// </summary>
    public string CorrelationId { get; set; }
    
    /// <summary>
    /// Format for the announcement
    /// </summary>
    public string? FormattingTemplate { get; set; }
    
    public Dictionary<string, string> Metadata { get; set; } = new();
}
```

### **3. Result Announcement Handler (New Lane Handler)**

```csharp
_commandProcessor.RegisterLaneHandler(CommandLane.Main, async (command, ct) =>
{
    if (command is ResultAnnouncementCommand announcementCmd)
    {
        _logger?.LogInformation(
            "Announcing sub-agent result (Correlation: {CorrelationId})",
            announcementCmd.CorrelationId);
        
        // Format the result message
        var message = FormatResultAnnouncement(
            announcementCmd.Result,
            announcementCmd.FormattingTemplate);
        
        // Send back to originating channel
        if (announcementCmd.RequesterChannel != null)
        {
            try
            {
                await announcementCmd.RequesterChannel.SendMessageAsync(message);
                _logger?.LogInformation("Result announced to channel");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to announce result to channel");
            }
        }
    }
});
```

### **4. Enhanced SubAgentManager (Add Result Routing)**

```csharp
/// <summary>
/// Register a callback for when sub-agent completes
/// Callback receives completion and can enqueue result announcement
/// </summary>
public delegate Task<ResultAnnouncementCommand?> SubAgentResultCallback(
    SubAgentTask task,
    SubAgentCompletionResult result);

public class SubAgentManager
{
    private event SubAgentResultCallback? OnSubAgentFinalized;
    
    public void RegisterResultCallback(SubAgentResultCallback callback)
    {
        OnSubAgentFinalized += callback;
    }
    
    public async void OnSubAgentCompleted(string runId, SubAgentCompletionResult result)
    {
        if (_activeSubAgents.TryGetValue(runId, out var task))
        {
            task.State = result.Status;
            task.CompletedAt = DateTime.UtcNow;
            task.Completion.SetResult(result);
            
            _logger?.LogInformation($"Sub-agent completed: {runId}, Status={result.Status}");
            
            // ✅ NEW: Invoke result callbacks
            if (OnSubAgentFinalized != null)
            {
                try
                {
                    var announcementCmd = await OnSubAgentFinalized.Invoke(task, result);
                    
                    // ✅ NEW: Enqueue result announcement if callback provides it
                    if (announcementCmd != null)
                    {
                        _commandQueue.Enqueue(announcementCmd);
                        _logger?.LogInformation(
                            "Result announcement queued (Correlation: {CorrelationId})",
                            announcementCmd.CorrelationId);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Error in result callback: {ex.Message}");
                }
            }
            
            // Decrement child count, cleanup, etc...
            _childCountPerAgent.AddOrUpdate(task.ParentAgentId, 0, (_, count) => count - 1);
        }
    }
}
```

### **5. Integration Example (Spawn Sub-Agent from Channel)**

```csharp
// When a channel message triggers sub-agent spawn:
var subAgentTask = new SubAgentTask
{
    SessionKey = subAgentSessionKey,
    RunId = command.RunId,
    ParentAgentId = parentAgentId,
    TaskPayload = taskMessage,
    
    // ✅ NEW: Store channel context
    OriginatingChannelId = channelMessage.ChannelId,
    OriginatingChannel = originatingChannel,
    OriginatingMessageId = channelMessage.Id,
    CorrelationId = channelCommand.RunId,
    
    SourceMetadata = new Dictionary<string, object>
    {
        ["channel_type"] = originatingChannel.ChannelType,
        ["user_id"] = channelMessage.SenderId,
        ["user_name"] = channelMessage.SenderName
    }
};

// Register callback to announce result back to channel
_subAgentManager.RegisterResultCallback(async (task, result) =>
{
    // Check if this came from a channel
    if (task.OriginatingChannel != null)
    {
        // Create announcement command
        return new ResultAnnouncementCommand
        {
            SessionKey = $"channel:{task.OriginatingChannelId}:{task.OriginatingMessageId}",
            Result = result,
            RequesterChannel = task.OriginatingChannel,
            CorrelationId = task.CorrelationId,
            FormattingTemplate = "Your sub-task completed:\\n{output}",
            Metadata = new Dictionary<string, string>
            {
                ["status"] = result.Status.ToString(),
                ["duration"] = result.Duration?.TotalSeconds.ToString() ?? "unknown"
            }
        };
    }
    
    return null;  // No channel to announce to
});
```

---

## Comparison: Current vs. OpenClaw-Inspired

| Aspect | Current | OpenClaw-Inspired |
|--------|---------|-------------------|
| **Result Storage** | TaskCompletionSource only | + Result Announcement Command |
| **Channel Awareness** | None | Session context + Correlation ID |
| **Result Routing** | Parent must poll/await | Enqueued back through lanes |
| **Callback System** | None | Event-based result callbacks |
| **Channel Notification** | Not implemented | Automatic via ResultAnnouncementCommand |
| **Metadata Tracking** | Basic | Rich correlation + source metadata |
| **Bidirectional Flow** | One-way execute → TaskCompletionSource | Bidirectional: request → execute → announce |
| **Composability** | Parent needs result reference | Results flow like commands through system |

---

## Benefits of OpenClaw-Inspired Approach

1. **Symmetric Flow**: Results flow back through the same system as commands
2. **Channel Context Preservation**: Originating channel remains trackable
3. **Decoupling**: Result handlers don't need parent reference to announce
4. **Scalability**: Multiple listeners can be registered for results
5. **Auditability**: Correlation IDs track full request → response cycle
6. **Extensibility**: Formatters can customize result announcements
7. **Reliability**: Results don't get "lost" in TaskCompletionSource limbo

---

## Implementation Recommendation

### **Phase 1: Add Channel Context (Low Risk)**
Add optional fields to SubAgentTask:
- `OriginatingChannelId`
- `OriginatingChannel`
- `OriginatingMessageId`
- `CorrelationId`

### **Phase 2: Create ResultAnnouncementCommand**
New command type to queue results for transmission.

### **Phase 3: Implement Callback System**
Add event registration for sub-agent completion.

### **Phase 4: Add Result Handler**
Register handler in SubAgentLaneSystemIntegration to process ResultAnnouncementCommand.

### **Phase 5: Integrate with ChannelSpawning**
When channel spawns sub-agent, automatically register callback and preserve channel context.

---

## Current Code Locations

- **SubAgentTask**: `Agents/SubAgentTask.cs:1-150`
- **SubAgentManager.OnSubAgentCompleted**: `Agents/SubAgentManager.cs:170-200`
- **Handler Registration**: `Agents/SubAgentLaneSystemIntegration.cs:230-270`
- **Channel Context** (missing piece): Should be in SubAgentTask or command

---

## Summary

The current implementation successfully handles sub-agent **execution** and **completion tracking** but lacks the **announcement and routing** mechanism to send results back to the originating channel. 

The OpenClaw-inspired approach would add a bidirectional result flow where:
1. Channels spawn sub-agents with preserved context
2. Sub-agent completion triggers callbacks
3. Callbacks enqueue ResultAnnouncementCommand
4. Results flow back through lanes to originating channel
5. Full correlation between request and response

This creates a symmetric, composable system that aligns with the existing command-lane architecture.
