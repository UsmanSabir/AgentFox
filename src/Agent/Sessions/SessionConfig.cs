namespace AgentFox.Sessions;

/// <summary>
/// Configuration for session lifecycle management.
/// Bind from appsettings.json "Sessions" section.
/// </summary>
public class SessionConfig
{
    /// <summary>
    /// Minutes of inactivity before a session is considered idle and queued for archiving.
    /// Default: 30
    /// </summary>
    public int IdleTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Number of days to retain archived sessions before deleting them.
    /// Default: 30
    /// </summary>
    public int ArchiveRetentionDays { get; set; } = 30;

    /// <summary>
    /// Directory (relative to workspace root) where active session files and index.json are stored.
    /// Default: "sessions"
    /// </summary>
    public string SessionDirectory { get; set; } = "sessions";

    /// <summary>
    /// Directory (relative to workspace root) where archived session files are moved.
    /// Default: "archive/sessions"
    /// </summary>
    public string ArchiveDirectory { get; set; } = "archive/sessions";

    /// <summary>
    /// How often (in seconds) the background timer runs idle-detection and archive cleanup.
    /// Default: 60
    /// </summary>
    public int BackgroundCheckIntervalSeconds { get; set; } = 60;
}
