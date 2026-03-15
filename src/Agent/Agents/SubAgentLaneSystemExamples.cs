using AgentFox.Models;
using Microsoft.Extensions.Logging;

namespace AgentFox.Agents;

/// <summary>
/// Practical code examples demonstrating various usage patterns of the sub-agent lane system
/// </summary>
public class SubAgentLaneSystemExamples
{
    /// <summary>
    /// Example 1: Basic setup with default configuration
    /// </summary>
    public static async Task Example1_BasicSetup(IAgentRuntime agentRuntime, ILogger logger)
    {
        logger.LogInformation("=== Example 1: Basic Setup ===");
        
        // Create with default configuration
        var system = new SubAgentLaneSystemIntegration(agentRuntime, logger: logger);
        system.Initialize();
        
        try
        {
            // Execute a main command
            await system.ExecuteMainAgentCommandAsync(
                "session:default",
                "main-agent",
                "What is machine learning?");
            
            await Task.Delay(1000);
            system.PrintStatistics();
        }
        finally
        {
            await system.ShutdownAsync();
        }
    }
    
    /// <summary>
    /// Example 2: Spawning multiple sub-agents with different depths
    /// </summary>
    public static async Task Example2_MultipleSubAgents(IAgentRuntime agentRuntime, ILogger logger)
    {
        logger.LogInformation("=== Example 2: Multiple Sub-Agents ===");
        
        var config = new SubAgentConfiguration
        {
            MaxSpawnDepth = 3,
            MaxConcurrentSubAgents = 5,
            MaxChildrenPerAgent = 3
        };
        
        var system = new SubAgentLaneSystemIntegration(agentRuntime, config, logger);
        system.Initialize();
        
        try
        {
            var tasks = new List<Task<SubAgentSpawnResult>>();
            
            // Spawn multiple sub-agents
            for (int i = 0; i < 3; i++)
            {
                var task = system.SpawnSubAgentAsync(
                    "session:multi",
                    $"parent-agent-{i % 2}",
                    $"Task {i + 1}: Analyze dataset {i + 1}");
                
                tasks.Add(task);
            }
            
            var results = await Task.WhenAll(tasks);
            
            logger.LogInformation($"Spawned {results.Length} sub-agents");
            foreach (var result in results)
            {
                logger.LogInformation($"  Status: {result.Status}, RunId: {result.RunId}");
            }
            
            await Task.Delay(2000);
            system.PrintStatistics();
        }
        finally
        {
            await system.ShutdownAsync();
        }
    }
    
    /// <summary>
    /// Example 3: Handling spawn policy rejections
    /// </summary>
    public static async Task Example3_PolicyEnforcement(IAgentRuntime agentRuntime, ILogger logger)
    {
        logger.LogInformation("=== Example 3: Policy Enforcement ===");
        
        // Very restrictive configuration to demonstrate policy enforcement
        var config = new SubAgentConfiguration
        {
            MaxSpawnDepth = 0,  // No sub-agents allowed
            MaxConcurrentSubAgents = 1,
            MaxChildrenPerAgent = 1
        };
        
        var system = new SubAgentLaneSystemIntegration(agentRuntime, config, logger);
        system.Initialize();
        
        try
        {
            // This will be rejected due to MaxSpawnDepth = 0
            var result1 = await system.SpawnSubAgentAsync(
                "session:policy",
                "agent-1",
                "This should be rejected");
            
            logger.LogInformation($"Result 1 - Success: {result1.Success}, Error: {result1.Error}");
            
            // Try spawning at depth 1 (also rejected)
            var result2 = await system.SpawnSubAgentAsync(
                "session:policy",
                "agent-2",
                "Also rejected");
            
            logger.LogInformation($"Result 2 - Success: {result2.Success}, Error: {result2.Error}");
        }
        finally
        {
            await system.ShutdownAsync();
        }
    }
    
    /// <summary>
    /// Example 4: Monitoring sub-agent states and statistics
    /// </summary>
    public static async Task Example4_MonitoringAndStatistics(IAgentRuntime agentRuntime, ILogger logger)
    {
        logger.LogInformation("=== Example 4: Monitoring Statistics ===");
        
        var config = new SubAgentConfiguration
        {
            EnableVerboseLogging = true
        };
        
        var system = new SubAgentLaneSystemIntegration(agentRuntime, config, logger);
        system.Initialize();
        
        try
        {
            // Spawn a sub-agent
            var result = await system.SpawnSubAgentAsync(
                "session:monitor",
                "agent-1",
                "Long running task");
            
            if (result.Success && result.Task != null)
            {
                logger.LogInformation($"Sub-agent state: {result.Task.State}");
                logger.LogInformation($"Spawn depth: {result.Task.SpawnDepth}");
                logger.LogInformation($"Created at: {result.Task.CreatedAt}");
                
                // Monitor for status change
                for (int i = 0; i < 5; i++)
                {
                    await Task.Delay(500);
                    logger.LogInformation($"  Current state: {result.Task.State}");
                }
            }
            
            system.PrintStatistics();
        }
        finally
        {
            await system.ShutdownAsync();
        }
    }
    
