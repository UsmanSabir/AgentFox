# Channel Message Gateway Integration Guide

## Overview

The `ChannelMessageGateway` provides a centralized way to route channel messages (from WhatsApp, Telegram, Discord, etc.) through the OpenClaw-inspired command lane system. This ensures proper async handling, rate limiting, and integrated execution flow.

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│ Multiple Channels (WhatsApp, Telegram, Discord, etc.)   │
└──────────────────────┬──────────────────────────────────┘
                       │ OnMessageReceived
                       ↓
            ┌──────────────────────────┐
            │   ChannelManager         │
            │                          │
            │  - Manages channels      │
            │  - Routes messages       │
            └──────────┬───────────────┘
                       │
                       ↓ (via gateway)
        ┌──────────────────────────────────────┐
        │  ChannelMessageGateway (NEW)         │
        │                                       │
        │  - Wraps ChannelMessage as Command  │
        │  - Manages concurrency limits        │
        │  - Tracks message processing         │
        │  - Handles responses & errors        │
        └──────────┬──────────────────┬────────┘
                   │                  │
         (enqueue)  │                  │ (on complete)
                   ↓                  │
        ┌────────────────────────┐   │
        │  CommandQueue          │   │
        │  (Multi-lane FIFO)     │   │
        └──────────┬─────────────┘   │
                   │                  │
     (Main/Tool/   ↓                  │
      Subagent     │                  │
      lanes)       ↓                  │
        ┌────────────────────────┐   │
        │  CommandProcessor      │   │
        └──────────┬─────────────┘   │
                   │                  │
                   ↓                  │
        ┌────────────────────────┐   │
        │  Agent Execution       │   │
        └──────────┬─────────────┘   │
                   │                  │
                   └──────────────────┘
                        (response)
```

## Integration Steps

### 1. Setup the Gateway

```csharp
// Create dependencies
var toolRegistry = CreateToolRegistry();
var agentRuntime = new DefaultAgentRuntime(toolRegistry);
var commandQueue = new CommandQueue();

// Create the gateway
var gateway = new ChannelMessageGateway(
    commandQueue: commandQueue,
    agentRuntime: agentRuntime,
    logger: logger,
    defaultLane: CommandLane.Main,
    defaultTimeoutSeconds: 300,
    maxConcurrentProcessing: 10
);

// Create agent and channel manager
var agent = new AgentBuilder(toolRegistry)
    .WithName("AgentFox")
    .Build();

var channelManager = new ChannelManager(agent, logger);
channelManager.SetGateway(gateway);  // ← Activate gateway mode
```

### 2. Add Channels

```csharp
// Add WhatsApp channel
var whatsappChannel = new WhatsAppChannel(
    phoneNumberId: "123456789",
    accessToken: "your-token",
    businessAccountId: "your-account"
);
channelManager.AddChannel(whatsappChannel);

// Add Discord channel
var discordChannel = new DiscordChannel(
    botToken: "your-bot-token",
    guildId: 123456789,
    channelId: 987654321
);
channelManager.AddChannel(discordChannel);

// Connect all channels
await channelManager.ConnectAllAsync();
```

### 3. Setup Command Processor

```csharp
var processor = new CommandProcessor(commandQueue, logger);

// Register handler for Main lane (for channel messages)
processor.RegisterLaneHandler(CommandLane.Main, async (command, ct) =>
{
    if (command is ChannelCommand channelCmd)
    {
        try
        {
            // Execute agent with channel message
            var result = await agentRuntime.ExecuteAsync(
                new Agent { Config = new AgentConfig { Name = channelCmd.AgentId } },
                channelCmd.ChannelMessage.Content
            );
            
            // Mark as complete and send response back to channel
            if (channelCmd.OriginatingChannel != null)
            {
                await gateway.CompleteChannelMessageAsync(
                    channelCmd.RunId,
                    result.Output,
                    channelCmd.OriginatingChannel
                );
            }
        }
        catch (Exception ex)
        {
            if (channelCmd.OriginatingChannel != null)
            {
                await gateway.FailChannelMessageAsync(
                    channelCmd.RunId,
                    ex.Message,
                    channelCmd.OriginatingChannel
                );
            }
        }
    }
});

