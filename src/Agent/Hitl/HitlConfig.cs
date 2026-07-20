namespace AgentFox.Hitl;

/// <summary>
/// Bound from the "Hitl" section of appsettings.json.
/// </summary>
public class HitlConfig
{
    /// <summary>Master switch — set to true to enable HITL approval flows.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Tool names that require human approval before execution.
    /// Example: ["shell", "delete", "write_file"]
    /// </summary>
    public List<string> RequireApprovalForTools { get; set; } = [];

    /// <summary>
    /// Contexts that bypass human approval entirely (both per-tool approval and plan approval).
    /// Use for trusted channels, automated origins (cron/heartbeat), or trusted agent roles.
    /// </summary>
    public HitlBypassConfig Bypass { get; set; } = new();
}

/// <summary>
/// Allow-list describing which sessions/agents may skip HITL approvals.
/// A request is bypassed if ANY rule matches. Bypass removes the *human* from the loop —
/// it does not disable the plan workflow itself (a bypassed session still flows through
/// submit_plan, but the plan auto-approves without waiting).
/// </summary>
public class HitlBypassConfig
{
    /// <summary>Skip all approvals everywhere. Use only for fully-trusted/offline deployments.</summary>
    public bool AutoApproveAll { get; set; } = false;

    /// <summary>Channel IDs whose sessions skip approval (e.g. a private admin chat).</summary>
    public List<string> ChannelIds { get; set; } = [];

    /// <summary>Channel types whose sessions skip approval (e.g. "Console", "Telegram").</summary>
    public List<string> ChannelTypes { get; set; } = [];

    /// <summary>Session origins that skip approval: Console, Channel, Heartbeat, CronJob, SubAgent.</summary>
    public List<string> SessionOrigins { get; set; } = [];

    /// <summary>Agent roles that skip approval (matched against the live agent's Role).</summary>
    public List<string> Roles { get; set; } = [];
}
