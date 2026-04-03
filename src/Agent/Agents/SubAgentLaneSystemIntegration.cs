using AgentFox.Models;
using Microsoft.Extensions.Logging;

namespace AgentFox.Agents;

/// <summary>
/// Example integration demonstrating how to use the sub-agent lane execution system
/// Shows how to wire up the components and handle different scenarios
/// </summary>
public class SubAgentLaneSystemIntegration
{
    private readonly ICommandQueue _commandQueue;
    private readonly CommandProcessor _commandProcessor;
    private readonly SubAgentManager _subAgentManager;
    private readonly IAgentRuntime _agentRuntime;
    private readonly ILogger? _logger;
    
    public SubAgentLaneSystemIntegration(
        IAgentRuntime agentRuntime,
        SubAgentConfiguration? config = null,
        ILogger? logger = null)
    {
        _agentRuntime = agentRuntime;
        _logger = logger;
        
        // Initialize the command queue
        _commandQueue = new CommandQueue();
        
        // Initialize the command processor
        _commandProcessor = new CommandProcessor(
            _commandQueue,
            _logger,
            processingDelayMilliseconds: 10,
            maxCommandsPerBatch: 5);
        
        // Initialize the sub-agent manager
        _subAgentManager = new SubAgentManager(
            _commandQueue,
            _agentRuntime,
            config ?? new SubAgentConfiguration(),
            _logger);
        
        // Register handlers for each lane
        RegisterCommandHandlers();
    }
    
    /// <summary>
    /// Initialize and start the system
    /// </summary>
    public void Initialize()
    {
        _logger?.LogInformation("Initializing SubAgentLaneSystem");
        _commandProcessor.Start();
        _logger?.LogInformation("SubAgentLaneSystem initialized and processing started");
    }
    
    /// <summary>
    /// Shutdown the system gracefully
    /// </summary>
    public async Task ShutdownAsync()
    {
        _logger?.LogInformation("Shutting down SubAgentLaneSystem");
        
        // Stop processing new commands
        await _commandProcessor.StopAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        
        // Cleanup all sub-agents
        await _subAgentManager.ForceCleanupAllAsync().ConfigureAwait(false);
        
        _logger?.LogInformation("SubAgentLaneSystem shutdown complete");
    }
    
    /// <summary>
    /// Execute a command in the main lane
    /// </summary>
    public async Task<AgentResult> ExecuteMainAgentCommandAsync(
        string sessionKey,
        string agentId,
        string message)
    {
        var command = AgentCommand.CreateMainCommand(sessionKey, agentId, message);
        _commandQueue.Enqueue(command);
        
        _logger?.LogInformation($"Queued main agent command: {command.RunId}");
        
        // In a real implementation, you would track the result via callbacks or similar mechanism
        await Task.Delay(100); // Simulate processing
        return new AgentResult { Success = true, Output = "Command queued for processing" };
    }
    
    /// <summary>
    /// Spawn a sub-agent to handle a complex task
    /// </summary>
    public async Task<SubAgentSpawnResult> SpawnSubAgentAsync(
        string parentSessionKey,
        string parentAgentId,
        string taskMessage,
        string? model = null,
        string? thinkingLevel = null)
    {
        var result = await _subAgentManager.SpawnSubAgentAsync(
            parentSessionKey,
            parentAgentId,
            taskMessage,
            parentSpawnDepth: 0,
            model: model,
            thinkingLevel: thinkingLevel).ConfigureAwait(false);
        
            if (result.Success && result.Task != null)
            {
                // Wait for completion with timeout
                var completionTask = result.Task.Completion.Task;
                var delayTask = Task.Delay(result.Task.TimeoutSeconds * 1000);
                
                var completedTask = await Task.WhenAny(completionTask, delayTask).ConfigureAwait(false);
                
                if (completedTask == delayTask && !string.IsNullOrEmpty(result.RunId))
                {
                    _logger?.LogWarning($"Sub-agent {result.RunId} timed out");
                    await _subAgentManager.CancelSubAgentAsync(result.RunId).ConfigureAwait(false);
                }
            }
        
        return result;
    }
    