// Start the processor
processor.Start();
```

### 4. Monitor Statistics

```csharp
// Get gateway statistics anytime
var stats = gateway.GetStatistics();
Console.WriteLine($"Total messages: {stats.TotalMessagesReceived}");
Console.WriteLine($"Processed: {stats.TotalMessagesProcessed}");
Console.WriteLine($"Failed: {stats.TotalMessagesFailed}");
Console.WriteLine($"Avg time: {stats.AverageProcessingTimeMilliseconds}ms");
Console.WriteLine($"Per channel: {string.Join(", ", stats.MessagesPerChannel)}");
```

## Key Features

### 1. Command Lane Integration
- Channel messages flow through the configured command lane (default: Main)
- Respects lane priorities and processing policies
- Enables controlled concurrent processing

### 2. Concurrency Control
- Configurable `maxConcurrentProcessing` limit (default: 10)
- Prevents resource exhaustion
- Gracefully rejects messages when at limit

### 3. Timeout Handling
- Configurable per-message timeout (default: 300s)
- Automatic timeout management
- Graceful error response to user

### 4. Error Handling
- Comprehensive exception handling
- Error responses sent back to channel
- Full error logging for debugging

### 5. Observability
- Per-channel message statistics
- Processing time tracking (last 1000 messages)
- Processing state tracking (Pending, Processing, Completed, Failed, TimedOut)
- Uptime and performance metrics

## Message Flow Example

```csharp
// User sends message on Discord
// Discord bot receives: "What is the weather?"

// 1. OnMessageReceived event triggered
channel.RaiseMessageReceived(new ChannelMessage 
{
    Id = "msg_123",
    ChannelId = "discord_...",
    Content = "What is the weather?",
    SenderId = "user_456",
    SenderName = "Alice"
});

// 2. ChannelManager.HandleMessage() calls gateway
var task = await gateway.ProcessChannelMessageAsync(
    channelMessage,
    channel,
    agentId: "AgentFox"
);
// Returns: ChannelMessageTask with State = Processing

// 3. Gateway creates ChannelCommand and enqueues
var command = ChannelCommand.CreateFromChannelMessage(...);
commandQueue.Enqueue(command);  // → Main lane

// 4. CommandProcessor picks up, executes handler
// Handler runs agent, gets response: "The weather is sunny, 72°F"

// 5. Gateway marks complete and sends response
await gateway.CompleteChannelMessageAsync(
    commandRunId,
    "The weather is sunny, 72°F",
    channel
);

// 6. Response sent back to Discord
// User sees: "The weather is sunny, 72°F"
```

## Backward Compatibility

If you don't set a gateway on ChannelManager, it automatically falls back to legacy direct execution mode:

```csharp
var channelManager = new ChannelManager(agent);
// Gateway not set - runs in legacy mode

var result = await agent.ExecuteAsync(message.Content);
await channel.SendMessageAsync(result.Output);
```

## Advanced Configuration

### Different Lanes for Different Channels

```csharp
// High-priority channel with Subagent lane for complex tasks
await gateway.ProcessChannelMessageAsync(
    message,
    vipChannel,
    agentId,
    overrideLane: CommandLane.Subagent,  // Use subagent lane
    overrideTimeoutSeconds: 600  // 10 minute timeout
);

// Regular channel with Main lane
await gateway.ProcessChannelMessageAsync(
    message,
    regularChannel,
    agentId
    // Defaults to Main lane, 300s timeout
);
```

### Custom Timeout Per Message

```csharp
// Quick response expected
await gateway.ProcessChannelMessageAsync(
    simpleQuery,
    channel,
    agentId,
    overrideTimeoutSeconds: 30
);

// Long-running task
await gateway.ProcessChannelMessageAsync(
    complexTask,
    channel,
    agentId,
    overrideTimeoutSeconds: 900  // 15 minutes
);
```

## Thread Safety

All components are designed to be thread-safe:
- `ConcurrentDictionary` for tracking tasks
- `ConcurrentQueue` for channel message storage
- Thread-safe statistics tracking with `Interlocked` operations
- Lock-protected processing times list

## Performance Considerations

1. **Processing Time Tracking**: Last 1000 messages are retained for averaging
2. **Concurrency Limit**: Default 10 concurrent processing to prevent overload
3. **Command Lane Prioritization**: Main lane > Subagent > Tool > Background
4. **Async All The Way**: Non-blocking I/O for channels

## Troubleshooting

### Messages are stuck in "Processing" state
- Check CommandProcessor is running: `processor.Start()`
- Verify handler is registered: `processor.RegisterLaneHandler(...)`
- Check timeout settings if taking longer

### Concurrency limit errors
- Increase `maxConcurrentProcessing` parameter
- Check if agent execution is hanging
- Review command handler performance

### Messages not reaching channels
- Verify channel is connected: `await channelManager.ConnectAllAsync()`
- Check channel's `SendMessageAsync` implementation
- Review error logs for exceptions
