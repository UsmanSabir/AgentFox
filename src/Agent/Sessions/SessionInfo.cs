using System.Text.Json.Serialization;

namespace AgentFox.Sessions;

/// <summary>
/// Lifecycle status of a session.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionStatus
{
    /// <summary>Session is actively receiving messages.</summary>
    Active,

    /// <summary>Session has exceeded the idle timeout; eligible for archiving.</summary>
    Idle,

    /// <summary>Session was interrupted mid-run (CancellationToken cancelled).</summary>
    Aborted,

    /// <summary>Session has been moved to the archive directory.</summary>
    Archived
}

/// <summary>
/// Where the session originates — determines session-lifetime policy.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionOrigin
{
    /// <summary>Interactive REPL / console input.</summary>
    Console,

    /// <summary>External channel (WhatsApp, Telegram, Slack, Teams …).</summary>
    Channel,

    /// <summary>Periodic heartbeat run — always creates a fresh session.</summary>
    Heartbeat,

    /// <summary>Cron-scheduled job — always creates a fresh session.</summary>
    CronJob,

    /// <summary>Sub-agent spawned by a parent agent — lives under the parent's subdirectory.</summary>
    SubAgent
}

/// <summary>
/// Metadata record for a single session.  Serialised as a row in sessions/index.json.
/// </summary>
public class SessionInfo
{
    /// <summary>
    /// Filesystem-safe identifier used as conversationId and as the .md file stem.
    /// Examples: "ch_whatsapp_12345", "console", "hb_health_20250101_120000", "agentfox/sa_abc123"
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable logical key before sanitisation (e.g. "channel:whatsapp_12345").
    /// </summary>
    public string LogicalKey { get; set; } = string.Empty;

    public SessionOrigin Origin { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Active;

    /// <summary>The agent that owns this session.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Channel identifier for Channel-origin sessions.</summary>
    public string? ChannelId { get; set; }

    /// <summary>Channel type string (e.g. "WhatsApp", "Telegram") for Channel-origin sessions.</summary>
    public string? ChannelType { get; set; }

    /// <summary>
    /// SessionId of the parent session for SubAgent sessions.
    /// Used to reconstruct the agent hierarchy.
    /// </summary>
    public string? ParentSessionId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set when the session is interrupted by cancellation.</summary>
    public DateTime? AbortedAt { get; set; }

    /// <summary>Optional human-readable reason for abort (e.g. "timeout", "user cancelled").</summary>
    public string? AbortReason { get; set; }

    /// <summary>
    /// Path of the archived .md file relative to the archive root, populated after archiving.
    /// </summary>
    public string? ArchivePath { get; set; }
}

/// <summary>
/// Root object serialised to sessions/index.json.
/// </summary>
public class SessionIndex
{
    public List<SessionInfo> Sessions { get; set; } = new();
}
