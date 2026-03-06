using System.Timers;
using AgentFox.Agents;

namespace AgentFox.Runtime;

/// <summary>
/// Heartbeat manager for periodic agent health checks
/// </summary>
public class HeartbeatManager : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly Dictionary<string, HeartbeatConfig> _heartbeats = new();
    private readonly FoxAgent _agent;
    private bool _disposed;
    
    public event EventHandler<HeartbeatEventArgs>? HeartbeatTriggered;
    public event EventHandler<HeartbeatMissedEventArgs>? HeartbeatMissed;
    
    public HeartbeatManager(FoxAgent agent, int intervalSeconds = 60)
    {
        _agent = agent;
        _timer = new System.Timers.Timer(intervalSeconds * 1000);
        _timer.Elapsed += OnTimerElapsed;
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
            LastTriggered = DateTime.UtcNow
        };
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
    
    private async void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        foreach (var heartbeat in _heartbeats.Values)
        {
            var timeSinceLastTrigger = DateTime.UtcNow - heartbeat.LastTriggered;
            
            if (timeSinceLastTrigger.TotalSeconds > heartbeat.IntervalSeconds * heartbeat.MaxMissed)
            {
                // Heartbeat missed
                heartbeat.MissedCount++;
                HeartbeatMissed?.Invoke(this, new HeartbeatMissedEventArgs
                {
                    Name = heartbeat.Name,
                    MissedCount = heartbeat.MissedCount,
                    MaxMissed = heartbeat.MaxMissed
                });
                
                if (heartbeat.MissedCount >= heartbeat.MaxMissed)
                {
                    // Execute recovery action
                    await ExecuteHeartbeatAsync(heartbeat);
                }
            }
            else
            {
                // Normal heartbeat
                await ExecuteHeartbeatAsync(heartbeat);
            }
        }
    }
    
    private async Task ExecuteHeartbeatAsync(HeartbeatConfig config)
    {
        try
        {
            var result = await _agent.ExecuteAsync(config.Task);
            
            config.LastTriggered = DateTime.UtcNow;
            config.MissedCount = 0;
            
            HeartbeatTriggered?.Invoke(this, new HeartbeatEventArgs
            {
                Name = config.Name,
                Task = config.Task,
                Success = result.Success,
                Output = result.Output
            });
        }
        catch (Exception ex)
        {
            HeartbeatMissed?.Invoke(this, new HeartbeatMissedEventArgs
            {
                Name = config.Name,
                Error = ex.Message
            });
        }
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

public class HeartbeatConfig
{
    public string Name { get; set; } = string.Empty;
    public string Task { get; set; } = string.Empty;
    public int IntervalSeconds { get; set; }
    public int MaxMissed { get; set; }
    public int MissedCount { get; set; }
    public DateTime LastTriggered { get; set; }
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

/// <summary>
/// Cron job scheduler for periodic tasks
/// </summary>
public class CronScheduler : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly Dictionary<string, CronJob> _jobs = new();
    private readonly FoxAgent _agent;
    private bool _disposed;
    
    public event EventHandler<CronJobExecutedEventArgs>? JobExecuted;
    public event EventHandler<CronJobErrorEventArgs>? JobError;
    
    public CronScheduler(FoxAgent agent, int checkIntervalSeconds = 60)
    {
        _agent = agent;
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
                var result = await _agent.ExecuteAsync(job.Task);
                
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
