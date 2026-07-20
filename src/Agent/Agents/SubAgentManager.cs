using System.Collections.Concurrent;
using AgentFox.Models;
using AgentFox.Sessions;
using Microsoft.Extensions.Logging;

namespace AgentFox.Agents;

/// <summary>
/// Delegate for handling sub-agent result announcements
/// Inspired by OpenClaw's event-driven architecture for result routing
/// </summary>
public delegate Task<ResultAnnouncementCommand?> SubAgentResultCallback(
    SubAgentTask task,
    SubAgentCompletionResult result);

/// <summary>
/// Result of a sub-agent spawn operation (lane system specific)
/// </summary>
public class SubAgentSpawnResult
{
    public bool Success { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? SubAgentSessionKey { get; set; }
    public string? RunId { get; set; }
    public string? Error { get; set; }
    public SubAgentTask? Task { get; set; }
}

/// <summary>
/// Policy check result indicating whether an action is allowed
/// </summary>
public class PolicyCheckResult
{
    public bool IsAllowed { get; set; } = true;
    public string? Reason { get; set; }
}

/// <summary>
/// Manages the complete lifecycle of sub-agent spawning, execution, and cleanup
/// Implements policy enforcement for spawn depth, concurrency limits, and timeouts
/// Inspired by OpenClaw's robust sub-agent management approach
/// </summary>
public class SubAgentManager : IDisposable
{
    private readonly ICommandQueue _commandQueue;
    private readonly IAgentRuntime _agentRuntime;
    private readonly SubAgentConfiguration _config;
    private readonly SessionManager? _sessionManager;
    private readonly ILogger? _logger;
    
    // Active sub-agents tracking
    private readonly ConcurrentDictionary<string, SubAgentTask> _activeSubAgents;
    
    // Track spawn count per parent agent to enforce MaxChildrenPerAgent
    private readonly ConcurrentDictionary<string, int> _childCountPerAgent;
    
    // Event for result announcements (OpenClaw-inspired callback system)
    private event SubAgentResultCallback? OnSubAgentFinalized;
    
    private bool _disposed;
    
    public SubAgentManager(
        ICommandQueue commandQueue,
        IAgentRuntime agentRuntime,
        SubAgentConfiguration? config = null,
        ILogger? logger = null,
        SessionManager? sessionManager = null)
    {
        _commandQueue = commandQueue ?? throw new ArgumentNullException(nameof(commandQueue));
        _agentRuntime = agentRuntime ?? throw new ArgumentNullException(nameof(agentRuntime));
        _config = config ?? new SubAgentConfiguration();
        _logger = logger;
        _sessionManager = sessionManager;
        _activeSubAgents = new ConcurrentDictionary<string, SubAgentTask>();
        _childCountPerAgent = new ConcurrentDictionary<string, int>();
        
        // Validate configuration
        var validationResult = _config.Validate();
        if (!validationResult.IsValid)
        {
            var errors = string.Join("; ", validationResult.Errors);
            throw new ArgumentException($"Invalid SubAgentConfiguration: {errors}");
        }
        
        _logger?.LogInformation("SubAgentManager initialized with configuration: " +
            $"MaxSpawnDepth={_config.MaxSpawnDepth}, " +
            $"MaxConcurrentSubAgents={_config.MaxConcurrentSubAgents}, " +
            $"DefaultTimeout={_config.DefaultRunTimeoutSeconds}s");
    }
    
    /// <summary>
    /// Register a callback to be invoked when a sub-agent completes
    /// Enables result announcement routing (OpenClaw-inspired)
    /// The callback receives the completed task and result, and can return a ResultAnnouncementCommand
    /// which will be enqueued for execution
    /// </summary>
    public void RegisterResultCallback(SubAgentResultCallback callback)
    {
        OnSubAgentFinalized += callback;
        _logger?.LogDebug("Result callback registered for sub-agent completion announcements");
    }
    
    /// <summary>
    /// Unregister a previously registered result callback
    /// </summary>
    public void UnregisterResultCallback(SubAgentResultCallback callback)
    {
        OnSubAgentFinalized -= callback;
        _logger?.LogDebug("Result callback unregistered");
    }
    