    /// <summary>
    /// Example 5: Custom command queue with priority handling
    /// </summary>
    public static async Task Example5_CustomQueueHandling(IAgentRuntime agentRuntime, ILogger logger)
    {
        logger.LogInformation("=== Example 5: Custom Queue Handling ===");
        
        // Create a custom queue
        var queue = new CommandQueue();
        
        // Enqueue commands with different priorities
        var cmd1 = AgentCommand.CreateMainCommand("session:custom", "agent-1", "High priority task");
        cmd1.Priority = 10;
        
        var cmd2 = AgentCommand.CreateMainCommand("session:custom", "agent-1", "Low priority task");
        cmd2.Priority = 1;
        
        queue.Enqueue(cmd1);
        queue.Enqueue(cmd2);
        
        logger.LogInformation($"Main lane queue count: {queue.GetQueueCount(CommandLane.Main)}");
        logger.LogInformation($"Total queue count: {queue.GetTotalQueueCount()}");
        
        // Dequeue commands
        while (queue.TryDequeue(out var command))
        {
            if (command is AgentCommand agentCmd)
            {
                logger.LogInformation($"Dequeued - Priority: {agentCmd.Priority}, Message: {agentCmd.Message}");
            }
        }
        
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// Example 6: Handling sub-agent timeouts
    /// </summary>
    public static async Task Example6_TimeoutHandling(IAgentRuntime agentRuntime, ILogger logger)
    {
        logger.LogInformation("=== Example 6: Timeout Handling ===");
        
        var config = new SubAgentConfiguration
        {
            DefaultRunTimeoutSeconds = 2  // Very short timeout for demo
        };
        
        var system = new SubAgentLaneSystemIntegration(agentRuntime, config, logger);
        system.Initialize();
        
        try
        {
            // Spawn sub-agent with short timeout
            var result = await system.SpawnSubAgentAsync(
                "session:timeout",
                "agent-1",
                "Long running computation");
            
            if (result.Success && result.Task != null)
            {
                logger.LogInformation($"Sub-agent spawned with {result.Task.TimeoutSeconds}s timeout");
                
                // Monitor until timeout or completion
                while (!result.Task.IsTimedOut && result.Task.IsActive)
                {
                    await Task.Delay(500);
                    logger.LogInformation($"  Elapsed: {result.Task.ElapsedTime.TotalSeconds:F1}s, State: {result.Task.State}");
                }
                
                if (result.Task.IsTimedOut)
                {
                    logger.LogInformation("Sub-agent timed out as expected");
                }
            }
        }
        finally
        {
            await system.ShutdownAsync();
        }
    }
    
    /// <summary>
    /// Example 7: Direct SubAgentManager usage for advanced scenarios
    /// </summary>
    public static async Task Example7_DirectManagerUsage(IAgentRuntime agentRuntime, ILogger logger)
    {
        logger.LogInformation("=== Example 7: Direct Manager Usage ===");
        
        var config = new SubAgentConfiguration
        {
            MaxSpawnDepth = 2,
            MaxConcurrentSubAgents = 5
        };
        
        var queue = new CommandQueue();
        var manager = new SubAgentManager(queue, agentRuntime, config, logger);
        
        try
        {
            // Spawn sub-agents directly
            var spawnResults = new List<SubAgentSpawnResult>();
            
            for (int i = 0; i < 3; i++)
            {
                var result = await manager.SpawnSubAgentAsync(
                    "session:direct",
                    $"agent-{i}",
                    $"Task {i}: Direct spawn test",
                    parentSpawnDepth: 0);
                
                spawnResults.Add(result);
                logger.LogInformation($"Spawn attempt {i + 1}: {(result.Success ? "Success" : "Failed")} - {result.Error}");
            }
            
            // Get statistics
            var stats = manager.GetStatistics();
            logger.LogInformation($"Total active sub-agents: {stats.TotalActiveSubAgents}");
            logger.LogInformation($"Running: {stats.RunningSubAgents}");
            logger.LogInformation($"Pending: {stats.PendingSubAgents}");
            
            // Get active sub-agents
            var activeAgents = manager.GetActiveSubAgents();
            logger.LogInformation($"Active agents list: {string.Join(", ", activeAgents.Select(a => a.RunId))}");
            
            // Cleanup
            await manager.ForceCleanupAllAsync();
        }
        finally
        {
            manager.Dispose();
        }
    }
    
    /// <summary>
    /// Example 8: Configuration validation
    /// </summary>
    public static void Example8_ConfigurationValidation(ILogger logger)
    {
        logger.LogInformation("=== Example 8: Configuration Validation ===");
        
        // Invalid configuration
        var invalidConfig = new SubAgentConfiguration
        {
            MaxConcurrentSubAgents = -1,  // Invalid: must be >= 1
            DefaultRunTimeoutSeconds = 0   // Invalid: must be >= 1
        };
        
        var validation = invalidConfig.Validate();
        
        if (!validation.IsValid)
        {
            logger.LogWarning("Configuration validation failed:");
            foreach (var error in validation.Errors)
            {
                logger.LogWarning($"  - {error}");
            }
        }
        
        // Valid configuration
        var validConfig = new SubAgentConfiguration
        {
            MaxSpawnDepth = 3,
            MaxConcurrentSubAgents = 10,
            MaxChildrenPerAgent = 5,
            DefaultRunTimeoutSeconds = 300
        };
        
        validation = validConfig.Validate();
        logger.LogInformation($"Valid configuration: {validation.IsValid}");
    }
    
    /// <summary>
    /// Example 9: Command priority ordering across lanes
    /// </summary>
    public static async Task Example9_LanePriorityOrdering(IAgentRuntime agentRuntime, ILogger logger)
    {
        logger.LogInformation("=== Example 9: Lane Priority Ordering ===");
        
        var queue = new CommandQueue();
        
        // Enqueue commands in all lanes
        for (int i = 0; i < 2; i++)
        {
            queue.Enqueue(AgentCommand.CreateMainCommand("s", "a", $"Main {i}"));
            queue.Enqueue(new AgentCommand 
            { 
                SessionKey = "s", 
                Lane = CommandLane.Background, 
                Message = $"Background {i}" 
            });
            queue.Enqueue(new AgentCommand 
            { 
                SessionKey = "s", 
                Lane = CommandLane.Tool, 
                Message = $"Tool {i}" 
            });
            queue.Enqueue(AgentCommand.CreateSubagentCommand("s", "a", $"Subagent {i}"));
        }
        
        logger.LogInformation("Dequeuing with priority:");
        int order = 1;
        while (queue.TryDequeue(out var cmd))
        {
            if (cmd is AgentCommand agentCmd)
            {
                logger.LogInformation($"{order}. Lane: {agentCmd.Lane}, Message: {agentCmd.Message}");
                order++;
            }
        }
        
        await Task.CompletedTask;
    }
}

/// <summary>
/// Interactive example demonstrating the full workflow
/// </summary>
public static class SubAgentLaneSystemInteractiveExample
{
    public static async Task RunInteractiveExampleAsync(
        IAgentRuntime agentRuntime,
        ILogger? logger)
    {
        Console.WriteLine("\n╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  Sub-Agent Lane Execution System - Interactive Example      ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝\n");
        
        var config = new SubAgentConfiguration
        {
            MaxSpawnDepth = 3,
            MaxConcurrentSubAgents = 5,
            MaxChildrenPerAgent = 3
        };
        
        var system = new SubAgentLaneSystemIntegration(agentRuntime, config, logger);
        system.Initialize();
        
        try
        {
            bool running = true;
            
            while (running)
            {
                Console.WriteLine("\nOptions:");
                Console.WriteLine("1. Spawn sub-agent");
                Console.WriteLine("2. Execute main command");
                Console.WriteLine("3. View statistics");
                Console.WriteLine("4. Exit");
                Console.Write("\nSelect option: ");
                
                var choice = Console.ReadLine();
                
                switch (choice)
                {
                    case "1":
                        Console.Write("Enter task message: ");
                        var taskMsg = Console.ReadLine() ?? "Default task";
                        var spawnResult = await system.SpawnSubAgentAsync(
                            "session:interactive",
                            "interactive-agent",
                            taskMsg);
                        Console.WriteLine($"Result: {(spawnResult.Success ? "Success" : "Failed")}");
                        if (!spawnResult.Success)
                            Console.WriteLine($"Error: {spawnResult.Error}");
                        break;
                    
                    case "2":
                        Console.Write("Enter command message: ");
                        var cmdMsg = Console.ReadLine() ?? "Default command";
                        await system.ExecuteMainAgentCommandAsync(
                            "session:interactive",
                            "interactive-agent",
                            cmdMsg);
                        Console.WriteLine("Command queued");
                        break;
                    
                    case "3":
                        system.PrintStatistics();
                        break;
                    
                    case "4":
                        running = false;
                        break;
                    
                    default:
                        Console.WriteLine("Invalid option");
                        break;
                }
            }
        }
        finally
        {
            await system.ShutdownAsync();
        }
    }
}
