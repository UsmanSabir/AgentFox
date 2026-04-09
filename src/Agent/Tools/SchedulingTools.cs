using AgentFox.Plugins.Interfaces;
using AgentFox.Runtime;

namespace AgentFox.Tools;

/// <summary>
/// Exposes HeartbeatManager to the agent as a tool.
/// Heartbeats are named health-check tasks that run on a fixed interval and track missed executions.
/// Use this for "is X still alive?" monitoring patterns.
/// </summary>
public class ManageHeartbeatTool : BaseTool
{
    private readonly Func<HeartbeatManager> _getManager;

    /// <param name="getManager">Lazy resolver — called at invocation time, not construction time.</param>
    public ManageHeartbeatTool(Func<HeartbeatManager> getManager)
    {
        _getManager = getManager;
    }

    public override string Name => "manage_heartbeat";

    public override string Description =>
        "Manage named heartbeats — periodic health-check tasks that run on a fixed interval and " +
        "track consecutive missed executions. Ideal for monitoring (\"is X still working?\"). " +
        "Operations: add, remove, pause, resume, update, list, status, trigger.";

    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["operation"] = new()
        {
            Type = "string",
            Description = "add | remove | pause | resume | update | list | status | trigger",
            Required = true,
            EnumValues = ["add", "remove", "pause", "resume", "update", "list", "status", "trigger"]
        },
        ["name"] = new()
        {
            Type = "string",
            Description = "Heartbeat name — required for every operation except 'list'",
            Required = false
        },
        ["task"] = new()
        {
            Type = "string",
            Description = "Agent task to execute on each beat (required for 'add'; optional for 'update')",
            Required = false
        },
        ["interval_seconds"] = new()
        {
            Type = "integer",
            Description = "Seconds between beats, minimum 10 (default: 60)",
            Required = false,
            Default = 60
        },
        ["max_missed"] = new()
        {
            Type = "integer",
            Description = "Consecutive missed beats before raising an alert (default: 3)",
            Required = false,
            Default = 3
        }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var operation = arguments.GetValueOrDefault("operation")?.ToString()?.ToLowerInvariant();
        var name      = arguments.GetValueOrDefault("name")?.ToString();
        var manager   = _getManager();

