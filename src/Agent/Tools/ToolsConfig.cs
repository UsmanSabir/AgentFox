namespace AgentFox.Tools;

/// <summary>
/// Controls which built-in tool groups are registered at startup.
/// Bound from the "Tools" section of appsettings.json.
/// All groups default to enabled — set a group to false to skip registration entirely.
/// Use <see cref="Disabled"/> for per-tool overrides within an otherwise-enabled group.
/// </summary>
/// <remarks>
/// Common profiles:
///   Docker / sandboxed  — set Shell=false, FileSystem=false, SystemInfo=false
///   Read-only agent     — set Shell=false, FileSystem=false (or Disabled: ["write_file","delete"])
///   Minimal / headless  — set all groups explicitly as needed
/// </remarks>
public class ToolsConfig
{
    // ── Tool groups ───────────────────────────────────────────────────────────

    /// <summary>Shell command execution via the <c>shell</c> tool. Disable in sandboxed/docker environments.</summary>
    public bool Shell { get; set; } = true;

    /// <summary>File I/O tools: read_file, write_file, list_files, search_files, make_directory, delete.</summary>
    public bool FileSystem { get; set; } = true;

    /// <summary>Web tools: web_search, fetch_url.</summary>
    public bool Web { get; set; } = true;

    /// <summary>Utility tools: calculate, uuid, timestamp.</summary>
    public bool Utilities { get; set; } = true;

    /// <summary>System information tool: get_env_info.</summary>
    public bool SystemInfo { get; set; } = true;

    /// <summary>Memory management tools: add_memory, search_memory, get_all_memories.</summary>
    public bool Memory { get; set; } = true;

    /// <summary>Sub-agent spawning: spawn_subagent, spawn_background_subagent.</summary>
    public bool SubAgent { get; set; } = true;

    /// <summary>Scheduling tools: manage_heartbeat, manage_cron.</summary>
    public bool Scheduling { get; set; } = true;

    /// <summary>Channel management tools: send_to_channel, manage_channel.</summary>
    public bool Channels { get; set; } = true;

    /// <summary>MCP server management tool: manage_mcp_server.</summary>
    public bool Mcp { get; set; } = true;

    // ── notify_user behaviour ─────────────────────────────────────────────────

    /// <summary>
    /// Allow a sub-agent to deliver to the user's channels via <c>notify_user</c>.
    /// Off by default: a sub-agent's result is meant to travel back to the agent that spawned it,
    /// which then decides what the user sees. With this on, a parent that delegates "gather the
    /// data and post the update" produces two deliveries — one from the sub-agent, one from itself.
    /// </summary>
    public bool SubAgentNotify { get; set; } = false;

    /// <summary>
    /// Window (seconds) in which a near-identical <c>notify_user</c> message is treated as a
    /// resend and suppressed. Scoped per session. 0 disables the check.
    /// Guards against auto-continuations and stale todo items re-delivering the same update.
    /// </summary>
    public int DuplicateNotifyWindowSeconds { get; set; } = 300;

    /// <summary>
    /// Similarity (0–1) at or above which two messages count as the same for the check above.
    /// Compared on word shingles, so a report where only a few figures changed still matches.
    /// </summary>
    public double DuplicateNotifyThreshold { get; set; } = 0.9;

    // ── Per-tool overrides ────────────────────────────────────────────────────

    /// <summary>
    /// Individual tool names to disable regardless of their group flag.
    /// Takes precedence over the group setting (i.e. group=true but name in Disabled → not registered).
    /// Example: ["shell", "delete", "get_env_info"]
    /// </summary>
    public List<string> Disabled { get; set; } = [];

    /// <summary>
    /// Returns true when the named tool is allowed by both its group flag and the Disabled list.
    /// </summary>
    public bool IsEnabled(string toolName) =>
        !Disabled.Contains(toolName, StringComparer.OrdinalIgnoreCase);
}