    /// <summary>
    /// Spawn a new sub-agent with the given task
    /// Performs all policy checks and enqueues the command for processing
    /// </summary>
    public async Task<SubAgentSpawnResult> SpawnSubAgentAsync(
        string parentSessionKey,
        string parentAgentId,
        string taskMessage,
        int parentSpawnDepth = 0,
        string? model = null,
        string? thinkingLevel = null,
        int? timeoutSeconds = null)
    {
        try
        {
            _logger?.LogInformation($"Spawn request for sub-agent from parent: {parentAgentId}");
            
            // 1. Policy checks
            var depthCheck = CheckSpawnDepth(parentSpawnDepth);
            if (!depthCheck.IsAllowed)
            {
                _logger?.LogWarning($"Spawn depth policy check failed: {depthCheck.Reason}");
                return new SubAgentSpawnResult
                {
                    Success = false,
                    Status = "rejected",
                    Error = depthCheck.Reason
                };
            }
            
            var concurrencyCheck = CheckConcurrency();
            if (!concurrencyCheck.IsAllowed)
            {
                _logger?.LogWarning($"Concurrency policy check failed: {concurrencyCheck.Reason}");
                return new SubAgentSpawnResult
                {
                    Success = false,
                    Status = "rejected",
                    Error = concurrencyCheck.Reason
                };
            }
            
            var childrenCheck = CheckChildrenLimit(parentAgentId);
            if (!childrenCheck.IsAllowed)
            {
                _logger?.LogWarning($"Children limit policy check failed: {childrenCheck.Reason}");
                return new SubAgentSpawnResult
                {
                    Success = false,
                    Status = "rejected",
                    Error = childrenCheck.Reason
                };
            }
            
            // 2. Generate sub-agent session key (filesystem-safe, scoped to parent agent directory)
            var subAgentId = Guid.NewGuid().ToString("N");
            string subAgentSessionKey = _sessionManager != null
                ? _sessionManager.CreateSubAgentSession(parentAgentId, subAgentId, parentSessionKey)
                : $"agent_{parentAgentId}_subagent_{subAgentId}"; // fallback: no colons for Windows FS safety
            
            // 3. Create sub-agent command
            var command = AgentCommand.CreateSubagentCommand(
                subAgentSessionKey,
                parentAgentId,
                taskMessage,
                model ?? _config.DefaultModel,
                thinkingLevel ?? _config.DefaultThinkingLevel,
                timeoutSeconds ?? _config.DefaultRunTimeoutSeconds
            );
            
            // 4. Create and track SubAgentTask
            var subAgentTask = new SubAgentTask
            {
                SessionKey = subAgentSessionKey,
                RunId = command.RunId,
                ParentAgentId = parentAgentId,
                ParentSessionKey = parentSessionKey,
                TaskPayload = taskMessage,
                Model = model ?? _config.DefaultModel,
                ThinkingLevel = thinkingLevel ?? _config.DefaultThinkingLevel,
                TimeoutSeconds = timeoutSeconds ?? _config.DefaultRunTimeoutSeconds,
                MaxIterations = _config.DefaultMaxIterations,
                SpawnDepth = parentSpawnDepth + 1,
                State = SubAgentState.Pending
            };
            
            _activeSubAgents.TryAdd(command.RunId, subAgentTask);
            _childCountPerAgent.AddOrUpdate(parentAgentId, 1, (_, count) => count + 1);
            
            // 5. Enqueue the command for processing
            _commandQueue.Enqueue(command);
            
            _logger?.LogInformation($"Sub-agent spawned successfully: " +
                $"SessionKey={subAgentSessionKey}, RunId={command.RunId}, Depth={subAgentTask.SpawnDepth}");
            
            return new SubAgentSpawnResult
            {
                Success = true,
                Status = "accepted",
                SubAgentSessionKey = subAgentSessionKey,
                RunId = command.RunId,
                Task = subAgentTask
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Error spawning sub-agent: {ex.Message}");
            return new SubAgentSpawnResult
            {
                Success = false,
                Status = "error",
                Error = ex.Message
            };
        }
    }
    
    /// <summary>
    /// Notify the manager that a sub-agent has started execution
    /// </summary>
    public void OnSubAgentStarted(string runId)
    {
        if (_activeSubAgents.TryGetValue(runId, out var task))
        {
            task.State = SubAgentState.Running;
            task.StartedAt = DateTime.UtcNow;
            _logger?.LogInformation($"Sub-agent started: {runId}");
        }
    }
    
    /// <summary>
    /// Notify the manager that a sub-agent has completed successfully
    /// Invokes registered result callbacks to enable result announcements
    /// </summary>
    public void OnSubAgentCompleted(string runId, SubAgentCompletionResult result)
    {
        if (_activeSubAgents.TryGetValue(runId, out var task))
        {
            task.State = result.Status;
            task.CompletedAt = DateTime.UtcNow;
            task.Completion.SetResult(result);

            // Mark aborted sessions so SessionManager archives them
            if (result.Status is SubAgentState.Cancelled or SubAgentState.TimedOut or SubAgentState.Failed)
                _sessionManager?.MarkAborted(task.SessionKey, $"sub-agent {result.Status}");

            _logger?.LogInformation($"Sub-agent completed: {runId}, Status={result.Status}");
            
            // ✅ NEW: Invoke result callbacks for result announcement routing
            if (OnSubAgentFinalized != null)
            {
                try
                {
                    // Invoke all registered callbacks (fire and forget pattern)
                    _ = InvokeResultCallbacksAsync(task, result);
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Error invoking result callbacks: {ex.Message}");
                }
            }
            
            // Decrement child count
            if (_childCountPerAgent.TryGetValue(task.ParentAgentId, out var count) && count > 0)
            {
                _childCountPerAgent.AddOrUpdate(task.ParentAgentId, 0, (_, _) => count - 1);
            }
            
            // Schedule cleanup if configured
            if (_config.AutoCleanupCompleted)
            {
                _ = ScheduleCleanupAsync(runId);
            }
            else
            {
                // Remove immediately if not scheduling cleanup
                _activeSubAgents.TryRemove(runId, out _);
            }
        }
    }
    
    /// <summary>
    /// Invoke registered result callbacks and enqueue any generated announcements
    /// </summary>
    private async Task InvokeResultCallbacksAsync(SubAgentTask task, SubAgentCompletionResult result)
    {
        var invocationList = OnSubAgentFinalized?.GetInvocationList() ?? Array.Empty<Delegate>();
        
        foreach (var del in invocationList)
        {
            if (del is SubAgentResultCallback callback)
            {
                try
                {
                    var announcementCmd = await callback.Invoke(task, result).ConfigureAwait(false);
                    
                    // If callback returns an announcement command, enqueue it
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
        }
    }
    
    /// <summary>
    /// Pause a sub-agent.  If the task is still pending/queued it will block before
    /// execution starts.  If already running, execution continues to the end of the
    /// current turn and then pauses (pause points are between turns inside the executor).
    /// </summary>
    public bool PauseSubAgent(string runId)
    {
        if (!_activeSubAgents.TryGetValue(runId, out var task)) return false;
        if (task.State is SubAgentState.Completed or SubAgentState.Failed
                       or SubAgentState.TimedOut or SubAgentState.Cancelled)
            return false;

        task.PauseGate.Pause();
        task.State = SubAgentState.Paused;
        _logger?.LogInformation("Sub-agent paused: {RunId}", runId);
        return true;
    }

    /// <summary>
    /// Resume a previously paused sub-agent, unblocking its execution gate.
    /// </summary>
    public bool ResumeSubAgent(string runId)
    {
        if (!_activeSubAgents.TryGetValue(runId, out var task)) return false;
        if (task.State != SubAgentState.Paused) return false;

        task.State = SubAgentState.Pending; // will transition to Running once dequeued
        task.PauseGate.Resume();
        _logger?.LogInformation("Sub-agent resumed: {RunId}", runId);
        return true;
    }

    /// <summary>
    /// Gracefully stop a sub-agent: signals its cancellation token and waits for it
    /// to complete the current work before returning.
    /// </summary>
    public Task<bool> StopSubAgentAsync(string runId) => CancelSubAgentAsync(runId);

    /// <summary>
    /// Force-kill a sub-agent: signals cancellation and returns immediately without
    /// waiting for the task to finish.  Use when you need a fire-and-forget abort.
    /// </summary>
    public bool KillSubAgent(string runId)
    {
        if (!_activeSubAgents.TryGetValue(runId, out var task)) return false;

        _logger?.LogInformation("Killing sub-agent: {RunId}", runId);
        _sessionManager?.MarkAborted(task.SessionKey, "killed by user");

        // Unblock any pause gate first so the cancellation propagates
        task.PauseGate.Resume();
        task.CancellationTokenSource.Cancel();
        return true;
    }

    /// <summary>
    /// Cancel a sub-agent execution
    /// </summary>
    public async Task<bool> CancelSubAgentAsync(string runId)
    {
        if (_activeSubAgents.TryGetValue(runId, out var task))
        {
            _logger?.LogInformation($"Cancelling sub-agent: {runId}");

            // Mark session as aborted before signalling cancellation
            _sessionManager?.MarkAborted(task.SessionKey, "user cancelled");

            // Unblock any pause gate first so cancellation propagates
            task.PauseGate.Resume();
            task.CancellationTokenSource.Cancel();

            // Wait for completion or timeout
            try
            {
                await task.Completion.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogInformation($"Sub-agent cancelled: {runId}");
            }

            return true;
        }

        return false;
    }
    
    /// <summary>
    /// Get information about an active sub-agent
    /// </summary>
    public SubAgentTask? GetSubAgentTask(string runId)
    {
        _activeSubAgents.TryGetValue(runId, out var task);
        return task;
    }
    
    /// <summary>
    /// Get all active sub-agents
    /// </summary>
    public IEnumerable<SubAgentTask> GetActiveSubAgents()
    {
        return _activeSubAgents.Values.ToList();
    }
    
    /// <summary>
    /// Get count of active sub-agents for a parent agent
    /// </summary>
    public int GetActiveChildCount(string parentAgentId)
    {
        return _childCountPerAgent.TryGetValue(parentAgentId, out var count) ? count : 0;
    }
    
    /// <summary>
    /// Get manager statistics
    /// </summary>
    public SubAgentManagerStatistics GetStatistics()
    {
        return new SubAgentManagerStatistics
        {
            TotalActiveSubAgents = _activeSubAgents.Count,
            TotalTrackedParents = _childCountPerAgent.Count,
            RunningSubAgents = _activeSubAgents.Values.Count(t => t.State == SubAgentState.Running),
            PendingSubAgents = _activeSubAgents.Values.Count(t => t.State == SubAgentState.Pending),
            CompletedSubAgents = _activeSubAgents.Values.Count(t => t.State == SubAgentState.Completed),
            FailedSubAgents = _activeSubAgents.Values.Count(t => t.State == SubAgentState.Failed),
            TimedOutSubAgents = _activeSubAgents.Values.Count(t => t.State == SubAgentState.TimedOut)
        };
    }
    
    /// <summary>
    /// Check if spawning a sub-agent at this depth is allowed
    /// </summary>
    private PolicyCheckResult CheckSpawnDepth(int currentDepth)
    {
        if (_config.MaxSpawnDepth >= 0 && currentDepth >= _config.MaxSpawnDepth)
        {
            return new PolicyCheckResult
            {
                IsAllowed = false,
                Reason = $"Maximum spawn depth ({_config.MaxSpawnDepth}) reached"
            };
        }
        
        return new PolicyCheckResult { IsAllowed = true };
    }
    
    /// <summary>
    /// Check if concurrent limit allows spawning another sub-agent
    /// </summary>
    private PolicyCheckResult CheckConcurrency()
    {
        var activeCount = _activeSubAgents.Values.Count(t => t.IsActive);
        
        if (activeCount >= _config.MaxConcurrentSubAgents)
        {
            return new PolicyCheckResult
            {
                IsAllowed = false,
                Reason = $"Maximum concurrent sub-agents ({_config.MaxConcurrentSubAgents}) limit reached"
            };
        }
        
        return new PolicyCheckResult { IsAllowed = true };
    }
    
    /// <summary>
    /// Check if parent agent can spawn another child
    /// </summary>
    private PolicyCheckResult CheckChildrenLimit(string parentAgentId)
    {
        if (!_childCountPerAgent.TryGetValue(parentAgentId, out var count))
        {
            return new PolicyCheckResult { IsAllowed = true };
        }
        
        if (count >= _config.MaxChildrenPerAgent)
        {
            return new PolicyCheckResult
            {
                IsAllowed = false,
                Reason = $"Parent agent ({parentAgentId}) has reached maximum children limit ({_config.MaxChildrenPerAgent})"
            };
        }
        
        return new PolicyCheckResult { IsAllowed = true };
    }
    
    /// <summary>
    /// Schedule cleanup of a completed sub-agent task
    /// </summary>
    private async Task ScheduleCleanupAsync(string runId)
    {
        try
        {
            await Task.Delay(_config.CleanupDelayMilliseconds).ConfigureAwait(false);
            _activeSubAgents.TryRemove(runId, out _);
            _logger?.LogDebug($"Cleaned up sub-agent task: {runId}");
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Error during cleanup of {runId}: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Force cleanup all sub-agents (typically on shutdown)
    /// </summary>
    public async Task ForceCleanupAllAsync()
    {
        _logger?.LogInformation("Force cleanup of all sub-agents");
        
        var tasks = _activeSubAgents.Values
            .Where(t => t.IsActive)
            .Select(t => CancelSubAgentAsync(t.RunId))
            .ToList();
        
        await Task.WhenAll(tasks).ConfigureAwait(false);
        
        _activeSubAgents.Clear();
        _childCountPerAgent.Clear();
        
        _logger?.LogInformation("Force cleanup completed");
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        try
        {
            ForceCleanupAllAsync().Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // Ignore exceptions during cleanup
        }
        
        _disposed = true;
    }
}

/// <summary>
/// Statistics about sub-agent manager state
/// </summary>
public class SubAgentManagerStatistics
{
    public int TotalActiveSubAgents { get; set; }
    public int TotalTrackedParents { get; set; }
    public int RunningSubAgents { get; set; }
    public int PendingSubAgents { get; set; }
    public int CompletedSubAgents { get; set; }
    public int FailedSubAgents { get; set; }
    public int TimedOutSubAgents { get; set; }
}
