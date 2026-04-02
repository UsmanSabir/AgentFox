using System.Timers;
using System.Text;
using AgentFox.Agents;
using AgentFox.Models;
using AgentFox.Sessions;

namespace AgentFox.Runtime;

/// <summary>
/// Heartbeat manager for periodic agent health checks with heartbeat.md persistence
/// Compatible with OpenClaw event hooks system
/// </summary>
public class HeartbeatManager : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly Dictionary<string, HeartbeatConfig> _heartbeats = new();
    private readonly FoxAgent _agent;
    private readonly SessionManager? _sessionManager;
    private readonly string? _beatFilePath;
    private bool _disposed;

    public event EventHandler<HeartbeatEventArgs>? HeartbeatTriggered;
    public event EventHandler<HeartbeatMissedEventArgs>? HeartbeatMissed;
    public event EventHandler<HeartbeatAddedEventArgs>? HeartbeatAdded;
    public event EventHandler<HeartbeatRemovedEventArgs>? HeartbeatRemoved;
    public event EventHandler<HeartbeatStatusChangedEventArgs>? HeartbeatStatusChanged;

    public HeartbeatManager(FoxAgent agent, int intervalSeconds = 60, string? beatFilePath = null, SessionManager? sessionManager = null)
    {
        _agent = agent;
        _sessionManager = sessionManager;
        _beatFilePath = beatFilePath ?? Path.Combine(AppContext.BaseDirectory, "Runtime", "Heartbeat.md");
        _timer = new System.Timers.Timer(intervalSeconds * 1000);
        _timer.Elapsed += OnTimerElapsed;

        // Load existing heartbeats from file
        LoadHeartbeatsFromFile();
    }
    
    /// <summary>
    /// Add a heartbeat check
    /// </summary>
    public void AddHeartbeat(string name, string task, int intervalSeconds = 60, int maxMissed = 3)
    {
        _heartbeats[name] = new HeartbeatConfig
        {
            Name = name,
            Task = task,
            IntervalSeconds = intervalSeconds,
            MaxMissed = maxMissed,
            MissedCount = 0,
            LastTriggered = DateTime.UtcNow,
            IsPaused = false
        };
        
        HeartbeatAdded?.Invoke(this, new HeartbeatAddedEventArgs
        {
            Name = name,
            Task = task,
            IntervalSeconds = intervalSeconds
        });
        
        SaveHeartbeatsToFile();
    }
    
    /// <summary>
    /// Start heartbeat monitoring
    /// </summary>
    public void Start()
    {
        _timer.Start();
    }
    
    /// <summary>
    /// Stop heartbeat monitoring
    /// </summary>
    public void Stop()
    {
        _timer.Stop();
    }
    
    /// <summary>
    /// Remove a heartbeat
    /// </summary>
    public bool RemoveHeartbeat(string name)
    {
        if (_heartbeats.Remove(name))
        {
            HeartbeatRemoved?.Invoke(this, new HeartbeatRemovedEventArgs { Name = name });
            SaveHeartbeatsToFile();
            return true;
        }
        return false;
    }
    
    /// <summary>
    /// Pause a heartbeat
    /// </summary>
    public bool PauseHeartbeat(string name)
    {
        if (_heartbeats.TryGetValue(name, out var config))
        {
            config.IsPaused = true;
            HeartbeatStatusChanged?.Invoke(this, new HeartbeatStatusChangedEventArgs 
            { 
                Name = name, 
                NewStatus = "paused" 
            });
            SaveHeartbeatsToFile();
            return true;
        }
        return false;
    }
    
    /// <summary>
    /// Resume a heartbeat
    /// </summary>
    public bool ResumeHeartbeat(string name)
    {
        if (_heartbeats.TryGetValue(name, out var config))
        {
            config.IsPaused = false;
            config.LastTriggered = DateTime.UtcNow;
            HeartbeatStatusChanged?.Invoke(this, new HeartbeatStatusChangedEventArgs 
            { 
                Name = name, 
                NewStatus = "active" 
            });
            SaveHeartbeatsToFile();
            return true;
        }
        return false;
    }
    
    /// <summary>
    /// Get all heartbeats
    /// </summary>
    public IReadOnlyDictionary<string, HeartbeatConfig> GetHeartbeats() => _heartbeats;
    
    /// <summary>
    /// Get specific heartbeat status
    /// </summary>
    public HeartbeatConfig? GetHeartbeat(string name)
    {
        _heartbeats.TryGetValue(name, out var config);
        return config;
    }
    
    /// <summary>
    /// Update an existing heartbeat
    /// </summary>
    public bool UpdateHeartbeat(string name, string? newTask = null, int? newInterval = null, int? newMaxMissed = null)
    {
        if (!_heartbeats.TryGetValue(name, out var config))
            return false;
            
        if (newTask != null)
            config.Task = newTask;
        if (newInterval.HasValue)
            config.IntervalSeconds = newInterval.Value;
        if (newMaxMissed.HasValue)
            config.MaxMissed = newMaxMissed.Value;
            
        SaveHeartbeatsToFile();
        return true;
    }
    
    /// <summary>
    /// Load heartbeats from heartbeat.md file
    /// </summary>
    private void LoadHeartbeatsFromFile()
    {
        try
        {
            if (_beatFilePath == null || !File.Exists(_beatFilePath))
                return;
                
            var lines = File.ReadAllLines(_beatFilePath);
            var inTable = false;
            
            foreach (var line in lines)
            {
                // Skip header separators and non-data lines
                if (line.Contains("---|")) { inTable = true; continue; }
                if (!inTable || line.StartsWith("|") == false || line.Contains("Name") || line.Contains("(none")) continue;
                
                var parts = line.Split('|').Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToArray();
                if (parts.Length < 6) continue;
                
                if (int.TryParse(parts[2], out var interval) && int.TryParse(parts[3], out var maxMissed))
                {
                    _heartbeats[parts[0]] = new HeartbeatConfig
                    {
                        Name = parts[0],
                        Task = parts[1],
                        IntervalSeconds = interval,
                        MaxMissed = maxMissed,
                        MissedCount = 0,
                        LastTriggered = DateTime.UtcNow,
                        IsPaused = parts[4] == "paused"
                    };
                }
            }
        }
        catch (Exception ex)
        {
            // Log silently - file might not exist yet
            System.Diagnostics.Debug.WriteLine($"Could not load heartbeats: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Save heartbeats to heartbeat.md file
    /// </summary>
    private void SaveHeartbeatsToFile()
    {
        try
        {
            if (_beatFilePath == null)
                return;
                
            var directory = Path.GetDirectoryName(_beatFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            
            var sb = new StringBuilder();
            sb.AppendLine("# Agent Heartbeat Configuration");
            sb.AppendLine();
            sb.AppendLine("> 🫀 Heartbeat monitoring system for agent health tracking. Stores and manages periodic health checks.");
            sb.AppendLine();
            sb.AppendLine("## Active Heartbeats");
            sb.AppendLine();
            
            if (_heartbeats.Count == 0)
            {
                sb.AppendLine("| Name | Task | Interval (s) | Max Missed | Status | Last Check |");
                sb.AppendLine("|------|------|-------------|-----------|--------|------------|");
                sb.AppendLine("| (none configured) | - | - | - | - | - |");
            }
            else
            {
                sb.AppendLine("| Name | Task | Interval (s) | Max Missed | Status | Last Check |");
                sb.AppendLine("|------|------|-------------|-----------|--------|------------|");
                
                foreach (var beat in _heartbeats.Values)
                {
                    var status = beat.IsPaused ? "paused" : "active";
                    var lastCheck = beat.LastTriggered.ToString("g");
                    sb.AppendLine($"| {beat.Name} | {beat.Task} | {beat.IntervalSeconds} | {beat.MaxMissed} | {status} | {lastCheck} |");
                }
            }
            
            sb.AppendLine();
            sb.AppendLine("## Configuration Format");
            sb.AppendLine();
            sb.AppendLine("Each heartbeat entry includes:");
            sb.AppendLine("- **Name**: Unique identifier for the heartbeat");
            sb.AppendLine("- **Task**: Command or script to execute for health check");
            sb.AppendLine("- **Interval**: Seconds between checks");
            sb.AppendLine("- **MaxMissed**: Number of missed checks before alert");
            sb.AppendLine("- **Status**: current | paused");
            sb.AppendLine("- **LastCheck**: ISO 8601 timestamp");
            sb.AppendLine();
            
            File.WriteAllText(_beatFilePath, sb.ToString());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save heartbeats: {ex.Message}");
        }
    }
    
    private async void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        foreach (var heartbeat in _heartbeats.Values)
        {
            // Skip paused heartbeats
            if (heartbeat.IsPaused)
                continue;
                
            var timeSinceLastTrigger = DateTime.UtcNow - heartbeat.LastTriggered;
            
            if (timeSinceLastTrigger.TotalSeconds >= heartbeat.IntervalSeconds)
            {
                // Execute scheduled heartbeat
                await ExecuteHeartbeatAsync(heartbeat);
            }
        }
    }
    
    private async Task ExecuteHeartbeatAsync(HeartbeatConfig config)
    {
        try
        {
            // Each heartbeat run gets a fresh session so runs don't share context
            AgentResult result;
            if (_sessionManager != null)
            {
                var sessionId = _sessionManager.CreateFreshSession(
                    SessionOrigin.Heartbeat, config.Name, _agent.Id);
                result = await _agent.ProcessAsync(config.Task, sessionId);
            }
            else
            {
                result = await _agent.ExecuteAsync(config.Task);
            }
            
            config.LastTriggered = DateTime.UtcNow;
            config.MissedCount = 0;
            
            HeartbeatTriggered?.Invoke(this, new HeartbeatEventArgs
            {
                Name = config.Name,
                Task = config.Task,
                Success = result.Success,
                Output = result.Output
            });
            
            // Persist state to file
            SaveHeartbeatsToFile();
        }
        catch (Exception ex)
        {
            config.MissedCount++;
            HeartbeatMissed?.Invoke(this, new HeartbeatMissedEventArgs
            {
                Name = config.Name,
                MissedCount = config.MissedCount,
                MaxMissed = config.MaxMissed,
                Error = ex.Message
            });
            
            // Persist state to file
            SaveHeartbeatsToFile();
        }
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            _timer.Stop();
            _timer.Dispose();
            SaveHeartbeatsToFile();
            _disposed = true;
        }
    }
}

public class HeartbeatConfig
{
    public string Name { get; set; } = string.Empty;
    public string Task { get; set; } = string.Empty;
    public int IntervalSeconds { get; set; }
    public int MaxMissed { get; set; }
    public int MissedCount { get; set; }
    public DateTime LastTriggered { get; set; }
    public bool IsPaused { get; set; }
}

public class HeartbeatEventArgs : EventArgs
{
    public string Name { get; set; } = string.Empty;
    public string Task { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
}

public class HeartbeatMissedEventArgs : EventArgs
{
    public string Name { get; set; } = string.Empty;
    public int MissedCount { get; set; }
    public int MaxMissed { get; set; }
    public string? Error { get; set; }
}

public class HeartbeatAddedEventArgs : EventArgs
{
    public string Name { get; set; } = string.Empty;
    public string Task { get; set; } = string.Empty;
    public int IntervalSeconds { get; set; }
}

public class HeartbeatRemovedEventArgs : EventArgs
{
    public string Name { get; set; } = string.Empty;
}

public class HeartbeatStatusChangedEventArgs : EventArgs
{
    public string Name { get; set; } = string.Empty;
    public string NewStatus { get; set; } = string.Empty;
}

/// <summary>
/// Cron job scheduler for periodic tasks
/// </summary>
public class CronScheduler : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly Dictionary<string, CronJob> _jobs = new();
    private readonly FoxAgent _agent;
    private readonly SessionManager? _sessionManager;
    private bool _disposed;

    public event EventHandler<CronJobExecutedEventArgs>? JobExecuted;
    public event EventHandler<CronJobErrorEventArgs>? JobError;

    public CronScheduler(FoxAgent agent, int checkIntervalSeconds = 60, SessionManager? sessionManager = null)
    {
        _agent = agent;
        _sessionManager = sessionManager;
        _timer = new System.Timers.Timer(checkIntervalSeconds * 1000);
        _timer.Elapsed += OnTimerElapsed;
    }
    
    /// <summary>
    /// Add a cron job
    /// </summary>
    public void AddJob(string name, string cronExpression, string task)
    {
        _jobs[name] = new CronJob
        {
            Name = name,
            CronExpression = cronExpression,
            Task = task,
            LastExecuted = DateTime.MinValue,
            NextExecution = CalculateNextExecution(cronExpression)
        };
    }
    
    /// <summary>
    /// Add common cron jobs
    /// </summary>
    public void AddEveryMinute(string name, string task) => AddJob(name, "* * * * *", task);
    public void AddEveryHour(string name, string task) => AddJob(name, "0 * * * *", task);
    public void AddDaily(string name, string task, int hour = 0, int minute = 0) 
        => AddJob(name, $"{minute} {hour} * * *", task);
    public void AddWeekly(string name, string task, DayOfWeek day, int hour = 0, int minute = 0)
        => AddJob(name, $"{minute} {hour} * * {(int)day}", task);
    
    /// <summary>
    /// Start scheduler
    /// </summary>
    public void Start()
    {
        _timer.Start();
    }
    
    /// <summary>
    /// Stop scheduler
    /// </summary>
    public void Stop()
    {
        _timer.Stop();
    }
    
    private async void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        var now = DateTime.UtcNow;
        
        foreach (var job in _jobs.Values.Where(j => j.NextExecution <= now))
        {
            try
            {
                // Each cron run gets a fresh session so jobs don't share context
                AgentResult result;
                if (_sessionManager != null)
                {
                    var sessionId = _sessionManager.CreateFreshSession(
                        SessionOrigin.CronJob, job.Name, _agent.Id);
                    result = await _agent.ProcessAsync(job.Task, sessionId);
                }
                else
                {
                    result = await _agent.ExecuteAsync(job.Task);
                }

                job.LastExecuted = now;
                job.NextExecution = CalculateNextExecution(job.CronExpression);
                
                JobExecuted?.Invoke(this, new CronJobExecutedEventArgs
                {
                    Name = job.Name,
                    Task = job.Task,
                    Success = result.Success,
                    Output = result.Output
                });
            }
            catch (Exception ex)
            {
                JobError?.Invoke(this, new CronJobErrorEventArgs
                {
                    Name = job.Name,
                    Error = ex.Message
                });
            }
        }
    }
    
    private DateTime CalculateNextExecution(string cronExpression)
    {
        // Simple cron parser - for production, use a proper cron library
        var parts = cronExpression.Split(' ');
        if (parts.Length != 5) return DateTime.UtcNow.AddMinutes(1);
        
        var now = DateTime.UtcNow;
        
        try
        {
            // Very basic cron interpretation
            if (parts[0] == "*" && parts[1] == "*" && parts[2] == "*" && parts[3] == "*" && parts[4] == "*")
                return now.AddMinutes(1); // Every minute
            
            if (parts[0] != "*" && parts[1] == "*" && parts[2] == "*" && parts[3] == "*" && parts[4] == "*")
            {
                var minute = int.Parse(parts[0]);
                var next = now.Date.AddHours(now.Hour).AddMinutes(minute);
                if (next <= now) next = next.AddHours(1);
                return next;
            }
        }
        catch { }
        
        return now.AddMinutes(1);
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            _timer.Stop();
            _timer.Dispose();
            _disposed = true;
        }
    }
}

public class CronJob
{
    public string Name { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public string Task { get; set; } = string.Empty;
    public DateTime LastExecuted { get; set; }
    public DateTime NextExecution { get; set; }
}

public class CronJobExecutedEventArgs : EventArgs
{
    public string Name { get; set; } = string.Empty;
    public string Task { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
}

public class CronJobErrorEventArgs : EventArgs
{
    public string Name { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
