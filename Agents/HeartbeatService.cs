using Microsoft.Extensions.Logging;
using AgentFox.Runtime;
using AgentFox.Tools;

namespace AgentFox.Agents;

/// <summary>
/// Service for handling heartbeat management commands
/// Integrates with HeartbeatManager and OpenClaw event hooks system
/// </summary>
public class HeartbeatService : IDisposable
{
    private readonly HeartbeatManager _manager;
    private readonly ToolEventHookRegistry? _hooks;
    private readonly ILogger? _logger;
    private bool _disposed;

    public event EventHandler<HeartbeatCommandResultEventArgs>? CommandExecuted;

    public HeartbeatService(HeartbeatManager manager, ToolEventHookRegistry? hooks = null, ILogger? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _hooks = hooks;
        _logger = logger;

        // Hook into heartbeat events for observability
        _manager.HeartbeatTriggered += OnHeartbeatTriggered;
        _manager.HeartbeatMissed += OnHeartbeatMissed;
        _manager.HeartbeatAdded += OnHeartbeatAdded;
        _manager.HeartbeatRemoved += OnHeartbeatRemoved;
        _manager.HeartbeatStatusChanged += OnHeartbeatStatusChanged;
    }

    /// <summary>
    /// Execute a heartbeat command
    /// </summary>
    public async Task<HeartbeatCommandResult> ExecuteCommandAsync(HeartbeatCommand command, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var startTime = DateTime.UtcNow;
        var executionId = Guid.NewGuid().ToString();

        try
        {
            _logger?.LogInformation($"[Heartbeat] Executing {command.Operation} for {command.BeatName}");

            var result = command.Operation switch
            {
                HeartbeatCommand.HeartbeatOperation.Add => ExecuteAddHeartbeat(command, executionId),
                HeartbeatCommand.HeartbeatOperation.Remove => ExecuteRemoveHeartbeat(command, executionId),
                HeartbeatCommand.HeartbeatOperation.Pause => ExecutePauseHeartbeat(command, executionId),
                HeartbeatCommand.HeartbeatOperation.Resume => ExecuteResumeHeartbeat(command, executionId),
                HeartbeatCommand.HeartbeatOperation.Update => ExecuteUpdateHeartbeat(command, executionId),
                HeartbeatCommand.HeartbeatOperation.List => ExecuteListHeartbeats(command, executionId),
                HeartbeatCommand.HeartbeatOperation.GetStatus => ExecuteGetStatus(command, executionId),
                HeartbeatCommand.HeartbeatOperation.TriggerNow => await ExecuteTriggerNowAsync(command, executionId),
                _ => HeartbeatCommandResult.CreateFailure($"Unknown operation: {command.Operation}")
            };

            var executionTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
            result.ExecutionTimeMs = (long)executionTime;
            result.ExecutionId = executionId;

            _logger?.LogInformation(
                $"[Heartbeat] {command.Operation} completed: {(result.Success ? "✓" : "✗")} in {executionTime:F0}ms");

            CommandExecuted?.Invoke(this, new HeartbeatCommandResultEventArgs
            {
                Command = command,
                Result = result,
                ExecutionTime = executionTime
            });

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"[Heartbeat] Error executing {command.Operation}");
            var executionTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

            return new HeartbeatCommandResult
            {
                Success = false,
                Message = ex.Message,
                ExecutionId = executionId,
                ExecutionTimeMs = (long)executionTime
            };
        }
    }

    private HeartbeatCommandResult ExecuteAddHeartbeat(HeartbeatCommand command, string executionId)
    {
        if (string.IsNullOrWhiteSpace(command.BeatName) || string.IsNullOrWhiteSpace(command.Task))
            return HeartbeatCommandResult.CreateFailure("Beat name and task are required");

        if (_manager.GetHeartbeat(command.BeatName) != null)
            return HeartbeatCommandResult.CreateFailure($"Heartbeat '{command.BeatName}' already exists");

        try
        {
            _manager.AddHeartbeat(
                command.BeatName,
                command.Task,
                command.IntervalSeconds ?? 60,
                command.MaxMissed ?? 3);

            // Fire hook if available
            if (_hooks != null)
            {
                _ = _hooks.InvokeHeartbeatAddedAsync(command.BeatName, command.Task, executionId);
            }

            return HeartbeatCommandResult.CreateSuccess(
                $"Heartbeat '{command.BeatName}' added successfully",
                new { name = command.BeatName, task = command.Task, interval = command.IntervalSeconds ?? 60 });
        }
        catch (Exception ex)
        {
            return HeartbeatCommandResult.CreateFailure($"Failed to add heartbeat: {ex.Message}");
        }
    }

    private HeartbeatCommandResult ExecuteRemoveHeartbeat(HeartbeatCommand command, string executionId)
    {
        if (string.IsNullOrWhiteSpace(command.BeatName))
            return HeartbeatCommandResult.CreateFailure("Beat name is required");

        var result = _manager.RemoveHeartbeat(command.BeatName);
        if (!result)
            return HeartbeatCommandResult.CreateFailure($"Heartbeat '{command.BeatName}' not found");

        // Fire hook if available
        if (_hooks != null)
        {
            _ = _hooks.InvokeHeartbeatRemovedAsync(command.BeatName, executionId);
        }

        return HeartbeatCommandResult.CreateSuccess($"Heartbeat '{command.BeatName}' removed successfully");
    }

    private HeartbeatCommandResult ExecutePauseHeartbeat(HeartbeatCommand command, string executionId)
    {
        if (string.IsNullOrWhiteSpace(command.BeatName))
            return HeartbeatCommandResult.CreateFailure("Beat name is required");

        var result = _manager.PauseHeartbeat(command.BeatName);
        if (!result)
            return HeartbeatCommandResult.CreateFailure($"Heartbeat '{command.BeatName}' not found");

        // Fire hook if available
        if (_hooks != null)
        {
            _ = _hooks.InvokeHeartbeatPausedAsync(command.BeatName, executionId);
        }

        return HeartbeatCommandResult.CreateSuccess($"Heartbeat '{command.BeatName}' paused");
    }

    private HeartbeatCommandResult ExecuteResumeHeartbeat(HeartbeatCommand command, string executionId)
    {
        if (string.IsNullOrWhiteSpace(command.BeatName))
            return HeartbeatCommandResult.CreateFailure("Beat name is required");

        var result = _manager.ResumeHeartbeat(command.BeatName);
        if (!result)
            return HeartbeatCommandResult.CreateFailure($"Heartbeat '{command.BeatName}' not found");

        // Fire hook if available
        if (_hooks != null)
        {
            _ = _hooks.InvokeHeartbeatResumedAsync(command.BeatName, executionId);
        }

        return HeartbeatCommandResult.CreateSuccess($"Heartbeat '{command.BeatName}' resumed");
    }

    private HeartbeatCommandResult ExecuteUpdateHeartbeat(HeartbeatCommand command, string executionId)
    {
        if (string.IsNullOrWhiteSpace(command.BeatName))
            return HeartbeatCommandResult.CreateFailure("Beat name is required");

        var result = _manager.UpdateHeartbeat(
            command.BeatName,
            command.Task,
            command.IntervalSeconds,
            command.MaxMissed);

        if (!result)
            return HeartbeatCommandResult.CreateFailure($"Heartbeat '{command.BeatName}' not found");

        // Fire hook if available - using a generic approach since we don't have a specific update hook
        // Updates are represented as executed operations
        if (_hooks != null)
        {
            _ = _hooks.InvokeHeartbeatExecutedAsync(command.BeatName, result, DateTime.UtcNow.ToString("o"));
        }

        return HeartbeatCommandResult.CreateSuccess($"Heartbeat '{command.BeatName}' updated successfully");
    }

    private HeartbeatCommandResult ExecuteListHeartbeats(HeartbeatCommand command, string executionId)
    {
        var beats = _manager.GetHeartbeats();
        if (beats.Count == 0)
            return HeartbeatCommandResult.CreateSuccess("No heartbeats configured", new { count = 0, beats = Array.Empty<object>() });

        var beatList = beats.Values.Select(b => new
        {
            name = b.Name,
            task = b.Task,
            interval_seconds = b.IntervalSeconds,
            max_missed = b.MaxMissed,
            missed_count = b.MissedCount,
            status = b.IsPaused ? "paused" : "active",
            last_triggered = b.LastTriggered.ToString("o")
        }).ToList();

        return HeartbeatCommandResult.CreateSuccess(
            $"Listed {beats.Count} heartbeat(s)",
            new { count = beats.Count, beats = beatList });
    }

    private HeartbeatCommandResult ExecuteGetStatus(HeartbeatCommand command, string executionId)
    {
        if (string.IsNullOrWhiteSpace(command.BeatName))
            return HeartbeatCommandResult.CreateFailure("Beat name is required");

        var beat = _manager.GetHeartbeat(command.BeatName);
        if (beat == null)
            return HeartbeatCommandResult.CreateFailure($"Heartbeat '{command.BeatName}' not found");

        return HeartbeatCommandResult.CreateSuccess(
            $"Status of {command.BeatName}",
            new
            {
                name = beat.Name,
                task = beat.Task,
                interval_seconds = beat.IntervalSeconds,
                max_missed = beat.MaxMissed,
                missed_count = beat.MissedCount,
                status = beat.IsPaused ? "paused" : "active",
                last_triggered = beat.LastTriggered.ToString("o")
            });
    }

    private async Task<HeartbeatCommandResult> ExecuteTriggerNowAsync(HeartbeatCommand command, string executionId)
    {
        if (string.IsNullOrWhiteSpace(command.BeatName))
            return HeartbeatCommandResult.CreateFailure("Beat name is required");

        var beat = _manager.GetHeartbeat(command.BeatName);
        if (beat == null)
            return HeartbeatCommandResult.CreateFailure($"Heartbeat '{command.BeatName}' not found");

        // Fire hook for manual trigger
        if (_hooks != null)
        {
            _ = _hooks.InvokeHeartbeatExecutedAsync(command.BeatName, false, DateTime.UtcNow.ToString("o"));
        }

        // Note: Direct execution would need to be added to HeartbeatManager
        // For now, just reset the last triggered time to trigger on next cycle
        beat.LastTriggered = DateTime.UtcNow.AddSeconds(-beat.IntervalSeconds);

        return HeartbeatCommandResult.CreateSuccess($"Heartbeat '{command.BeatName}' scheduled for immediate execution");
    }

    private void OnHeartbeatTriggered(object? sender, HeartbeatEventArgs e)
    {
        _logger?.LogInformation($"[Heartbeat] ✓ {e.Name} triggered successfully");
        if (_hooks != null)
        {
            _ = _hooks.InvokeHeartbeatExecutedAsync(e.Name, e.Success, DateTime.UtcNow.ToString("o"));
        }
    }

    private void OnHeartbeatMissed(object? sender, HeartbeatMissedEventArgs e)
    {
        _logger?.LogWarning($"[Heartbeat] ✗ {e.Name} missed (count: {e.MissedCount}/{e.MaxMissed})");
        if (_hooks != null)
        {
            _ = _hooks.InvokeHeartbeatMissedAsync(e.Name, e.MissedCount, e.MaxMissed, e.Error ?? string.Empty);
        }
    }

    private void OnHeartbeatAdded(object? sender, HeartbeatAddedEventArgs e)
    {
        _logger?.LogInformation($"[Heartbeat] + {e.Name} added with interval {e.IntervalSeconds}s");
    }

    private void OnHeartbeatRemoved(object? sender, HeartbeatRemovedEventArgs e)
    {
        _logger?.LogInformation($"[Heartbeat] - {e.Name} removed");
    }

    private void OnHeartbeatStatusChanged(object? sender, HeartbeatStatusChangedEventArgs e)
    {
        _logger?.LogInformation($"[Heartbeat] {e.Name} status changed to: {e.NewStatus}");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_manager != null)
            {
                _manager.HeartbeatTriggered -= OnHeartbeatTriggered;
                _manager.HeartbeatMissed -= OnHeartbeatMissed;
                _manager.HeartbeatAdded -= OnHeartbeatAdded;
                _manager.HeartbeatRemoved -= OnHeartbeatRemoved;
                _manager.HeartbeatStatusChanged -= OnHeartbeatStatusChanged;
            }
            _disposed = true;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HeartbeatService));
    }
}

/// <summary>
/// Result of a heartbeat command execution
/// </summary>
public class HeartbeatCommandResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }
    public string ExecutionId { get; set; } = string.Empty;
    public long ExecutionTimeMs { get; set; }

    public static HeartbeatCommandResult CreateSuccess(string message, object? data = null)
    {
        return new HeartbeatCommandResult
        {
            Success = true,
            Message = message,
            Data = data
        };
    }

    public static HeartbeatCommandResult CreateFailure(string message)
    {
        return new HeartbeatCommandResult
        {
            Success = false,
            Message = message
        };
    }
}

/// <summary>
/// Event args for heartbeat command results
/// </summary>
public class HeartbeatCommandResultEventArgs : EventArgs
{
    public HeartbeatCommand Command { get; set; } = null!;
    public HeartbeatCommandResult Result { get; set; } = null!;
    public double ExecutionTime { get; set; }
}
