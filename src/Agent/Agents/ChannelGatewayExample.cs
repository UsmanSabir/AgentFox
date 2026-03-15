using AgentFox.Agents;
using AgentFox.Channels;
using AgentFox.Models;
using AgentFox.Tools;
using Microsoft.Extensions.Logging;

namespace AgentFox;

/// <summary>
/// Example: Using the ChannelMessageGateway for multi-channel message processing
/// Demonstrates how to set up and integrate the gateway with channels and command lanes
/// Inspired by OpenClaw's robust multi-channel architecture
/// </summary>
public class ChannelGatewayExample
{
    public static async Task RunExample()
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║      Channel Message Gateway Integration Example            ║");
        Console.WriteLine("║    Routing multi-channel messages through command lanes     ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        
        // Setup logging
        var loggerFactory = new LoggerFactory();
        var logger = loggerFactory.CreateLogger<ChannelGatewayExample>();
        
        // 1. Create core infrastructure
        logger.LogInformation("🔧 Setting up infrastructure...");
        var toolRegistry = CreateToolRegistry();
        var agentRuntime = new DefaultAgentRuntime(toolRegistry, logger: logger);
        
        // 2. Create command queue (multi-lane)
        var commandQueue = new CommandQueue();
        logger.LogInformation("✓ CommandQueue created with 4 lanes");
        
        // 3. Create the gateway for routing channel messages
        var gateway = new ChannelMessageGateway(
            commandQueue: commandQueue,
            agentRuntime: agentRuntime,
            logger: logger,
            defaultLane: CommandLane.Main,
            defaultTimeoutSeconds: 300,
            maxConcurrentProcessing: 10
        );
        logger.LogInformation("✓ ChannelMessageGateway created");
        
        // 4. Create agent
        var agent = new AgentBuilder(toolRegistry)
            .WithName("AgentFox")
            .WithSystemPrompt("You are AgentFox, a helpful AI assistant with access to various tools.")
            .WithHybridMemory(100, "memory.json")
            .Build();
        logger.LogInformation("✓ Agent created");
        
        // 5. Create channel manager with gateway
        var channelManager = new ChannelManager(agent, logger);
        channelManager.SetGateway(gateway);
        logger.LogInformation("✓ ChannelManager configured with gateway");
        
        // 6. Add multiple channels
        logger.LogInformation("📡 Adding channels...");
        
        var discordChannel = new DiscordChannel(
            botToken: "discord_token_123",
            guildId: 123456789,
            channelId: 987654321
        );
        channelManager.AddChannel(discordChannel);
        logger.LogInformation("✓ Discord channel added");
        
        var telegramChannel = new TelegramChannel(
            botToken: "telegram_token_456",
            chatId: 555555555
        );
        channelManager.AddChannel(telegramChannel);
        logger.LogInformation("✓ Telegram channel added");
        
        // 7. Connect channels
        await channelManager.ConnectAllAsync();
        logger.LogInformation("✓ All channels connected");
        
        // 8. Create and start command processor
        var processor = new CommandProcessor(commandQueue, logger, processingDelayMilliseconds: 10);
        
        // Register handler for Main lane (processes channel messages)
        processor.RegisterLaneHandler(CommandLane.Main, async (command, ct) =>
        {
            if (command is ChannelCommand channelCmd)
            {
                logger.LogInformation("▶️  Processing channel command: {RunId}", command.RunId);
                
                try
                {
                    // Simulate agent execution delay
                    await Task.Delay(100, ct);
                    
                    // In real implementation, would call agent.ExecuteAsync()
                    var response = $"Response to: {channelCmd.ChannelMessage.Content}";
                    
                    // Mark complete and send response back to channel
                    if (channelCmd.OriginatingChannel != null)
                    {
                        await gateway.CompleteChannelMessageAsync(
                            command.RunId,
                            response,
                            channelCmd.OriginatingChannel
                        );
                        logger.LogInformation("✅ Channel message completed");
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation("⚠️  Command cancelled");
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "❌ Error processing channel command");
                    
                    if (command is ChannelCommand chCmd && chCmd.OriginatingChannel != null)
                    {
                        await gateway.FailChannelMessageAsync(
                            command.RunId,
                            ex.Message,
                            chCmd.OriginatingChannel
                        );
                    }
                }
            }
        });
        
        // Register handler for Tool lane (for tool calls)
        processor.RegisterLaneHandler(CommandLane.Tool, async (command, ct) =>
        {
            logger.LogInformation("▶️  Processing tool command: {RunId}", command.RunId);
            await Task.Delay(50, ct);
            logger.LogInformation("✅ Tool command completed");
        });
        
        // Register other lanes
        processor.RegisterLaneHandler(CommandLane.Subagent, async (command, ct) =>
        {
            logger.LogInformation("▶️  Processing subagent command: {RunId}", command.RunId);
            await Task.Delay(50, ct);
            logger.LogInformation("✅ Subagent command completed");
        });
        
        processor.RegisterLaneHandler(CommandLane.Background, async (command, ct) =>
        {
            logger.LogInformation("▶️  Processing background command: {RunId}", command.RunId);
            await Task.Delay(25, ct);
            logger.LogInformation("✅ Background command completed");
        });
        
        // Start processor
        processor.Start();
        logger.LogInformation("▶️  CommandProcessor started");
        Console.WriteLine();
        
        // 9. Simulate incoming channel messages
        logger.LogInformation("📨 Simulating incoming channel messages...");
        
        // In a real scenario, messages would arrive from external channels (Discord, Telegram, etc.)
        // For this example, we'll create messages and pass them through the gateway
        var simulatedMessages = new (string content, Channel channel)[]
        {
            ("Hello from Discord!", discordChannel),
            ("Hi from Telegram!", telegramChannel),
            ("Another Discord message", discordChannel),
        };
        
        foreach (var message in simulatedMessages)
        {
            var channelMsg = new ChannelMessage
            {
                Id = Guid.NewGuid().ToString(),
                ChannelId = message.channel.ChannelId,
                SenderId = $"user_{Guid.NewGuid().ToString("N")[..8]}",
                SenderName = "TestUser",
                Content = message.content,
                Timestamp = DateTime.UtcNow,
                Type = MessageType.Text
            };
            
            logger.LogInformation("📬 Processing message: '{Content}'", message.content);
            
            // In real scenario, this would be triggered by channel events
            // Here we call the gateway directly to simulate message arrival
            await gateway.ProcessChannelMessageAsync(
                channelMsg,
                message.channel,
                agent.Id
            );
            
            await Task.Delay(100);
        }
        
        // 10. Wait for processing
        logger.LogInformation("⏳ Waiting for messages to process...");
        await Task.Delay(2000);
        
        // 11. Display statistics
        logger.LogInformation("📊 Gateway Statistics:");
        var stats = gateway.GetStatistics();
        Console.WriteLine($"  Total received: {stats.TotalMessagesReceived}");
        Console.WriteLine($"  Total processed: {stats.TotalMessagesProcessed}");
        Console.WriteLine($"  Total failed: {stats.TotalMessagesFailed}");
        Console.WriteLine($"  Average processing time: {stats.AverageProcessingTimeMilliseconds:F1}ms");
        Console.WriteLine($"  Uptime: {stats.Uptime.TotalSeconds:F1}s");
        
        if (stats.MessagesPerChannel.Count > 0)
        {
            Console.WriteLine("  Messages per channel:");
            foreach (var (channelId, count) in stats.MessagesPerChannel)
            {
                Console.WriteLine($"    - {channelId}: {count}");
            }
        }
        Console.WriteLine();
        
        // 12. Display queue statistics
        var procStats = processor.GetStatistics();
        Console.WriteLine($"📋 Processor Statistics:");
        Console.WriteLine($"  Total processed: {procStats.TotalProcessed}");
        Console.WriteLine($"  Total failed: {procStats.TotalFailed}");
        Console.WriteLine($"  Queued commands: {procStats.QueuedCommands}");
        Console.WriteLine($"  Uptime: {procStats.Uptime.TotalSeconds:F1}s");
        Console.WriteLine();
        
        // 13. Cleanup
        await processor.StopAsync();
        await channelManager.DisconnectAllAsync();
        gateway.Dispose();
        
        logger.LogInformation("✓ Example completed successfully");
    }
    
