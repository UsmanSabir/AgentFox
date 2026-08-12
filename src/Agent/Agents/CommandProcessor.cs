using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AgentFox.Agents;

/// <summary>
/// Delegate for handling command execution.
/// </summary>
public delegate Task CommandHandler(ICommand command, CancellationToken cancellationToken);

// ─────────────────────────────────────────────────────────────────────────────
// Per-lane policy
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Execution policy for a single lane.
/// </summary>
public class LanePolicy
{
    /// <summary>
    /// Maximum commands that can execute simultaneously in this lane.
    /// 1 = strictly serial. Values above 1 enable parallel execution.
    /// </summary>
    public int MaxConcurrency { get; init; } = 1;

    /// <summary>
    /// How long (ms) to sleep when the lane queue is empty before polling again.
    /// </summary>
    public int PollingDelayMilliseconds { get; init; } = 10;

    /// <summary>Serial lane — one command at a time.</summary>
    public static LanePolicy Serial(int pollingDelayMs = 10) =>
        new() { MaxConcurrency = 1, PollingDelayMilliseconds = pollingDelayMs };

    /// <summary>Parallel lane — up to <paramref name="maxConcurrency"/> commands simultaneously.</summary>
    public static LanePolicy Parallel(int maxConcurrency, int pollingDelayMs = 10) =>
        new() { MaxConcurrency = maxConcurrency, PollingDelayMilliseconds = pollingDelayMs };
}

// ─────────────────────────────────────────────────────────────────────────────
// Processor config
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Aggregates per-lane execution policies for <see cref="CommandProcessor"/>.
/// </summary>
public class CommandProcessorConfig
{
    /// <summary>
    /// How long a single command may run before the watchdog reports it as stuck. 0 disables the
    /// watchdog.
    /// <para>
    /// This does not cancel anything — it makes a wedge visible. A command that blocks forever
    /// holds a lane slot forever, and because Main is serial that reads to the operator as a dead
    /// terminal with no error anywhere: nothing logged, nothing printed, nothing to search for.
    /// </para>
    /// </summary>
    public int StuckCommandWarningSeconds { get; init; } = 600;

    /// <summary>How often the watchdog sweeps in-flight commands.</summary>
    public int WatchdogIntervalSeconds { get; init; } = 60;

    /// <summary>
    /// Per-lane policies. Lanes not listed fall back to <see cref="LanePolicy.Serial()"/>.
    /// </summary>
    public Dictionary<CommandLane, LanePolicy> LanePolicies { get; init; } = new()
    {
        // Main is always serial — one agent turn at a time
        [CommandLane.Main]       = LanePolicy.Serial(pollingDelayMs: 10),
        [CommandLane.Specialist] = LanePolicy.Parallel(maxConcurrency: 3, pollingDelayMs: 10),
        // Subagents run in parallel up to a cap driven by SubAgentConfiguration
        [CommandLane.Subagent]   = LanePolicy.Parallel(maxConcurrency: 10, pollingDelayMs: 10),
        // Long-running tools can overlap
        [CommandLane.Tool]       = LanePolicy.Parallel(maxConcurrency: 5,  pollingDelayMs: 10),
        // Background tasks are lowest-priority but still concurrent
        [CommandLane.Background] = LanePolicy.Parallel(maxConcurrency: 3,  pollingDelayMs: 20),
    };

    /// <summary>
    /// Creates a config that derives <c>Subagent</c> concurrency from
    /// <see cref="SubAgentConfiguration.MaxConcurrentSubAgents"/>.
    /// </summary>
    public static CommandProcessorConfig FromSubAgentConfig(SubAgentConfiguration subAgentConfig) =>
        new()
        {
            LanePolicies = new()
            {
                [CommandLane.Main]       = LanePolicy.Serial(pollingDelayMs: 10),
                [CommandLane.Specialist] = LanePolicy.Parallel(maxConcurrency: 3, pollingDelayMs: 10),
                [CommandLane.Subagent]   = LanePolicy.Parallel(subAgentConfig.MaxConcurrentSubAgents, pollingDelayMs: 10),
                [CommandLane.Tool]       = LanePolicy.Parallel(maxConcurrency: 5,  pollingDelayMs: 10),
                [CommandLane.Background] = LanePolicy.Parallel(maxConcurrency: 3,  pollingDelayMs: 20),
            }
        };
}

