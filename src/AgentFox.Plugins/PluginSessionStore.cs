using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentFox.Plugins;

/// <summary>
/// Tracks execution sessions for plugins with tool audit trail.
/// Captures tool invocations, results, and errors for observability and audit purposes.
/// </summary>
public class PluginSessionStore
{
    private readonly ILogger<PluginSessionStore> _logger;
    private readonly ConcurrentDictionary<string, PluginSession> _sessions = new();
    private readonly ConcurrentDictionary<(string, string), List<ToolExecution>> _executions = new();

    public PluginSessionStore(ILogger<PluginSessionStore> logger)
    {
        _logger = logger;
    }

    /// <summary>Record tool pre-execution.</summary>
    public void OnToolStart(string pluginName, string sessionId, string toolName, IDictionary<string, object?> args, string executionId)
    {
        var session = _sessions.GetOrAdd(
            $"{pluginName}:{sessionId}",
            _ => new PluginSession
            {
                PluginName = pluginName,
                SessionId = sessionId,
                CreatedAt = DateTimeOffset.UtcNow
            });

        var execution = new ToolExecution
        {
            ExecutionId = executionId,
            ToolName = toolName,
            Arguments = args.ToDictionary(kv => kv.Key, kv => kv.Value),
            StartedAt = DateTimeOffset.UtcNow,
            Status = ToolExecutionStatus.Running
        };

        // The hook registry is global and fires for tools across every execution lane
        // (main / sub-agent / background), so these stores can be hit concurrently — and a
        // web client may read the same session at the same time. Lock the per-session list
        // for every structural change, and the session for counter writes.
        var list = _executions.GetOrAdd((pluginName, sessionId), _ => new List<ToolExecution>());
        lock (list)
            list.Add(execution);

        lock (session)
            session.ToolCount++;
        _logger.LogDebug("[{Plugin}:{Session}] Tool {Tool} started (exec={ExecId})", pluginName, sessionId, toolName, executionId);
    }

    /// <summary>Record tool post-execution success.</summary>
    public void OnToolComplete(string pluginName, string sessionId, string toolName, string result, long executionTimeMs, string executionId)
    {
        var session = _sessions.GetOrAdd(
            $"{pluginName}:{sessionId}",
            _ => new PluginSession { PluginName = pluginName, SessionId = sessionId, CreatedAt = DateTimeOffset.UtcNow });

        var key = (pluginName, sessionId);
        if (_executions.TryGetValue(key, out var list))
        {
            lock (list)
            {
                var exec = list.FirstOrDefault(e => e.ExecutionId == executionId);
                if (exec != null)
                {
                    exec.Status = ToolExecutionStatus.Completed;
                    exec.Result = result;
                    exec.ExecutionTimeMs = executionTimeMs;
                    exec.CompletedAt = DateTimeOffset.UtcNow;
                }
            }
        }

        lock (session)
            session.SuccessfulToolCount++;
        _logger.LogDebug("[{Plugin}:{Session}] Tool {Tool} completed in {Ms}ms (exec={ExecId})", pluginName, sessionId, toolName, executionTimeMs, executionId);
    }

    /// <summary>Record tool execution failure.</summary>
    public void OnToolError(string pluginName, string sessionId, string toolName, string error, long executionTimeMs, string executionId)
    {
        var session = _sessions.GetOrAdd(
            $"{pluginName}:{sessionId}",
            _ => new PluginSession { PluginName = pluginName, SessionId = sessionId, CreatedAt = DateTimeOffset.UtcNow });

        var key = (pluginName, sessionId);
        if (_executions.TryGetValue(key, out var list))
        {
            lock (list)
            {
                var exec = list.FirstOrDefault(e => e.ExecutionId == executionId);
                if (exec != null)
                {
                    exec.Status = ToolExecutionStatus.Failed;
                    exec.Error = error;
                    exec.ExecutionTimeMs = executionTimeMs;
                    exec.CompletedAt = DateTimeOffset.UtcNow;
                }
            }
        }

        lock (session)
            session.FailedToolCount++;
        _logger.LogWarning("[{Plugin}:{Session}] Tool {Tool} failed: {Error} (exec={ExecId})", pluginName, sessionId, toolName, error, executionId);
    }

