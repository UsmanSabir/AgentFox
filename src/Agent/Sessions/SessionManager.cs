using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFox.Tools;
using Microsoft.Extensions.Logging;

namespace AgentFox.Sessions;

/// <summary>
/// Manages session lifecycle across channels, console, heartbeats, cron jobs, and sub-agents.
///
/// Responsibilities:
///   - Assign stable conversation IDs per channel (one session per channel, persisted across restarts).
///   - Create fresh (ephemeral) sessions for heartbeats and cron jobs.
///   - Scope sub-agent sessions to a per-agent subdirectory.
///   - Detect and archive idle sessions via a background timer.
///   - Handle /new and /reset commands to reset a session.
///   - Prune archived sessions older than ArchiveRetentionDays.
///   - Persist all session metadata to sessions/index.json.
/// </summary>
public class SessionManager : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SessionConfig _config;
    private readonly string _sessionDir;
    private readonly string _archiveDir;

    /// <summary>Resolved absolute path where active session files and index.json are stored.</summary>
    public string SessionDirectory => _sessionDir;

    /// <summary>Resolved absolute path where archived session files are stored.</summary>
    public string ArchiveDirectory => _archiveDir;
    private readonly ILogger? _logger;

    // In-memory index: sessionId → SessionInfo
    private readonly ConcurrentDictionary<string, SessionInfo> _index = new();
    // Channel lookup: channelId → sessionId  (only Active sessions)
    private readonly ConcurrentDictionary<string, string> _channelMap = new();

    // SessionIds loaded from the index file at startup — used to detect
    // sessions that were Active when the previous process terminated.
    private readonly HashSet<string> _preloadedSessionIds = new();

    private readonly System.Timers.Timer _bgTimer;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _disposed;

    // Commands that trigger a session reset
    private static readonly HashSet<string> ResetCommands =
        new(StringComparer.OrdinalIgnoreCase) { "/new", "/reset" };

    public SessionManager(SessionConfig config, WorkspaceManager workspaceManager, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;

        var ws = workspaceManager ?? throw new ArgumentNullException(nameof(workspaceManager));
        _sessionDir = ws.ResolvePath(config.SessionDirectory);
        _archiveDir = ws.ResolvePath(config.ArchiveDirectory);

        Directory.CreateDirectory(_sessionDir);
        Directory.CreateDirectory(_archiveDir);

        LoadIndex();

        _bgTimer = new System.Timers.Timer(_config.BackgroundCheckIntervalSeconds * 1000);
        _bgTimer.Elapsed += OnBackgroundTick;
        _bgTimer.AutoReset = true;
        _bgTimer.Start();
    }

    // -------------------------------------------------------------------------
    // Public API — session acquisition
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the active conversation ID for the given channel, creating one if none exists.
    /// The same session persists across restarts until manually reset or idle-archived.
    /// </summary>
    public string GetOrCreateChannelSession(string channelId, string channelType, string agentId)
    {
        if (_channelMap.TryGetValue(channelId, out var existing) &&
            _index.TryGetValue(existing, out var info) &&
            info.Status is SessionStatus.Active or SessionStatus.Idle)
        {
            return existing;
        }

        var sessionId = Sanitize($"ch_{channelId}");
        // If the ID collides (unlikely but safe), append a short suffix
        while (_index.ContainsKey(sessionId) && _index[sessionId].ChannelId != channelId)
            sessionId += "_" + Guid.NewGuid().ToString("N")[..4];

        var session = new SessionInfo
        {
            SessionId = sessionId,
            LogicalKey = $"channel:{channelId}",
            Origin = SessionOrigin.Channel,
            Status = SessionStatus.Active,
            AgentId = agentId,
            ChannelId = channelId,
            ChannelType = channelType,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        _index[sessionId] = session;
        _channelMap[channelId] = sessionId;
        SaveIndexAsync();

        _logger?.LogInformation("Created channel session {SessionId} for channel {ChannelId}", sessionId, channelId);
        return sessionId;
    }

    /// <summary>
    /// Returns the single persistent console session for the given agent, creating it if absent.
    /// </summary>
    public string GetOrCreateConsoleSession(string agentId)
    {
        const string sessionId = "console";
        if (_index.TryGetValue(sessionId, out var existing) &&
            existing.Status is SessionStatus.Active or SessionStatus.Idle)
        {
            return sessionId;
        }

        var session = new SessionInfo
        {
            SessionId = sessionId,
            LogicalKey = "console",
            Origin = SessionOrigin.Console,
            Status = SessionStatus.Active,
            AgentId = agentId,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        _index[sessionId] = session;
        SaveIndexAsync();
        return sessionId;
    }

    /// <summary>
    /// Creates a brand-new session every call — intended for heartbeats and cron jobs where
    /// each execution should start with a clean context.
    /// </summary>
    /// <param name="origin">Heartbeat or CronJob</param>
    /// <param name="identifier">Beat name or job name</param>
    /// <param name="agentId">Owning agent</param>
    public string CreateFreshSession(SessionOrigin origin, string identifier, string agentId)
    {
        var prefix = origin == SessionOrigin.Heartbeat ? "hb" : "cron";
        var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var sessionId = Sanitize($"{prefix}_{identifier}_{ts}");

        var session = new SessionInfo
        {
            SessionId = sessionId,
            LogicalKey = $"{prefix}:{identifier}",
            Origin = origin,
            Status = SessionStatus.Active,
            AgentId = agentId,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        _index[sessionId] = session;
        SaveIndexAsync();

        _logger?.LogDebug("Created fresh {Origin} session {SessionId}", origin, sessionId);
        return sessionId;
    }

    /// <summary>
    /// Creates a sub-agent session scoped to a per-agent subdirectory.
    /// The returned ID contains a directory separator so MarkdownSessionStore
    /// writes to {sessionDir}/{agentId}/sa_{runId}.md.
    /// </summary>
    public string CreateSubAgentSession(string agentId, string runId, string parentSessionId)
    {
        var safeName = Sanitize(agentId);
        var sessionId = $"{safeName}/sa_{Sanitize(runId)}";

        var session = new SessionInfo
        {
            SessionId = sessionId,
            LogicalKey = $"subagent:{agentId}:{runId}",
            Origin = SessionOrigin.SubAgent,
            Status = SessionStatus.Active,
            AgentId = agentId,
            ParentSessionId = parentSessionId,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        _index[sessionId] = session;
        SaveIndexAsync();

        _logger?.LogDebug("Created sub-agent session {SessionId} under parent {Parent}", sessionId, parentSessionId);
        return sessionId;
    }

    // -------------------------------------------------------------------------
    // Public API — session lifecycle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Updates LastActivityAt and reactivates an Idle session.
    /// Call after each agent turn to keep the session alive.
    /// </summary>
    public void TouchSession(string sessionId)
    {
        if (_index.TryGetValue(sessionId, out var info))
        {
            info.LastActivityAt = DateTime.UtcNow;
            if (info.Status == SessionStatus.Idle)
            {
                info.Status = SessionStatus.Active;
                _logger?.LogDebug("Session {SessionId} reactivated (was idle)", sessionId);
            }
        }
    }

    /// <summary>
    /// Marks a session as Aborted (e.g. CancellationToken cancelled mid-run).
    /// The session is eligible for archiving on the next background tick.
    /// </summary>
    public void MarkAborted(string sessionId, string? reason = null)
    {
        if (!_index.TryGetValue(sessionId, out var info)) return;

        info.Status = SessionStatus.Aborted;
        info.AbortedAt = DateTime.UtcNow;
        info.AbortReason = reason;
        SaveIndexAsync();

        _logger?.LogWarning("Session {SessionId} marked as Aborted. Reason: {Reason}", sessionId, reason ?? "n/a");
    }

    /// <summary>
    /// Returns true if the task string is a reset command (/new or /reset).
    /// </summary>
    public static bool IsResetCommand(string task) =>
        ResetCommands.Contains(task.Trim());

    /// <summary>
    /// Archives the old session and returns a new conversation ID for the same channel/origin.
    /// Called when the user sends /new or /reset.
    /// For non-channel origins (console, etc.) the new ID is a timestamped variant.
    /// </summary>
    public string ResetSession(string oldSessionId)
    {
        if (!_index.TryGetValue(oldSessionId, out var old))
            return oldSessionId; // nothing to reset

        ArchiveSession(oldSessionId);

        // Create the replacement session
        string newSessionId = old.Origin switch
        {
            SessionOrigin.Channel when old.ChannelId != null =>
                GetOrCreateChannelSession(old.ChannelId, old.ChannelType ?? "unknown", old.AgentId),

            SessionOrigin.Console =>
                CreateFreshConsoleSession(old.AgentId),

            _ => CreateFreshSession(old.Origin, old.LogicalKey, old.AgentId)
        };

        _logger?.LogInformation("Session reset: {Old} → {New}", oldSessionId, newSessionId);
        return newSessionId;
    }

    /// <summary>
    /// Archives a session: moves its .md file to the archive directory and updates status.
    /// </summary>
    public void ArchiveSession(string sessionId)
    {
        if (!_index.TryGetValue(sessionId, out var info)) return;
        if (info.Status == SessionStatus.Archived) return;

        var srcPath = ConversationFilePath(sessionId);
        if (File.Exists(srcPath))
        {
            var monthDir = Path.Combine(_archiveDir, DateTime.UtcNow.ToString("yyyy-MM"));
            Directory.CreateDirectory(monthDir);

            var stem = sessionId.Replace('/', '_').Replace('\\', '_');
            var ts = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            var destPath = Path.Combine(monthDir, $"{stem}_{ts}.md");

            try
            {
                File.Move(srcPath, destPath);
                info.ArchivePath = Path.GetRelativePath(_archiveDir, destPath);
                _logger?.LogInformation("Archived session file {Src} → {Dest}", srcPath, destPath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to archive session file {Path}", srcPath);
            }
        }

        // Remove from channel map so a new session can be created
        if (info.ChannelId != null)
            _channelMap.TryRemove(info.ChannelId, out _);

        info.Status = SessionStatus.Archived;
        SaveIndexAsync();
    }

    /// <summary>Returns the SessionInfo for a conversation ID, or null if unknown.</summary>
    public SessionInfo? GetSession(string sessionId) =>
        _index.TryGetValue(sessionId, out var info) ? info : null;

    /// <summary>Snapshot of all tracked sessions.</summary>
    public IReadOnlyList<SessionInfo> GetAllSessions() =>
        _index.Values.ToList();

    /// <summary>
    /// Returns sessions that were persisted as <see cref="SessionStatus.Active"/> when the
    /// previous process terminated — i.e., work that was in progress and may need recovery.
    ///
    /// Only sessions loaded from the index at startup are considered; any session created
    /// during this run is excluded.
    /// </summary>
    public IReadOnlyList<SessionInfo> GetInterruptedActiveSessions() =>
        _preloadedSessionIds
            .Where(id => _index.TryGetValue(id, out var s) && s.Status == SessionStatus.Active)
            .Select(id => _index[id])
            .ToList();

    // -------------------------------------------------------------------------
    // Path helpers (public so MarkdownSessionStore can share the convention)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Full path to the .md conversation file for the given conversationId.
    /// Supports sub-directory paths (e.g. "agentfox/sa_abc123" → sessions/agentfox/sa_abc123.md).
    /// </summary>
    public string ConversationFilePath(string sessionId)
    {
        var rel = sessionId.Replace('/', Path.DirectorySeparatorChar)
                           .Replace('\\', Path.DirectorySeparatorChar);
        return Path.Combine(_sessionDir, rel + ".md");
    }

    // -------------------------------------------------------------------------
    // Background maintenance
    // -------------------------------------------------------------------------

    private async void OnBackgroundTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        try
        {
            MarkIdleSessions();
            await CleanupExpiredArchivesAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SessionManager background tick error");
        }
    }

    private void MarkIdleSessions()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-_config.IdleTimeoutMinutes);
        bool changed = false;

        foreach (var info in _index.Values.Where(s => s.Status == SessionStatus.Active))
        {
            if (info.LastActivityAt < cutoff)
            {
                _logger?.LogInformation("Session {SessionId} is idle (last activity: {LastActivity})",
                    info.SessionId, info.LastActivityAt);

                ArchiveSession(info.SessionId);
                changed = true;
            }
        }

        // Also archive aborted sessions that have been sitting for > idle timeout
        foreach (var info in _index.Values.Where(s => s.Status == SessionStatus.Aborted))
        {
            if (info.AbortedAt.HasValue && info.AbortedAt.Value < cutoff)
            {
                _logger?.LogInformation("Archiving aborted session {SessionId}", info.SessionId);
                ArchiveSession(info.SessionId);
                changed = true;
            }
        }

        if (changed) SaveIndexAsync();
    }

    private Task CleanupExpiredArchivesAsync()
    {
        var cutoff = DateTime.UtcNow.AddDays(-_config.ArchiveRetentionDays);

        try
        {
            if (!Directory.Exists(_archiveDir)) return Task.CompletedTask;

            foreach (var file in Directory.EnumerateFiles(_archiveDir, "*.md", SearchOption.AllDirectories))
            {
                var created = File.GetCreationTimeUtc(file);
                if (created < cutoff)
                {
                    File.Delete(file);
                    _logger?.LogInformation("Deleted expired archive {File}", file);
                }
            }

            // Remove empty month directories
            foreach (var dir in Directory.EnumerateDirectories(_archiveDir))
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error cleaning up expired archives");
        }

        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Index persistence
    // -------------------------------------------------------------------------

    private void LoadIndex()
    {
        var path = IndexPath();
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            var idx = JsonSerializer.Deserialize<SessionIndex>(json, JsonOpts);
            if (idx == null) return;

            foreach (var s in idx.Sessions)
            {
                _index[s.SessionId] = s;
                _preloadedSessionIds.Add(s.SessionId);
                if (s.Origin == SessionOrigin.Channel &&
                    s.ChannelId != null &&
                    s.Status is SessionStatus.Active or SessionStatus.Idle)
                {
                    _channelMap[s.ChannelId] = s.SessionId;
                }
            }

            _logger?.LogInformation("Loaded {Count} sessions from index", _index.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load session index from {Path}", path);
        }
    }

    private void SaveIndexAsync()
    {
        // Fire-and-forget with semaphore to prevent concurrent writes
        _ = Task.Run(async () =>
        {
            await _saveLock.WaitAsync();
            try
            {
                var idx = new SessionIndex { Sessions = _index.Values.ToList() };
                var json = JsonSerializer.Serialize(idx, JsonOpts);
                await File.WriteAllTextAsync(IndexPath(), json);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to save session index");
            }
            finally
            {
                _saveLock.Release();
            }
        });
    }

    private string IndexPath() => Path.Combine(_sessionDir, "index.json");

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a new console session with a timestamp suffix (used after /reset on console).
    /// </summary>
    private string CreateFreshConsoleSession(string agentId)
    {
        // Remove old console session from index so a new one can be registered
        _index.TryRemove("console", out _);

        return GetOrCreateConsoleSession(agentId);
    }

    /// <summary>
    /// Converts an arbitrary string into a filesystem-safe identifier.
    /// Replaces characters invalid on Windows/Linux with underscores.
    /// </summary>
    public static string Sanitize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(new[] { ':', '/', '\\', ' ' })
            .ToHashSet();
        var chars = input.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim('_');
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bgTimer.Stop();
        _bgTimer.Dispose();
        _saveLock.Dispose();
    }
}