    /// <summary>
    /// Create a simple tool registry for the example
    /// </summary>
    private static ToolRegistry CreateToolRegistry()
    {
        var toolRegistry = new ToolRegistry();
        
        // Add example tools
        var toolDef = new ToolDefinition
        {
            Name = "echo",
            Description = "Echo the input back",
            Parameters = new Dictionary<string, Models.ToolParameter>
            {
                ["text"] = new() { Type = "string", Description = "Text to echo", Required = true }
            }
        };
        
        // In a real implementation, would register with toolRegistry
        // For now, just return the registry
        return toolRegistry;
    }
}

/// <summary>
/// Example of advanced multi-lane routing based on message characteristics
/// </summary>
public class AdvancedChannelGatewayExample
{
    public static async Task RunAdvancedExample()
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║     Advanced: Multi-Lane Channel Message Routing            ║");
        Console.WriteLine("║    Different lanes for different message types             ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        
        var loggerFactory = new LoggerFactory();
        var logger = loggerFactory.CreateLogger<AdvancedChannelGatewayExample>();
        
        var toolRegistry = new ToolRegistry();
        var agentRuntime = new DefaultAgentRuntime(toolRegistry, logger: logger);
        var commandQueue = new CommandQueue();
        var gateway = new ChannelMessageGateway(commandQueue, agentRuntime, logger);
        var agent = new AgentBuilder(toolRegistry).WithName("AdvancedAgent").Build();
        var channelManager = new ChannelManager(agent, logger);
        channelManager.SetGateway(gateway);
        
        var channel = new DiscordChannel("token", 123, 456);
        channelManager.AddChannel(channel);
        
        // Simulate different types of messages with routing
        var testMessages = new[]
        {
            new { Content = "Hello?", Channel = channel, Priority = "normal" },
            new { Content = "URGENT: System down!", Channel = channel, Priority = "high" },
            new { Content = "Please process this 10MB file...", Channel = channel, Priority = "background" }
        };
        
        foreach (var test in testMessages)
        {
            var msg = new ChannelMessage
            {
                Id = Guid.NewGuid().ToString(),
                ChannelId = channel.ChannelId,
                Content = test.Content,
                SenderId = "user_123",
                SenderName = "TestUser"
            };
            
            // Dynamic lane routing based on message content
            CommandLane lane = test.Priority switch
            {
                "high" => CommandLane.Main,           // High priority = Main lane
                "background" => CommandLane.Background, // Non-urgent = Background lane
                _ => CommandLane.Tool                 // Regular = Tool lane
            };
            
            int? timeout = test.Priority switch
            {
                "high" => 60,        // Urgent: 1 minute timeout
                "background" => 900, // Background: 15 minutes
                _ => 300             // Normal: 5 minutes
            };
            
            logger.LogInformation("📬 Routing [{Priority}] message to {Lane} lane (timeout: {Timeout}s)",
                test.Priority, lane, timeout);
            
            await gateway.ProcessChannelMessageAsync(
                msg,
                channel,
                agent.Id,
                overrideLane: lane,
                overrideTimeoutSeconds: timeout
            );
        }
    }
}
