namespace AgentFox.Harness;

/// <summary>
/// Feature-flagged configuration for the optional Microsoft Agent Framework HarnessAgent
/// execution profile. Disabled by default: with <see cref="Enabled"/> false, AgentFox behaviour
/// is unchanged and no Harness code path runs. Capabilities are opt-in per named profile and
/// default to least privilege.
/// </summary>
public sealed class HarnessOptions
{
    public const string SectionName = "Harness";

    /// <summary>Master switch. False (default) means the adapter refuses to create agents.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Profile used when the caller does not name one.</summary>
    public string DefaultProfile { get; set; } = MainSafeProfile;

    public const string MainSafeProfile = "main-safe";
    public const string TradingResearchProfile = "trading-research";
    public const string DeveloperSandboxProfile = "developer-sandbox";

    /// <summary>
    /// Named capability profiles. The three roadmap profiles are seeded with safe defaults and
    /// can be overridden or extended from configuration.
    /// </summary>
    public Dictionary<string, HarnessProfileOptions> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // Harness features disabled until individually approved.
        [MainSafeProfile] = new(),
        // Read-only research/reporting; still no file, web, shell, or skill capability until
        // the Phase 1 bridge work lands. Telemetry on so pilot runs are traceable.
        [TradingResearchProfile] = new() { EnableOpenTelemetry = true },
        // Planning UX (todos/modes) allowed; everything else stays off.
        [DeveloperSandboxProfile] = new() { EnableTodoAndModes = true, EnableOpenTelemetry = true },
    };
}

/// <summary>
/// Per-profile capability switches. Every switch defaults to the least-privilege setting; the
/// factory maps them onto explicit HarnessAgentOptions values so no Harness default is accepted
/// implicitly. Capabilities not yet bridged through AgentFox policy (file access, file memory,
/// web search, shell, skills discovery, background agents) have no switch here on purpose —
/// the factory hard-disables them until a later roadmap phase wires them deliberately.
/// </summary>
public sealed class HarnessProfileOptions
{
    /// <summary>Allow Harness todo-list and agent-mode providers (planning UX only — AgentFox's plan gate stays the enforcement source).</summary>
    public bool EnableTodoAndModes { get; set; } = false;

    /// <summary>Emit OpenTelemetry traces from the Harness pipeline.</summary>
    public bool EnableOpenTelemetry { get; set; } = false;

    /// <summary>OpenTelemetry source name used when telemetry is enabled.</summary>
    public string OpenTelemetrySourceName { get; set; } = "AgentFox.Harness";

    /// <summary>
    /// Let Harness compact its context window. Off by default — AgentFox's own compaction and
    /// session store remain the system of record for history.
    /// </summary>
    public bool EnableHarnessCompaction { get; set; } = false;

    /// <summary>Optional context-window budget passed to Harness when set.</summary>
    public int? MaxContextWindowTokens { get; set; }

    /// <summary>Cap on tool-loop iterations per request.</summary>
    public int MaxIterationsPerRequest { get; set; } = 25;

    /// <summary>Extra harness-level instructions appended for this profile.</summary>
    public string? Instructions { get; set; }
}
