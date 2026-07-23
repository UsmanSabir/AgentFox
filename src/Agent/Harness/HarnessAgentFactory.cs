using AgentFox.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentFox.Harness;

/// <summary>
/// The single place AgentFox touches the HarnessAgent API (<c>AsHarnessAgent</c> and
/// <c>HarnessAgentOptions</c>). Everything else in the codebase works against the returned
/// <see cref="AIAgent"/> abstraction, so churn in this API stays inside this file.
///
/// As of Microsoft.Agents.AI.Harness 1.15.0 the package is no longer prerelease and the
/// HarnessAgent type itself is no longer <c>[Experimental]</c> — but a subset of
/// <c>HarnessAgentOptions</c> members still is (compaction, context/output budgets, loop
/// evaluators, the file stores, and background agents), which is why MAAI001 is still
/// suppressed below rather than removed.
///
/// Safety contract (roadmap Phase 0):
///  - Disabled by default; <see cref="Create"/> throws unless Harness:Enabled is true.
///  - Every tool is bridged through <see cref="AgentBuilder.CreateGatewayTools"/>, so the plan
///    gate, HITL approval, plugin lifecycle hooks, and experience learning all still apply.
///  - File memory, file access, web search, skill discovery, and background agents stay off
///    regardless of profile until a later phase bridges them deliberately. Note the shapes
///    differ: file memory / web search / skills are opt-OUT (explicit Disable* below), while
///    file access and background agents are opt-IN and stay off simply by not being set.
///  - Harness tool approval stays in auto mode: AgentFox's own gate inside the tool gateway is
///    the only approval authority, so no alternate approval path exists. The 1.15.0 approval
///    hardening (approval-response binding, approval-not-required bypassing) is left at its
///    secure default so a surfaced approval binds to exactly the call it was raised for.
///
/// Shell execution is not mentioned here because it no longer exists on this surface: 1.15.0
/// removed the ShellExecutor/ShellTool*/DisableShellToolApproval members, and the separate
/// Microsoft.Agents.AI.Tools.Shell package has no 1.15.0 release. There is nothing to disable.
/// </summary>
public sealed class HarnessAgentFactory
{
    private readonly HarnessOptions _options;
    private readonly ILoggerFactory? _loggerFactory;

    public HarnessAgentFactory(IOptions<HarnessOptions> options, ILoggerFactory? loggerFactory = null)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
    }

    public bool IsEnabled => _options.Enabled;

    /// <summary>
    /// Creates a HarnessAgent for the named profile (or the configured default), exposing only
    /// tools bridged through the AgentFox gateway. Throws when the feature flag is off or the
    /// profile is unknown — never silently falls back to a more permissive configuration.
    /// </summary>
    public AIAgent Create(IChatClient chatClient, AgentBuilder toolGateway, string? profileName = null)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException(
                "HarnessAgent is disabled. Set Harness:Enabled=true and configure a profile to opt in.");

        var name = profileName ?? _options.DefaultProfile;
        if (!_options.Profiles.TryGetValue(name, out var profile))
            throw new InvalidOperationException(
                $"Unknown Harness profile '{name}'. Configured profiles: {string.Join(", ", _options.Profiles.Keys)}.");

        var tools = toolGateway.CreateGatewayTools().ToList();

        // MAAI001 is still required at 1.15.0 — not for HarnessAgent/AsHarnessAgent (both went
        // stable) but for the individual experimental options set below: DisableCompaction and
        // MaxContextWindowTokens. Containing that exposure is this file's whole purpose (see
        // class doc); the roadmap's version policy governs upgrades.
#pragma warning disable MAAI001
        var harnessOptions = new HarnessAgentOptions
        {
            Name = $"AgentFox-Harness-{name}",
            Description = $"AgentFox HarnessAgent profile '{name}' (gateway-bridged tools only)",
            ChatOptions = new ChatOptions { Tools = tools },
            HarnessInstructions = string.IsNullOrWhiteSpace(profile.Instructions) ? null : profile.Instructions,

            // AgentFox's session store stays the system of record; Harness compaction is a
            // context-window optimization that must be opted into per profile.
            DisableCompaction = !profile.EnableHarnessCompaction,
            MaxContextWindowTokens = profile.MaxContextWindowTokens,
            MaximumIterationsPerRequest = profile.MaxIterationsPerRequest,

            // Approval authority is AgentFox's gate inside the bridged tool gateway. Harness-level
            // approval stays in auto mode so no second, divergent approval path exists.
            DisableToolAutoApproval = false,

            // Least privilege: no Harness-native data or capability provider is active until a
            // later roadmap phase bridges it through AgentFox policy explicitly.
            //
            // File access has no Disable* switch as of Harness 1.15.0 — it inverted to opt-in:
            // leaving FileAccessStore null means no FileAccessProvider is added and the agent gets
            // no file tools. Do NOT set FileAccessStore/FileAccessProviderOptions here; that would
            // hand the model file tools that bypass the AgentFox gateway.
            DisableFileMemory = true,
            DisableWebSearch = true,
            DisableAgentSkillsProvider = true,

            // Planning UX only; AgentFox PlanState remains the enforcement source.
            DisableTodoProvider = !profile.EnableTodoAndModes,
            DisableAgentModeProvider = !profile.EnableTodoAndModes,

            DisableOpenTelemetry = !profile.EnableOpenTelemetry,
            OpenTelemetrySourceName = profile.OpenTelemetrySourceName,
        };
#pragma warning restore MAAI001

        // Outside the suppression on purpose: AsHarnessAgent is stable at 1.15.0, so if a future
        // bump re-flags it the warning surfaces here instead of being silently swallowed.
        return chatClient.AsHarnessAgent(harnessOptions, _loggerFactory);
    }
}
