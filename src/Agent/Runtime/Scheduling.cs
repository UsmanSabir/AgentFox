using System.Timers;
using System.Text;
using Cronos;
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
    // Case-insensitive so "PSX-Check" and "psx-check" cannot both exist as separate beats.
    private readonly Dictionary<string, HeartbeatConfig> _heartbeats = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _beatsLock = new();
    private readonly FoxAgent _agent;
    private readonly SessionManager? _sessionManager;
    private readonly ICommandQueue? _commandQueue;
    private readonly string? _beatFilePath;
    private bool _disposed;

    public event EventHandler<HeartbeatEventArgs>? HeartbeatTriggered;
    public event EventHandler<HeartbeatMissedEventArgs>? HeartbeatMissed;
    public event EventHandler<HeartbeatAddedEventArgs>? HeartbeatAdded;
    public event EventHandler<HeartbeatRemovedEventArgs>? HeartbeatRemoved;
    public event EventHandler<HeartbeatStatusChangedEventArgs>? HeartbeatStatusChanged;

    public HeartbeatManager(
        FoxAgent agent,
        int intervalSeconds = 60,
        string? beatFilePath = null,
        SessionManager? sessionManager = null,
        ICommandQueue? commandQueue = null)
    {
        _agent = agent;
        _sessionManager = sessionManager;
        _commandQueue = commandQueue;
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
        lock (_beatsLock)
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
        }

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
        bool removed;
        lock (_beatsLock)
        {
            removed = _heartbeats.Remove(name);
        }

        if (removed)
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
        lock (_beatsLock)
        {
            if (!_heartbeats.TryGetValue(name, out var config)) return false;
            config.IsPaused = true;
        }

        HeartbeatStatusChanged?.Invoke(this, new HeartbeatStatusChangedEventArgs
        {
            Name = name,
            NewStatus = "paused"
        });
        SaveHeartbeatsToFile();
        return true;
    }
    
    /// <summary>
    /// Resume a heartbeat
    /// </summary>
    public bool ResumeHeartbeat(string name)
    {
        lock (_beatsLock)
        {
            if (!_heartbeats.TryGetValue(name, out var config)) return false;
            config.IsPaused = false;
            config.LastTriggered = DateTime.UtcNow;
        }

        HeartbeatStatusChanged?.Invoke(this, new HeartbeatStatusChangedEventArgs
        {
            Name = name,
            NewStatus = "active"
        });
        SaveHeartbeatsToFile();
        return true;
    }
    
    /// <summary>
    /// Get all heartbeats
    /// </summary>
    public IReadOnlyDictionary<string, HeartbeatConfig> GetHeartbeats()
    {
        lock (_beatsLock)
        {
            return new Dictionary<string, HeartbeatConfig>(_heartbeats, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Get specific heartbeat status
    /// </summary>
    public HeartbeatConfig? GetHeartbeat(string name)
    {
        lock (_beatsLock)
        {
            _heartbeats.TryGetValue(name, out var config);
            return config;
        }
    }
    
    /// <summary>
    /// Update an existing heartbeat
    /// </summary>
    public bool UpdateHeartbeat(string name, string? newTask = null, int? newInterval = null, int? newMaxMissed = null)
    {
        lock (_beatsLock)
        {
            if (!_heartbeats.TryGetValue(name, out var config))
                return false;

            if (newTask != null)
                config.Task = newTask;
            if (newInterval.HasValue)
                config.IntervalSeconds = newInterval.Value;
            if (newMaxMissed.HasValue)
                config.MaxMissed = newMaxMissed.Value;
        }

        SaveHeartbeatsToFile();
        return true;
    }
    
    /// <summary>
    /// Back-dates a beat so the next timer tick fires it immediately. Returns false if the beat
    /// does not exist, or null if it is already mid-run — in which case forcing another run would
    /// be exactly the concurrent-duplicate behaviour the claim guard exists to prevent.
    /// </summary>
    public bool? TriggerHeartbeat(string name)
    {
        lock (_beatsLock)
        {
            if (!_heartbeats.TryGetValue(name, out var config)) return false;
            if (config.IsRunning) return null;

            config.LastTriggered = DateTime.UtcNow.AddSeconds(-config.IntervalSeconds);
            return true;
        }
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
                    lock (_beatsLock)
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
            
            List<HeartbeatConfig> beatsSnapshot;
            lock (_beatsLock)
            {
                beatsSnapshot = _heartbeats.Values.ToList();
            }

            var sb = new StringBuilder();
            sb.AppendLine("# Agent Heartbeat Configuration");
            sb.AppendLine();
            sb.AppendLine("> 🫀 Heartbeat monitoring system for agent health tracking. Stores and manages periodic health checks.");
            sb.AppendLine();
            sb.AppendLine("## Active Heartbeats");
            sb.AppendLine();
            
            if (beatsSnapshot.Count == 0)
            {
                sb.AppendLine("| Name | Task | Interval (s) | Max Missed | Status | Last Check |");
                sb.AppendLine("|------|------|-------------|-----------|--------|------------|");
                sb.AppendLine("| (none configured) | - | - | - | - | - |");
            }
            else
            {
                sb.AppendLine("| Name | Task | Interval (s) | Max Missed | Status | Last Check |");
                sb.AppendLine("|------|------|-------------|-----------|--------|------------|");

                foreach (var beat in beatsSnapshot)
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
    
    private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        var now = DateTime.UtcNow;

        // Claim every due beat *before* running any of them: mark it in-flight and advance
        // LastTriggered inside the same lock. A beat's task is a full agent turn and can take
        // far longer than the timer interval; if the claim happened after the await, every
        // subsequent tick would still see the beat as due and launch another concurrent run.
        List<HeartbeatConfig> dueBeats;
        lock (_beatsLock)
        {
            dueBeats = _heartbeats.Values
                .Where(b => !b.IsPaused
                            && !b.IsRunning
                            && (now - b.LastTriggered).TotalSeconds >= b.IntervalSeconds)
                .ToList();

            foreach (var beat in dueBeats)
            {
                beat.IsRunning = true;
                beat.LastTriggered = now;
            }
        }

        if (dueBeats.Count == 0)
            return;

        SaveHeartbeatsToFile();

        // Dispatch each beat independently so one slow or wedged task cannot starve the others.
        // A wedged beat keeps IsRunning set and is simply never re-fired, which is the safe
        // failure: silence rather than a run per tick.
        foreach (var beat in dueBeats)
            _ = ExecuteHeartbeatAsync(beat);
    }

    private async Task ExecuteHeartbeatAsync(HeartbeatConfig config)
    {
        try
        {
            // Each heartbeat run gets a fresh session so runs don't share context
            var sessionId = _sessionManager?.CreateFreshSession(
                SessionOrigin.Heartbeat, config.Name, _agent.Id)
                ?? Guid.NewGuid().ToString("N");

            AgentResult result;
            if (_commandQueue != null)
            {
                var tcs = new TaskCompletionSource<AgentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cmd = new AgentCommand
                {
                    SessionKey = sessionId,
                    AgentId = _agent.Id,
                    Lane = CommandLane.Background,
                    Message = config.Task,
                    ResultSource = tcs
                };
                _commandQueue.Enqueue(cmd);
                result = await tcs.Task;
            }
            else
            {
                result = await _agent.ProcessAsync(config.Task, sessionId);
            }
            
            // LastTriggered was already stamped when this beat was claimed — re-stamping here
            // would push the next beat out by however long the task took to run.
            lock (_beatsLock)
            {
                config.MissedCount = 0;
            }

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
            int missed;
            lock (_beatsLock)
            {
                missed = ++config.MissedCount;
            }

            HeartbeatMissed?.Invoke(this, new HeartbeatMissedEventArgs
            {
                Name = config.Name,
                MissedCount = missed,
                MaxMissed = config.MaxMissed,
                Error = ex.Message
            });
        }
        finally
        {
            // Release the claim so the next due tick can fire this beat again.
            lock (_beatsLock)
            {
                config.IsRunning = false;
            }
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

    /// <summary>
    /// True while a beat's task is executing. Transient (never persisted) — it exists so the
    /// scheduler tick does not launch a second run of a beat that is still in flight.
    /// </summary>
    public bool IsRunning { get; set; }
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
    // Case-insensitive so "PSX-Daily-Summary" and "psx-daily-summary" cannot both exist as
    // separate jobs, each firing its own copy of the same report.
    private readonly Dictionary<string, CronJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _jobsLock = new();
    private readonly FoxAgent _agent;
    private readonly SessionManager? _sessionManager;
    private readonly ICommandQueue? _commandQueue;
    private readonly string? _jobsFilePath;
    private bool _disposed;

    public event EventHandler<CronJobExecutedEventArgs>? JobExecuted;
    public event EventHandler<CronJobErrorEventArgs>? JobError;

    public CronScheduler(
        FoxAgent agent,
        int checkIntervalSeconds = 60,
        string? jobsFilePath = null,
        SessionManager? sessionManager = null,
        ICommandQueue? commandQueue = null)
    {
        _agent = agent;
        _sessionManager = sessionManager;
        _commandQueue = commandQueue;
        _jobsFilePath = jobsFilePath;
        _timer = new System.Timers.Timer(checkIntervalSeconds * 1000);
        _timer.Elapsed += OnTimerElapsed;

        LoadJobsFromFile();
    }
    
    /// <summary>
    /// Add a cron job
    /// </summary>
    public void AddJob(string name, string cronExpression, string task)
    {
        lock (_jobsLock)
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
        SaveJobsToFile();
    }

    /// <summary>
    /// Update an existing cron job's schedule and/or task in place, preserving
    /// its run history (LastExecuted). Returns false if the job doesn't exist.
    /// </summary>
    public bool UpdateJob(string name, string cronExpression, string task)
    {
        lock (_jobsLock)
        {
            if (!_jobs.TryGetValue(name, out var job))
                return false;

            job.CronExpression = cronExpression;
            job.Task = task;
            job.NextExecution = CalculateNextExecution(cronExpression);
        }
        SaveJobsToFile();
        return true;
    }

    /// <summary>
    /// Remove a cron job by name. Returns false if not found.
    /// </summary>
    public bool RemoveJob(string name)
    {
        bool removed;
        lock (_jobsLock)
        {
            removed = _jobs.Remove(name);
        }
        if (removed) SaveJobsToFile();
        return removed;
    }

    /// <summary>
    /// Get a single job by name, or null if not found.
    /// </summary>
    public CronJob? GetJob(string name)
    {
        lock (_jobsLock)
        {
            _jobs.TryGetValue(name, out var job);
            return job;
        }
    }

    /// <summary>
    /// Get all registered cron jobs.
    /// </summary>
    public IReadOnlyDictionary<string, CronJob> GetJobs()
    {
        lock (_jobsLock)
        {
            return new Dictionary<string, CronJob>(_jobs, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Finds an existing job whose name differs from <paramref name="name"/> only by case,
    /// separators or whitespace — e.g. "PSX Daily Summary" against "psx-daily-summary".
    /// Used to stop a caller that hit a name collision from simply inventing a new name and
    /// ending up with two jobs delivering the same thing.
    /// </summary>
    public CronJob? FindSimilarJob(string name)
    {
        var needle = NormalizeName(name);
        if (needle.Length == 0) return null;

        lock (_jobsLock)
        {
            return _jobs.Values.FirstOrDefault(j => NormalizeName(j.Name) == needle);
        }
    }

    /// <summary>Strips case, whitespace and separator characters for fuzzy name comparison.</summary>
    private static string NormalizeName(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// Selects the jobs that are due and claims them in the same step: each is marked in-flight
    /// and its schedule advanced before it runs. Callers must hold the jobs lock.
    ///
    /// Claiming up front is the whole point. A job's task is a full agent turn and routinely runs
    /// for minutes, while the timer keeps ticking every check interval. If the schedule were
    /// advanced only after the run finished, every tick in between would still see the job as due
    /// and start another complete, independent run of it.
    /// </summary>
    internal static List<CronJob> ClaimDueJobs(
        IEnumerable<CronJob> jobs,
        DateTime now,
        Func<string, DateTime> nextOccurrence)
    {
        var due = jobs
            .Where(j => !j.IsRunning && j.NextExecution <= now)
            .ToList();

        foreach (var job in due)
        {
            job.IsRunning = true;
            job.LastExecuted = now;
            job.NextExecution = nextOccurrence(job.CronExpression);
        }

        return due;
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
    
    private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        var now = DateTime.UtcNow;

        // Claim every due job *before* running any of them: mark it in-flight and advance its
        // schedule inside the same lock.
        //
        // A job's task is a full agent turn — web searches, sub-agents, channel sends — and
        // routinely runs for minutes. The timer keeps ticking every checkIntervalSeconds (60 by
        // default) throughout. When the schedule was advanced *after* the await, every tick in
        // between still saw NextExecution <= now and launched another complete, independent run
        // of the same job: a "daily" summary fired once a minute until the first run happened to
        // finish, each copy doing its own research and its own delivery to the user's channels.
        List<CronJob> dueJobs;
        lock (_jobsLock)
        {
            dueJobs = ClaimDueJobs(_jobs.Values, now, CalculateNextExecution);
        }

        if (dueJobs.Count == 0)
            return;

        SaveJobsToFile();

        // Dispatch each job independently so one slow job cannot delay the others. A job that
        // wedges keeps IsRunning set and is never re-fired — silence, rather than a run per tick.
        foreach (var job in dueJobs)
            _ = ExecuteJobAsync(job);
    }

    private async Task ExecuteJobAsync(CronJob job)
    {
        var startedAt = DateTime.UtcNow;
        try
        {
            // Each cron run gets a fresh session so jobs don't share context
            var sessionId = _sessionManager?.CreateFreshSession(
                SessionOrigin.CronJob, job.Name, _agent.Id)
                ?? Guid.NewGuid().ToString("N");

            AgentResult result;
            if (_commandQueue != null)
            {
                var tcs = new TaskCompletionSource<AgentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cmd = new AgentCommand
                {
                    SessionKey = sessionId,
                    AgentId = _agent.Id,
                    Lane = CommandLane.Background,
                    Message = job.Task,
                    ResultSource = tcs
                };
                _commandQueue.Enqueue(cmd);
                result = await tcs.Task;
            }
            else
            {
                result = await _agent.ProcessAsync(job.Task, sessionId);
            }

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
        finally
        {
            DateTime nextExecution;
            lock (_jobsLock)
            {
                // Release the claim. The schedule was already advanced at claim time, so a run
                // that overran its own interval does not immediately re-fire.
                job.IsRunning = false;
                nextExecution = job.NextExecution;
            }

            var elapsed = DateTime.UtcNow - startedAt;
            if (nextExecution <= DateTime.UtcNow)
                System.Diagnostics.Debug.WriteLine(
                    $"Cron job '{job.Name}' took {elapsed.TotalMinutes:F1} min — longer than its own " +
                    $"schedule allows. It will run again on the next tick.");

            SaveJobsToFile();
        }
    }
    
    private DateTime CalculateNextExecution(string cronExpression)
    {
        // Standard 5-field cron (minute hour day-of-month month day-of-week),
        // evaluated in UTC. Cron jobs are scheduled against DateTime.UtcNow, so
        // e.g. "0 6 * * 1-5" fires at 06:00 UTC (11:00 PKT) on weekdays.
        var now = DateTime.UtcNow;
        try
        {
            var expr = CronExpression.Parse(cronExpression.Trim());
            var next = expr.GetNextOccurrence(now, TimeZoneInfo.Utc);
            // No future occurrence (unreachable schedule) — back off a day rather
            // than hammering the check every tick.
            return next ?? now.AddDays(1);
        }
        catch
        {
            // Malformed expression — back off an hour instead of re-firing every
            // minute (the previous fallback caused runaway execution).
            return now.AddHours(1);
        }
    }
    
    // ── Persistence ──────────────────────────────────────────────────────────

    /// <summary>
    /// Makes a value safe to store in a single markdown table cell. Model-authored task strings
    /// routinely contain newlines and pipes; written raw they broke the table, and the reader then
    /// silently truncated the task at the first newline or invented bogus jobs from its later lines.
    /// </summary>
    private static string EscapeCell(string value) =>
        value.Replace("\\", "\\\\")
             .Replace("|", "\\|")
             .Replace("\r\n", "\\n")
             .Replace("\n", "\\n")
             .Replace("\r", "\\n");

    private static string UnescapeCell(string value)
    {
        if (!value.Contains('\\')) return value;

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }

            var next = value[++i];
            sb.Append(next switch
            {
                'n'  => '\n',
                '\\' => '\\',
                '|'  => '|',
                _    => next
            });
        }
        return sb.ToString();
    }

    /// <summary>
    /// Splits a markdown table row on unescaped pipes. Unlike a plain Split('|') that discards
    /// empty entries, this preserves empty interior cells — dropping them shifted every later
    /// column onto the wrong field.
    /// </summary>
    private static string[] SplitRow(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length)
            {
                current.Append(c).Append(line[++i]);   // keep the escape for UnescapeCell
                continue;
            }
            if (c == '|')
            {
                cells.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        cells.Add(current.ToString());

        // Leading and trailing pipes yield empty sentinel cells.
        if (cells.Count > 0 && cells[0].Trim().Length == 0) cells.RemoveAt(0);
        if (cells.Count > 0 && cells[^1].Trim().Length == 0) cells.RemoveAt(cells.Count - 1);

        return cells.Select(c => c.Trim()).ToArray();
    }

    private static string FormatStamp(DateTime value) =>
        value == DateTime.MinValue ? "never" : value.ToString("o");

    private static DateTime ParseStamp(string value)
    {
        // RoundtripKind cannot be combined with AdjustToUniversal, so normalize afterwards.
        if (!DateTime.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed))
            return DateTime.MinValue;

        return parsed.Kind switch
        {
            DateTimeKind.Utc   => parsed,
            DateTimeKind.Local => parsed.ToUniversalTime(),
            // A hand-edited stamp with no offset: the column is documented as UTC, so take it
            // at face value rather than reinterpreting it in the machine's local zone.
            _ => DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
        };
    }

    /// <summary>Renders the jobs file. Pure, so the round-trip can be tested directly.</summary>
    internal static string SerializeJobs(IEnumerable<CronJob> jobs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Cron Schedule");
        sb.AppendLine();
        sb.AppendLine("> Scheduled cron jobs managed by AgentFox. Edit with care — task strings are executed by the agent.");
        sb.AppendLine();
        sb.AppendLine("## Jobs");
        sb.AppendLine();
        sb.AppendLine("| Name | Cron | Task | Last Run | Next Run |");
        sb.AppendLine("|------|------|------|----------|----------|");

        var any = false;
        foreach (var job in jobs)
        {
            any = true;
            sb.AppendLine(
                $"| {EscapeCell(job.Name)} " +
                $"| {EscapeCell(job.CronExpression)} " +
                $"| {EscapeCell(job.Task)} " +
                $"| {FormatStamp(job.LastExecuted)} " +
                $"| {FormatStamp(job.NextExecution)} |");
        }

        if (!any)
            sb.AppendLine("| (none configured) | - | - | - | - |");

        sb.AppendLine();
        sb.AppendLine("Task cells are escaped: `\\n` is a line break, `\\|` a literal pipe, `\\\\` a backslash.");
        sb.AppendLine("`Last Run` and `Next Run` are UTC and maintained by the scheduler — edit the");
        sb.AppendLine("first three columns only.");

        return sb.ToString();
    }

    /// <summary>
    /// Parses the jobs file. A persisted future occurrence is honoured so a restart cannot re-run
    /// an occurrence that already fired; a stamp in the past means the process was down when the
    /// job was due, and that occurrence is skipped rather than fired immediately on startup.
    /// </summary>
    internal static List<CronJob> DeserializeJobs(
        IEnumerable<string> lines,
        DateTime now,
        Func<string, DateTime> nextOccurrence)
    {
        var jobs = new List<CronJob>();
        var inTable = false;

        foreach (var line in lines)
        {
            if (line.Contains("---|")) { inTable = true; continue; }
            if (!inTable || !line.StartsWith("|")) continue;

            var cells = SplitRow(line);
            if (cells.Length < 3) continue;

            // Match on the first cell only. Testing the whole line meant any job whose task text
            // happened to mention "Name" was silently dropped on load.
            if (cells[0] is "Name" || cells[0].StartsWith("(none", StringComparison.Ordinal))
                continue;

            var name = UnescapeCell(cells[0]);
            var cron = UnescapeCell(cells[1]);
            var task = UnescapeCell(cells[2]);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(cron)) continue;

            var lastExecuted = cells.Length > 3 ? ParseStamp(cells[3]) : DateTime.MinValue;
            var persistedNext = cells.Length > 4 ? ParseStamp(cells[4]) : DateTime.MinValue;

            jobs.Add(new CronJob
            {
                Name = name,
                CronExpression = cron,
                Task = task,
                LastExecuted = lastExecuted,
                NextExecution = persistedNext > now ? persistedNext : nextOccurrence(cron)
            });
        }

        return jobs;
    }

    private void SaveJobsToFile()
    {
        try
        {
            if (_jobsFilePath == null) return;

            var dir = Path.GetDirectoryName(_jobsFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            List<CronJob> jobsSnapshot;
            lock (_jobsLock)
            {
                jobsSnapshot = _jobs.Values.ToList();
            }

            File.WriteAllText(_jobsFilePath, SerializeJobs(jobsSnapshot));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save cron jobs: {ex.Message}");
        }
    }

    private void LoadJobsFromFile()
    {
        try
        {
            if (_jobsFilePath == null || !File.Exists(_jobsFilePath)) return;

            var jobs = DeserializeJobs(
                File.ReadAllLines(_jobsFilePath), DateTime.UtcNow, CalculateNextExecution);

            lock (_jobsLock)
            {
                foreach (var job in jobs)
                    _jobs[job.Name] = job;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not load cron jobs: {ex.Message}");
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

public class CronJob
{
    public string Name { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public string Task { get; set; } = string.Empty;
    public DateTime LastExecuted { get; set; }
    public DateTime NextExecution { get; set; }

    /// <summary>
    /// True while this job's task is executing. Transient (never persisted) — it exists so the
    /// scheduler tick does not launch a second run of a job that is still in flight.
    /// </summary>
    public bool IsRunning { get; set; }
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