// ─────────────────────────────────────────────────────────────────────────────
// Processor
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Dispatches commands from <see cref="ICommandQueue"/> to registered lane handlers.
///
/// <para>Each lane runs its own background loop and enforces an independent
/// concurrency limit via a <see cref="SemaphoreSlim"/>.  This means:</para>
/// <list type="bullet">
///   <item>The <c>Main</c> lane is strictly serial (one active command at a time).</item>
///   <item>The <c>Subagent</c> lane runs up to <c>MaxConcurrentSubAgents</c> commands simultaneously.</item>
///   <item>Other lanes honour their own configured caps.</item>
/// </list>
/// <para>All in-flight tasks are tracked so <see cref="StopAsync"/> can drain them gracefully.</para>
/// </summary>
public class CommandProcessor : IDisposable
{
    private readonly ICommandQueue _commandQueue;
    private readonly ILogger? _logger;
    private readonly CommandProcessorConfig _config;
    private readonly Dictionary<CommandLane, CommandHandler?> _handlers;
    private readonly CancellationTokenSource _cts;

    // Per-lane concurrency gates
    private readonly Dictionary<CommandLane, SemaphoreSlim> _semaphores;

    // Global in-flight task registry (taskId → task) used for graceful drain
    private readonly ConcurrentDictionary<Guid, Task> _inflight = new();
    private readonly ConcurrentDictionary<CommandLane, int> _activeByLane = new();

    // What each in-flight task is, so a wedge can be named rather than just counted.
    private readonly ConcurrentDictionary<Guid, InflightCommand> _inflightInfo = new();

    // Reported-once set, so a permanently stuck command produces one warning and one escalation
    // rather than a line every sweep for the rest of the process's life.
    private readonly ConcurrentDictionary<Guid, bool> _reportedStuck = new();

    private Task? _watchdogLoop;

    // Lane pump tasks (one per lane)
    private readonly List<Task> _laneLoops = [];
    private bool _started;
    private bool _disposed;

    // Statistics
    private long _totalProcessed;
    private long _totalFailed;
    private DateTime _startTime;

    // ── Construction ─────────────────────────────────────────────────────────

    public CommandProcessor(
        ICommandQueue commandQueue,
        CommandProcessorConfig? config = null,
        ILogger? logger = null)
    {
        _commandQueue = commandQueue ?? throw new ArgumentNullException(nameof(commandQueue));
        _config = config ?? new CommandProcessorConfig();
        _logger = logger;
        _cts = new CancellationTokenSource();

        // Ensure every known lane has a handler slot and a semaphore
        _handlers = [];
        _semaphores = [];
        foreach (var lane in Enum.GetValues<CommandLane>())
        {
            _handlers[lane] = null;
            var policy = _config.LanePolicies.GetValueOrDefault(lane, LanePolicy.Serial());
            _semaphores[lane] = new SemaphoreSlim(policy.MaxConcurrency, policy.MaxConcurrency);
        }
    }

    // ── Registration ─────────────────────────────────────────────────────────