        return operation switch
        {
            "add"     => Task.FromResult(Add(manager, name, arguments)),
            "remove"  => Task.FromResult(Remove(manager, name)),
            "pause"   => Task.FromResult(Pause(manager, name)),
            "resume"  => Task.FromResult(Resume(manager, name)),
            "update"  => Task.FromResult(Update(manager, name, arguments)),
            "list"    => Task.FromResult(List(manager)),
            "status"  => Task.FromResult(Status(manager, name)),
            "trigger" => Task.FromResult(Trigger(manager, name)),
            _ => Task.FromResult(ToolResult.Fail(
                $"Unknown operation '{operation}'. Valid values: add, remove, pause, resume, update, list, status, trigger"))
        };
    }

    private static ToolResult Add(HeartbeatManager manager, string? name, Dictionary<string, object?> args)
    {
        if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("'name' is required for add");
        var task = args.GetValueOrDefault("task")?.ToString();
        if (string.IsNullOrWhiteSpace(task)) return ToolResult.Fail("'task' is required for add");
        if (manager.GetHeartbeat(name) != null)
            return ToolResult.Fail($"Heartbeat '{name}' already exists. Use 'update' to modify it.");

        var interval  = Math.Max(10, ParseInt(args.GetValueOrDefault("interval_seconds"), 60));
        var maxMissed = Math.Max(1,  ParseInt(args.GetValueOrDefault("max_missed"), 3));

        manager.AddHeartbeat(name, task, interval, maxMissed);
        return ToolResult.Ok(
            $"Heartbeat '{name}' added.\n" +
            $"  Task: {task}\n" +
            $"  Interval: every {interval}s\n" +
            $"  Alert after: {maxMissed} missed beat(s)");
    }

    private static ToolResult Remove(HeartbeatManager manager, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("'name' is required for remove");
        return manager.RemoveHeartbeat(name)
            ? ToolResult.Ok($"Heartbeat '{name}' removed.")
            : ToolResult.Fail($"Heartbeat '{name}' not found.");
    }

    private static ToolResult Pause(HeartbeatManager manager, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("'name' is required for pause");
        return manager.PauseHeartbeat(name)
            ? ToolResult.Ok($"Heartbeat '{name}' paused.")
            : ToolResult.Fail($"Heartbeat '{name}' not found.");
    }

    private static ToolResult Resume(HeartbeatManager manager, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("'name' is required for resume");
        return manager.ResumeHeartbeat(name)
            ? ToolResult.Ok($"Heartbeat '{name}' resumed.")
            : ToolResult.Fail($"Heartbeat '{name}' not found.");
    }

    private static ToolResult Update(HeartbeatManager manager, string? name, Dictionary<string, object?> args)
    {
        if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("'name' is required for update");
        var newTask      = args.GetValueOrDefault("task")?.ToString();
        var newInterval  = args.ContainsKey("interval_seconds") ? (int?)Math.Max(10, ParseInt(args["interval_seconds"], 60)) : null;
        var newMaxMissed = args.ContainsKey("max_missed")       ? (int?)Math.Max(1,  ParseInt(args["max_missed"], 3)) : null;

        return manager.UpdateHeartbeat(name, newTask, newInterval, newMaxMissed)
            ? ToolResult.Ok($"Heartbeat '{name}' updated.")
            : ToolResult.Fail($"Heartbeat '{name}' not found.");
    }

    private static ToolResult List(HeartbeatManager manager)
    {
        var beats = manager.GetHeartbeats();
        if (beats.Count == 0)
            return ToolResult.Ok("No heartbeats configured.");

        var lines = beats.Values.Select(b =>
            $"• {b.Name} [{(b.IsPaused ? "paused" : "active")}]\n" +
            $"  Task: {b.Task}\n" +
            $"  Interval: {b.IntervalSeconds}s | Max missed: {b.MaxMissed} | Missed so far: {b.MissedCount}\n" +
            $"  Last triggered: {(b.LastTriggered == default ? "never" : b.LastTriggered.ToString("o"))}");

        return ToolResult.Ok($"{beats.Count} heartbeat(s):\n\n{string.Join("\n\n", lines)}");
    }

    private static ToolResult Status(HeartbeatManager manager, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("'name' is required for status");
        var b = manager.GetHeartbeat(name);
        if (b == null) return ToolResult.Fail($"Heartbeat '{name}' not found.");

        return ToolResult.Ok(
            $"Heartbeat '{b.Name}'\n" +
            $"  Status:       {(b.IsPaused ? "paused" : "active")}\n" +
            $"  Task:         {b.Task}\n" +
            $"  Interval:     {b.IntervalSeconds}s\n" +
            $"  Max missed:   {b.MaxMissed}\n" +
            $"  Missed count: {b.MissedCount}\n" +
            $"  Last run:     {(b.LastTriggered == default ? "never" : b.LastTriggered.ToString("o"))}\n" +
            $"  Next run:     {b.LastTriggered.AddSeconds(b.IntervalSeconds):o}");
    }

    private static ToolResult Trigger(HeartbeatManager manager, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("'name' is required for trigger");
        var b = manager.GetHeartbeat(name);
        if (b == null) return ToolResult.Fail($"Heartbeat '{name}' not found.");

        // Back-date LastTriggered so the next timer tick fires this beat immediately.
        b.LastTriggered = DateTime.UtcNow.AddSeconds(-b.IntervalSeconds);
        return ToolResult.Ok($"Heartbeat '{name}' will fire on the next timer tick.");
    }

    private static int ParseInt(object? value, int fallback) => value switch
    {
        int i    => i,
        long l   => (int)l,
        double d => (int)d,
        _ => int.TryParse(value?.ToString(), out var p) ? p : fallback
    };
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Exposes CronScheduler to the agent as a tool.
/// Cron jobs fire on a standard 5-field cron expression and have no failure tracking.
/// Use this for "run X at time Y" scheduling patterns.
/// </summary>
public class ManageCronTool : BaseTool
{
    private readonly Func<CronScheduler> _getScheduler;