    /// <summary>Get a single plugin session with its execution history.</summary>
    public PluginSessionDetail? GetSession(string pluginName, string sessionId)
    {
        var key = $"{pluginName}:{sessionId}";
        if (!_sessions.TryGetValue(key, out var session))
            return null;

        var exKey = (pluginName, sessionId);
        List<ToolExecution> execs;
        if (_executions.TryGetValue(exKey, out var list))
            lock (list) execs = list.OrderBy(e => e.StartedAt).ToList();
        else
            execs = new();

        return new PluginSessionDetail
        {
            PluginName = session.PluginName,
            SessionId = session.SessionId,
            CreatedAt = session.CreatedAt,
            LastActivityAt = execs.LastOrDefault()?.CompletedAt ?? session.CreatedAt,
            ToolCount = session.ToolCount,
            SuccessfulToolCount = session.SuccessfulToolCount,
            FailedToolCount = session.FailedToolCount,
            Executions = execs
        };
    }

    /// <summary>Get all active sessions for a plugin.</summary>
    public IEnumerable<PluginSessionSummary> GetActiveSessions(string pluginName) =>
        BuildSummaries(_sessions.Values.Where(s => s.PluginName == pluginName));

    /// <summary>Get all active sessions across every plugin (used by the "list all" endpoint).</summary>
    public IEnumerable<PluginSessionSummary> GetAllSessions() =>
        BuildSummaries(_sessions.Values);

    private IEnumerable<PluginSessionSummary> BuildSummaries(IEnumerable<PluginSession> sessions)
    {
        return sessions
            .OrderByDescending(s => s.CreatedAt)
            .Select(s =>
            {
                var exKey = (s.PluginName, s.SessionId);
                ToolExecution? latestExec = null;
                if (_executions.TryGetValue(exKey, out var list))
                    lock (list) latestExec = list.OrderByDescending(e => e.StartedAt).FirstOrDefault();

                return new PluginSessionSummary
                {
                    PluginName = s.PluginName,
                    SessionId = s.SessionId,
                    CreatedAt = s.CreatedAt,
                    LastActivityAt = latestExec?.CompletedAt ?? s.CreatedAt,
                    ToolCount = s.ToolCount,
                    SuccessfulToolCount = s.SuccessfulToolCount,
                    FailedToolCount = s.FailedToolCount
                };
            })
            .ToList();
    }

    /// <summary>Get aggregate statistics for a plugin across all sessions.</summary>
    public PluginSessionStats GetStats(string pluginName)
    {
        var sessions = _sessions.Values.Where(s => s.PluginName == pluginName).ToList();
        var totalTools = sessions.Sum(s => s.ToolCount);
        var successCount = sessions.Sum(s => s.SuccessfulToolCount);
        var failCount = sessions.Sum(s => s.FailedToolCount);

        return new PluginSessionStats
        {
            PluginName = pluginName,
            ActiveSessionCount = sessions.Count,
            TotalToolInvocations = totalTools,
            SuccessfulInvocations = successCount,
            FailedInvocations = failCount,
            SuccessRate = totalTools > 0 ? (double)successCount / totalTools : 0
        };
    }
}

/// <summary>In-memory session state (minimal).</summary>
public class PluginSession
{
    public string PluginName { get; set; } = "";
    public string SessionId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public int ToolCount { get; set; }
    public int SuccessfulToolCount { get; set; }
    public int FailedToolCount { get; set; }
}

/// <summary>Execution record for a single tool invocation.</summary>
public class ToolExecution
{
    public string ExecutionId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public Dictionary<string, object?> Arguments { get; set; } = new();
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long ExecutionTimeMs { get; set; }
    public ToolExecutionStatus Status { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
}

public enum ToolExecutionStatus
{
    Running,
    Completed,
    Failed
}

/// <summary>Session summary for list views.</summary>
public class PluginSessionSummary
{
    public string PluginName { get; set; } = "";
    public string SessionId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public int ToolCount { get; set; }
    public int SuccessfulToolCount { get; set; }
    public int FailedToolCount { get; set; }
}

/// <summary>Full session details with execution history.</summary>
public class PluginSessionDetail
{
    public string PluginName { get; set; } = "";
    public string SessionId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public int ToolCount { get; set; }
    public int SuccessfulToolCount { get; set; }
    public int FailedToolCount { get; set; }
    public List<ToolExecution> Executions { get; set; } = new();
}

/// <summary>Aggregate statistics across all sessions.</summary>
public class PluginSessionStats
{
    public string PluginName { get; set; } = "";
    public int ActiveSessionCount { get; set; }
    public int TotalToolInvocations { get; set; }
    public int SuccessfulInvocations { get; set; }
    public int FailedInvocations { get; set; }
    public double SuccessRate { get; set; }
}