    /// <summary>Register a handler for a specific lane. Must be called before <see cref="Start"/>.</summary>
    public void RegisterLaneHandler(CommandLane lane, CommandHandler handler)
    {
        _handlers[lane] = handler;
        _logger?.LogInformation("Registered handler for lane {Lane}", lane);
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>Start a pump loop for every configured lane.</summary>
    public void Start()
    {
        if (_started)
        {
            _logger?.LogWarning("CommandProcessor is already running");
            return;
        }

        _started = true;
        _startTime = DateTime.UtcNow;

        foreach (var lane in Enum.GetValues<CommandLane>())
        {
            var policy = _config.LanePolicies.GetValueOrDefault(lane, LanePolicy.Serial());
            _laneLoops.Add(RunLaneLoopAsync(lane, policy, _cts.Token));
        }

        if (_config.StuckCommandWarningSeconds > 0)
            _watchdogLoop = RunWatchdogLoopAsync(_cts.Token);

        _logger?.LogInformation("CommandProcessor started ({LaneCount} lanes)", _laneLoops.Count);
    }

    /// <summary>
    /// Signal cancellation, wait for lane loops to exit, then drain all in-flight handlers.
    /// If <paramref name="drainTimeout"/> elapses before all in-flight handlers complete, returns anyway.
    /// </summary>
    public async Task StopAsync(TimeSpan? drainTimeout = null)
    {
        _logger?.LogInformation("CommandProcessor stopping…");
        _cts.Cancel();

        // Wait for pump loops to exit cleanly
        try { await Task.WhenAll(_laneLoops).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        if (_watchdogLoop != null)
        {
            try { await _watchdogLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        // Drain in-flight handlers
        if (_inflight.Count > 0)
        {
            _logger?.LogInformation("Draining {Count} in-flight command(s)…", _inflight.Count);
            var drain = Task.WhenAll(_inflight.Values);
            var deadline = drainTimeout.HasValue
                ? Task.Delay(drainTimeout.Value)
                : Task.CompletedTask;

            await Task.WhenAny(drain, deadline).ConfigureAwait(false);
        }

        _logger?.LogInformation("CommandProcessor stopped (processed={P}, failed={F})",
            _totalProcessed, _totalFailed);
    }

    // ── Statistics ───────────────────────────────────────────────────────────

    public ProcessorStatistics GetStatistics() => new()
    {
        TotalProcessed = Interlocked.Read(ref _totalProcessed),
        TotalFailed    = Interlocked.Read(ref _totalFailed),
        Uptime         = DateTime.UtcNow - _startTime,
        QueuedCommands = _commandQueue.GetTotalQueueCount(),
        ActiveCommands = _inflight.Count,
    };

    public IReadOnlyList<CommandLaneStatistics> GetLaneStatistics() =>
        Enum.GetValues<CommandLane>()
            .Select(lane => new CommandLaneStatistics(
                lane.ToString(),
                _commandQueue.GetQueueCount(lane),
                _activeByLane.GetValueOrDefault(lane),
                _config.LanePolicies.GetValueOrDefault(lane, LanePolicy.Serial()).MaxConcurrency,
                _handlers.GetValueOrDefault(lane) is not null))
            .ToList();

    // ── Core pump ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the pump loop for <paramref name="lane"/>.
    /// Dequeues commands one at a time but fires them as concurrent tasks
    /// (bounded by the lane's <see cref="LanePolicy.MaxConcurrency"/> semaphore).
    /// For serial lanes (MaxConcurrency = 1) the effect is the same as awaiting inline.
    /// </summary>
    private async Task RunLaneLoopAsync(CommandLane lane, LanePolicy policy, CancellationToken ct)
    {
        var semaphore = _semaphores[lane];

        _logger?.LogDebug("Lane {Lane} pump started (maxConcurrency={Max})", lane, policy.MaxConcurrency);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_commandQueue.TryDequeue(lane, out var command) || command is null)
                {
                    await Task.Delay(policy.PollingDelayMilliseconds, ct).ConfigureAwait(false);
                    continue;
                }

                if (!_handlers.TryGetValue(lane, out var handler) || handler is null)
                {
                    _logger?.LogWarning("No handler registered for lane {Lane} — dropping command {RunId}",
                        lane, command.RunId);
                    Interlocked.Increment(ref _totalFailed);
                    continue;
                }

                // Block this loop until a concurrency slot is free, then fire the task.
                // For serial lanes this is equivalent to awaiting inline.
                await semaphore.WaitAsync(ct).ConfigureAwait(false);

                var taskId  = Guid.NewGuid();
                var tracked = ExecuteHandlerAsync(handler, command, lane, semaphore, taskId, ct);
                _inflight[taskId] = tracked;
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Lane {Lane} pump cancelled", lane);
        }
    }

    /// <summary>
    /// Executes a single handler invocation, releases the semaphore when done,
    /// and removes itself from the in-flight registry.
    /// </summary>
    private async Task ExecuteHandlerAsync(
        CommandHandler handler,
        ICommand command,
        CommandLane lane,
        SemaphoreSlim semaphore,
        Guid taskId,
        CancellationToken ct)
    {
        try
        {
            _activeByLane.AddOrUpdate(lane, 1, (_, current) => current + 1);
            _inflightInfo[taskId] = new InflightCommand(
                command.RunId, command.SessionKey, lane, DateTime.UtcNow);
            _logger?.LogDebug("Executing {Lane} command {RunId}", lane, command.RunId);
            await handler(command, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _totalProcessed);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("{Lane} command {RunId} cancelled", lane, command.RunId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "{Lane} command {RunId} failed: {Message}", lane, command.RunId, ex.Message);
            Interlocked.Increment(ref _totalFailed);
        }
        finally
        {
            _activeByLane.AddOrUpdate(lane, 0, (_, current) => Math.Max(0, current - 1));
            semaphore.Release();
            _inflight.TryRemove(taskId, out _);

            if (_inflightInfo.TryRemove(taskId, out var info) && _reportedStuck.TryRemove(taskId, out _))
                _logger?.LogInformation(
                    "{Lane} command {RunId} recovered after {Minutes:F1} min.",
                    info.Lane, info.RunId, (DateTime.UtcNow - info.StartedAt).TotalMinutes);
        }
    }

    // ── Watchdog ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Periodically reports commands that have been running past
    /// <see cref="CommandProcessorConfig.StuckCommandWarningSeconds"/>, and lanes that are fully
    /// saturated while work is still queued behind them.
    ///
    /// <para>
    /// Deliberately observational — it never cancels a handler. The cancellation ceilings live where
    /// the blocking actually happens (tool timeouts, HITL expiry); killing a turn from out here
    /// would abandon partial state with nothing to hand back. What this fixes is the diagnosis: a
    /// wedged Main lane used to present as a terminal that stopped echoing, with no log line
    /// anywhere pointing at the command responsible.
    /// </para>
    /// </summary>
    private async Task RunWatchdogLoopAsync(CancellationToken ct)
    {
        var threshold = TimeSpan.FromSeconds(_config.StuckCommandWarningSeconds);
        var interval  = TimeSpan.FromSeconds(Math.Max(5, _config.WatchdogIntervalSeconds));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);

                var now = DateTime.UtcNow;

                foreach (var (taskId, info) in _inflightInfo)
                {
                    var elapsed = now - info.StartedAt;
                    if (elapsed < threshold) continue;
                    if (!_reportedStuck.TryAdd(taskId, true)) continue; // already reported

                    var maxConcurrency = _config.LanePolicies
                        .GetValueOrDefault(info.Lane, LanePolicy.Serial()).MaxConcurrency;

                    _logger?.LogWarning(
                        "{Lane} command {RunId} (session {Session}) has been running for {Minutes:F1} min. " +
                        "It is holding 1 of {Max} slot(s) on this lane" +
                        "{MainNote}. Queued behind it: {Queued}.",
                        info.Lane, info.RunId, info.SessionKey, elapsed.TotalMinutes,
                        maxConcurrency,
                        info.Lane == CommandLane.Main
                            ? ", which is serial — the interactive prompt is blocked until it finishes"
                            : string.Empty,
                        _commandQueue.GetQueueCount(info.Lane));
                }

                foreach (var lane in Enum.GetValues<CommandLane>())
                {
                    var max    = _config.LanePolicies.GetValueOrDefault(lane, LanePolicy.Serial()).MaxConcurrency;
                    var active = _activeByLane.GetValueOrDefault(lane);
                    var queued = _commandQueue.GetQueueCount(lane);

                    if (active >= max && queued > 0)
                        _logger?.LogWarning(
                            "Lane {Lane} is saturated ({Active}/{Max} slots busy) with {Queued} command(s) " +
                            "waiting. If this persists, one of the running commands is not returning.",
                            lane, active, max, queued);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("CommandProcessor watchdog stopped");
        }
    }

    /// <summary>
    /// In-flight commands that have exceeded <paramref name="olderThan"/>, newest first.
    /// Surfaced by <c>/agents stats</c> so a wedge is visible without reading the log file.
    /// </summary>
    public IReadOnlyList<InflightCommand> GetLongRunningCommands(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        return _inflightInfo.Values
            .Where(i => i.StartedAt <= cutoff)
            .OrderBy(i => i.StartedAt)
            .ToList();
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();
        foreach (var s in _semaphores.Values) s.Dispose();
    }
}

/// <summary>A command currently occupying a lane slot.</summary>
public sealed record InflightCommand(
    string RunId,
    string SessionKey,
    CommandLane Lane,
    DateTime StartedAt)
{
    public TimeSpan Elapsed => DateTime.UtcNow - StartedAt;
}

public sealed record CommandLaneStatistics(
    string Lane,
    int QueuedCommands,
    int ActiveCommands,
    int MaxConcurrency,
    bool HandlerRegistered);

// ─────────────────────────────────────────────────────────────────────────────
// Statistics
// ─────────────────────────────────────────────────────────────────────────────

public class ProcessorStatistics
{
    public long TotalProcessed { get; set; }
    public long TotalFailed    { get; set; }
    public TimeSpan Uptime     { get; set; }
    public int QueuedCommands  { get; set; }
    /// <summary>Commands currently executing across all lanes.</summary>
    public int ActiveCommands  { get; set; }
}
