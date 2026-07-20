using AgentFox.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentFox.Harness;

/// <summary>
/// The single place AgentFox touches the preview HarnessAgent API (<c>AsHarnessAgent</c> and
/// <c>HarnessAgentOptions</c>). Everything else in the codebase works against the returned
/// <see cref="AIAgent"/> abstraction, so preview churn stays inside this file.
///
/// Safety contract (roadmap Phase 0):
///  - Disabled by default; <see cref="Create"/> throws unless Harness:Enabled is true.
///  - Every tool is bridged through <see cref="AgentBuilder.CreateGatewayTools"/>, so the plan
///    gate, HITL approval, plugin lifecycle hooks, and experience learning all still apply.
///  - File access, file memory, web search, skill discovery, shell, and background agents are
///    hard-disabled regardless of profile until a later phase bridges them deliberately.
///  - Harness tool approval stays in auto mode: AgentFox's own gate inside the tool gateway is
///    the only approval authority, so no alternate approval path exists.
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

        // MAAI001: HarnessAgent is an evaluation-only preview API. Containing its use is this
        // file's whole purpose (see class doc); the roadmap's version policy governs upgrades.
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
            DisableFileMemory = true,
            DisableFileAccess = true,
            DisableWebSearch = true,
            DisableAgentSkillsProvider = true,

            // Planning UX only; AgentFox PlanState remains the enforcement source.
            DisableTodoProvider = !profile.EnableTodoAndModes,
            DisableAgentModeProvider = !profile.EnableTodoAndModes,

            DisableOpenTelemetry = !profile.EnableOpenTelemetry,
            OpenTelemetrySourceName = profile.OpenTelemetrySourceName,
        };

        return chatClient.AsHarnessAgent(harnessOptions, _loggerFactory);
#pragma warning restore MAAI001
    }
}
