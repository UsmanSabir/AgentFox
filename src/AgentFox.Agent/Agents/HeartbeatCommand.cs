using System;

namespace AgentFox.Agents;

/// <summary>
/// Command for managing agent heartbeats
/// Compatible with OpenClaw heartbeat system
/// </summary>
public class HeartbeatCommand : ICommand
{
    /// <summary>
    /// Heartbeat operation types
    /// </summary>
    public enum HeartbeatOperation
    {
        Add,
        Remove,
        Pause,
        Resume,
        Update,
        List,
        GetStatus,
        TriggerNow
    }

    public string RunId { get; set; }
    public string SessionKey { get; set; }
    public CommandLane Lane { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Priority { get; set; }

    // Heartbeat-specific properties
    public HeartbeatOperation Operation { get; set; }
    public string BeatName { get; set; } = string.Empty;
    public string? Task { get; set; }
    public int? IntervalSeconds { get; set; }
    public int? MaxMissed { get; set; }
    
    /// <summary>
    /// Correlation ID for tracking heartbeat operations
    /// </summary>
    public string CorrelationId { get; set; }
    
    /// <summary>
    /// Optional source that triggered this command (e.g., "channel:slack:msg123")
    /// </summary>
    public string? SourceId { get; set; }

    public HeartbeatCommand()
    {
        RunId = Guid.NewGuid().ToString();
        SessionKey = $"heartbeat:{DateTime.UtcNow.Ticks}";
        Lane = CommandLane.Tool;
        CreatedAt = DateTime.UtcNow;
        Priority = 5;
        CorrelationId = Guid.NewGuid().ToString();
    }

    /// <summary>
    /// Create a command to add a new heartbeat
    /// </summary>
    public static HeartbeatCommand CreateAddHeartbeat(
        string beatName,
        string task,
        int intervalSeconds = 60,
        int maxMissed = 3,
        string? sourceId = null)
    {
        return new HeartbeatCommand
        {
            Operation = HeartbeatOperation.Add,
            BeatName = beatName,
            Task = task,
            IntervalSeconds = intervalSeconds,
            MaxMissed = maxMissed,
            SourceId = sourceId,
            Lane = CommandLane.Tool,
            Priority = 5
        };
    }

    /// <summary>
    /// Create a command to remove a heartbeat
    /// </summary>
    public static HeartbeatCommand CreateRemoveHeartbeat(string beatName, string? sourceId = null)
    {
        return new HeartbeatCommand
        {
            Operation = HeartbeatOperation.Remove,
            BeatName = beatName,
            SourceId = sourceId,
            Lane = CommandLane.Tool,
            Priority = 5
        };
    }

    /// <summary>
    /// Create a command to pause a heartbeat
    /// </summary>
    public static HeartbeatCommand CreatePauseHeartbeat(string beatName, string? sourceId = null)
    {
        return new HeartbeatCommand
        {
            Operation = HeartbeatOperation.Pause,
            BeatName = beatName,
            SourceId = sourceId,
            Lane = CommandLane.Tool,
            Priority = 7 // Higher priority for pause
        };
    }

    /// <summary>
    /// Create a command to resume a heartbeat
    /// </summary>
    public static HeartbeatCommand CreateResumeHeartbeat(string beatName, string? sourceId = null)
    {
        return new HeartbeatCommand
        {
            Operation = HeartbeatOperation.Resume,
            BeatName = beatName,
            SourceId = sourceId,
            Lane = CommandLane.Tool,
            Priority = 7 // Higher priority for resume
        };
    }

    /// <summary>
    /// Create a command to list all heartbeats
    /// </summary>
    public static HeartbeatCommand CreateListHeartbeats(string? sourceId = null)
    {
        return new HeartbeatCommand
        {
            Operation = HeartbeatOperation.List,
            SourceId = sourceId,
            Lane = CommandLane.Tool,
            Priority = 3 // Lower priority for read-only
        };
    }

    /// <summary>
    /// Create a command to get status of a specific heartbeat
    /// </summary>
    public static HeartbeatCommand CreateGetStatus(string beatName, string? sourceId = null)
    {
        return new HeartbeatCommand
        {
            Operation = HeartbeatOperation.GetStatus,
            BeatName = beatName,
            SourceId = sourceId,
            Lane = CommandLane.Tool,
            Priority = 3 // Lower priority for read-only
        };
    }

    /// <summary>
    /// Create a command to update a heartbeat
    /// </summary>
    public static HeartbeatCommand CreateUpdateHeartbeat(
        string beatName,
        string? newTask = null,
        int? newIntervalSeconds = null,
        int? newMaxMissed = null,
        string? sourceId = null)
    {
        return new HeartbeatCommand
        {
            Operation = HeartbeatOperation.Update,
            BeatName = beatName,
            Task = newTask,
            IntervalSeconds = newIntervalSeconds,
            MaxMissed = newMaxMissed,
            SourceId = sourceId,
            Lane = CommandLane.Tool,
            Priority = 5
        };
    }

    /// <summary>
    /// Create a command to trigger a heartbeat immediately
    /// </summary>
    public static HeartbeatCommand CreateTriggerNow(string beatName, string? sourceId = null)
    {
        return new HeartbeatCommand
        {
            Operation = HeartbeatOperation.TriggerNow,
            BeatName = beatName,
            SourceId = sourceId,
            Lane = CommandLane.Tool,
            Priority = 6
        };
    }

    public override string ToString()
    {
        var basInfo = $"[{Operation}] {BeatName}";
        return Operation switch
        {
            HeartbeatOperation.Add => $"{basInfo} interval={IntervalSeconds}s max_missed={MaxMissed}",
            HeartbeatOperation.Update => $"{basInfo} new_interval={IntervalSeconds}s new_max_missed={MaxMissed}",
            _ => basInfo
        };
    }
}