    /// <param name="getScheduler">Lazy resolver — called at invocation time, not construction time.</param>
    public ManageCronTool(Func<CronScheduler> getScheduler)
    {
        _getScheduler = getScheduler;
    }

    public override string Name => "manage_cron";

    public override string Description =>
        "Manage cron-scheduled tasks — jobs that run an agent task on a time-based schedule " +
        "using standard 5-field cron expressions (minute hour day month weekday). " +
        "No missed-count tracking; use manage_heartbeat instead when you need failure detection. " +
        "Operations: add, remove, list.";

    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["operation"] = new()
        {
            Type = "string",
            Description = "add | remove | list",
            Required = true,
            EnumValues = ["add", "remove", "list"]
        },
        ["name"] = new()
        {
            Type = "string",
            Description = "Job name — required for 'add' and 'remove'",
            Required = false
        },
        ["cron"] = new()
        {
            Type = "string",
            Description = "5-field cron expression. Examples: '* * * * *' = every minute, " +
                          "'0 * * * *' = every hour, '0 9 * * *' = 9 am daily, " +
                          "'0 9 * * 1' = 9 am every Monday, '*/5 * * * *' = every 5 minutes",
            Required = false
        },
        ["task"] = new()
        {
            Type = "string",
            Description = "Agent task to run when the cron fires (required for 'add')",
            Required = false
        }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var operation = arguments.GetValueOrDefault("operation")?.ToString()?.ToLowerInvariant();
        var name      = arguments.GetValueOrDefault("name")?.ToString();
        var scheduler = _getScheduler();

        return operation switch
        {
            "add"    => Task.FromResult(Add(scheduler, name, arguments)),
            "remove" => Task.FromResult(Remove(scheduler, name)),
            "list"   => Task.FromResult(List(scheduler)),
            _ => Task.FromResult(ToolResult.Fail(
                $"Unknown operation '{operation}'. Valid values: add, remove, list"))
        };
    }

    private static ToolResult Add(CronScheduler scheduler, string? name, Dictionary<string, object?> args)
    {
        if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("'name' is required for add");
        var cron = args.GetValueOrDefault("cron")?.ToString();
        if (string.IsNullOrWhiteSpace(cron)) return ToolResult.Fail("'cron' expression is required for add");
        var task = args.GetValueOrDefault("task")?.ToString();
        if (string.IsNullOrWhiteSpace(task)) return ToolResult.Fail("'task' is required for add");

        if (scheduler.GetJob(name) != null)
            return ToolResult.Fail($"Cron job '{name}' already exists. Remove it first to replace it.");

        scheduler.AddJob(name, cron, task);
        var next = scheduler.GetJob(name)?.NextExecution;
        return ToolResult.Ok(
            $"Cron job '{name}' added.\n" +
            $"  Cron: {cron}\n" +
            $"  Task: {task}\n" +
            $"  Next run: {(next.HasValue ? next.Value.ToString("o") : "calculated on first tick")}");
    }

    private static ToolResult Remove(CronScheduler scheduler, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("'name' is required for remove");
        return scheduler.RemoveJob(name)
            ? ToolResult.Ok($"Cron job '{name}' removed.")
            : ToolResult.Fail($"Cron job '{name}' not found.");
    }

    private static ToolResult List(CronScheduler scheduler)
    {
        var jobs = scheduler.GetJobs();
        if (jobs.Count == 0)
            return ToolResult.Ok("No cron jobs configured.");

        var lines = jobs.Values.Select(j =>
            $"• {j.Name}  [{j.CronExpression}]\n" +
            $"  Task:       {j.Task}\n" +
            $"  Last run:   {(j.LastExecuted == DateTime.MinValue ? "never" : j.LastExecuted.ToString("o"))}\n" +
            $"  Next run:   {j.NextExecution:o}");

        return ToolResult.Ok($"{jobs.Count} cron job(s):\n\n{string.Join("\n\n", lines)}");
    }
}
