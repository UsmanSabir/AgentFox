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