    /// <summary>
    /// Get current system statistics
    /// </summary>
    public void PrintStatistics()
    {
        var processorStats = _commandProcessor.GetStatistics();
        var managerStats = _subAgentManager.GetStatistics();
        
        _logger?.LogInformation("=== SubAgentLaneSystem Statistics ===");
        _logger?.LogInformation($"Processor Uptime: {processorStats.Uptime}");
        _logger?.LogInformation($"Commands Processed: {processorStats.TotalProcessed}");
        _logger?.LogInformation($"Commands Failed: {processorStats.TotalFailed}");
        _logger?.LogInformation($"Queued Commands: {processorStats.QueuedCommands}");
        _logger?.LogInformation($"Active Sub-Agents: {managerStats.TotalActiveSubAgents}");
        _logger?.LogInformation($"Running Sub-Agents: {managerStats.RunningSubAgents}");
        _logger?.LogInformation($"Pending Sub-Agents: {managerStats.PendingSubAgents}");
        _logger?.LogInformation($"Completed Sub-Agents: {managerStats.CompletedSubAgents}");
        _logger?.LogInformation($"Failed Sub-Agents: {managerStats.FailedSubAgents}");
        _logger?.LogInformation($"Timed Out Sub-Agents: {managerStats.TimedOutSubAgents}");
    }
    
    /// <summary>
    /// Register command handlers for each lane
    /// In a real implementation, these would delegate to the appropriate executors
    /// </summary>
    private async Task HandleResultAnnouncementAsync(
        ResultAnnouncementCommand announcementCmd,
        CancellationToken ct)
    {
        try
        {
            _logger?.LogInformation(
                "Announcing sub-agent result to channel (Correlation: {CorrelationId})",
                announcementCmd.CorrelationId);
            
            // Format the result message
            var message = announcementCmd.FormatMessage();
            
            // Send back to originating channel if specified
            if (announcementCmd.RequesterChannel != null && !announcementCmd.SuppressChannelNotification)
            {
                try
                {
                    await announcementCmd.RequesterChannel.SendMessageAsync(message).ConfigureAwait(false);
                    
                    _logger?.LogInformation(
                        "Result announced successfully to channel {ChannelId} (Correlation: {CorrelationId})",
                        announcementCmd.RequesterChannel.ChannelId,
                        announcementCmd.CorrelationId);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(
                        ex,
                        "Failed to announce result to channel {ChannelId}",
                        announcementCmd.RequesterChannel.ChannelId);
                }
            }
            else if (announcementCmd.SuppressChannelNotification)
            {
                _logger?.LogDebug(
                    "Result announcement suppressed (local only) (Correlation: {CorrelationId})",
                    announcementCmd.CorrelationId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error handling result announcement");
        }
    }
    
    /// <summary>
    /// Register command handlers for each lane
    /// In a real implementation, these would delegate to the appropriate executors
    /// </summary>
    private void RegisterCommandHandlers()
    {
        // Main lane handler (for both AgentCommand and ResultAnnouncementCommand)
        _commandProcessor.RegisterLaneHandler(CommandLane.Main, async (command, ct) =>
        {
            if (command is ResultAnnouncementCommand announcementCmd)
            {
                // ✅ NEW: Handle result announcements (OpenClaw-inspired)
                await HandleResultAnnouncementAsync(announcementCmd, ct);
            }
            else if (command is AgentCommand agentCmd)
            {
                _logger?.LogInformation($"Executing main command: {agentCmd.Message.Substring(0, Math.Min(50, agentCmd.Message.Length))}...");
                
                try
                {
                    var result = await _agentRuntime.ExecuteAsync(agentCmd, ct).ConfigureAwait(false);

                    _logger?.LogInformation($"Main command completed successfully: {agentCmd.RunId}");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Error executing main command: {ex.Message}");
                }
            }
        });
        
        // Sub-agent lane handler
        _commandProcessor.RegisterLaneHandler(CommandLane.Subagent, async (command, ct) =>
        {
            if (command is AgentCommand agentCmd)
            {
                var runId = agentCmd.RunId;
                
                try
                {
                    _subAgentManager.OnSubAgentStarted(runId);

                    _logger?.LogInformation($"Executing sub-agent: {agentCmd.SessionKey}");

                    // Delegate to the runtime — FoxAgentExecutor handles model overrides automatically
                    var result = await _agentRuntime.ExecuteAsync(agentCmd, ct).ConfigureAwait(false);
                    
                    // Notify completion
                    var completionResult = new SubAgentCompletionResult
                    {
                        Status = result.Success ? SubAgentState.Completed : SubAgentState.Failed,
                        Output = result.Output,
                        Error = result.Error,
                        AgentResult = result,
                        Duration = result.Duration
                    };
                    
                    _subAgentManager.OnSubAgentCompleted(runId, completionResult);
                    
                    _logger?.LogInformation($"Sub-agent completed successfully: {runId}");
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogInformation($"Sub-agent cancelled: {runId}");
                    var cancelResult = SubAgentCompletionResult.Cancelled();
                    _subAgentManager.OnSubAgentCompleted(runId, cancelResult);
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Error executing sub-agent: {ex.Message}");
                    var failureResult = SubAgentCompletionResult.Failure(ex.Message);
                    _subAgentManager.OnSubAgentCompleted(runId, failureResult);
                }
            }
        });
        
        // Tool lane handler
        _commandProcessor.RegisterLaneHandler(CommandLane.Tool, async (command, ct) =>
        {
            if (command is AgentCommand agentCmd)
            {
                _logger?.LogDebug($"Executing tool command: {agentCmd.RunId}");
                // Tool execution logic would go here
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        });
        
        // Background lane handler
        _commandProcessor.RegisterLaneHandler(CommandLane.Background, async (command, ct) =>
        {
            if (command is AgentCommand agentCmd)
            {
                _logger?.LogDebug($"Executing background task: {agentCmd.RunId}");
                // Background task logic would go here
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        });
    }
}

/// <summary>
/// Example of how to initialize and use the sub-agent lane system
/// </summary>
public static class SubAgentLaneSystemExample
{
    public static async Task RunExampleAsync(IAgentRuntime agentRuntime, ILogger? logger)
    {
        // Create configuration
        var config = new SubAgentConfiguration
        {
            MaxSpawnDepth = 3,
            MaxConcurrentSubAgents = 10,
            MaxChildrenPerAgent = 5,
            DefaultRunTimeoutSeconds = 300,
            DefaultModel = "gpt-4",
            DefaultThinkingLevel = "high"
        };
        
        // Initialize the system
        var system = new SubAgentLaneSystemIntegration(agentRuntime, config, logger);
        system.Initialize();
        
        try
        {
            // Execute main agent command
            logger?.LogInformation("=== Executing Main Agent Command ===");
            var mainResult = await system.ExecuteMainAgentCommandAsync(
                "session:main-agent",
                "main-agent-1",
                "What is 2+2?");
            
            logger?.LogInformation($"Main command result: {mainResult.Output}");
            
            // Spawn a sub-agent
            logger?.LogInformation("=== Spawning Sub-Agent ===");
            var spawnResult = await system.SpawnSubAgentAsync(
                "session:main-agent",
                "main-agent-1",
                "Solve the Fibonacci sequence up to 10",
                model: "gpt-4",
                thinkingLevel: "high");
            
            if (spawnResult.Success)
            {
                logger?.LogInformation($"Sub-agent spawned: {spawnResult.SubAgentSessionKey}");
            }
            else
            {
                logger?.LogWarning($"Failed to spawn sub-agent: {spawnResult.Error}");
            }
            
            // Print statistics
            await Task.Delay(2000); // Wait for processing
            system.PrintStatistics();
        }
        finally
        {
            // Shutdown gracefully
            await system.ShutdownAsync();
        }
    }
}
