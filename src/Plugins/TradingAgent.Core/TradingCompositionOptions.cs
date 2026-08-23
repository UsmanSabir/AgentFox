using AgentFox.Plugins.Interfaces;

namespace TradingAgent;

/// <summary>
/// Everything an entry plugin may add to the trading engine without forking its wiring.
///
/// <para>
/// This is the whole extension surface, and it is deliberately a small typed record rather than a
/// capability registry keyed by strings: an edition that adds a tool the core does not know about
/// should fail to compile when the contract changes, not fail silently at run time on a mistyped
/// key. Everything absent from this record is intentionally not extensible yet — add a field when a
/// real edition needs it, so each seam arrives with a caller that justifies it.
/// </para>
///
/// <para>
/// <see cref="Community"/> is all-empty, so the community edition's behavior is exactly what it was
/// before these seams existed. That is the property to preserve when adding a field: a new field's
/// default must be "what the community edition already did".
/// </para>
///
/// <para>
/// Two things deliberately do NOT live here. UI pages: an entry plugin implements
/// <c>IPluginUiContributor.GetPages</c> itself and simply chooses whether to return
/// <see cref="TradingAgentRuntime.GetCorePages"/>, so no flag is needed. Endpoints: an edition adds
/// routes freely, but must not re-map a core route — two endpoints with the same template, method,
/// and precedence make the request ambiguous and routing throws at request time. Premium behavior on
/// an existing route arrives through a provider the handler already consults, not by shadowing it.
/// </para>
/// </summary>
public sealed record TradingCompositionOptions
{
    /// <summary>The community edition: adds nothing, changes nothing.</summary>
    public static TradingCompositionOptions Community { get; } = new();

    /// <summary>
    /// Which edition composed the engine, for logs and <c>/trading/status</c>. Both editions
    /// register the module under the name <c>trading-agent</c> so existing <c>Modules</c> /
    /// <c>DisabledModules</c> config and saved plugin-config overlays keep working — which means the
    /// module name cannot tell an operator which edition is running, and this is what does.
    /// </summary>
    public string EditionName { get; init; } = "community";

    /// <summary>
    /// Extra tools to register alongside the core set. They go through the same registration loop,
    /// so they land in the same audit name set and the same pre/post/error hooks. An edition must
    /// not call <c>IPluginContext.RegisterAgentTool</c> itself: tools registered outside this loop
    /// are missing from the audit filter and their executions are never recorded.
    /// </summary>
    public IReadOnlyList<ITool> AdditionalTools { get; init; } = [];

    /// <summary>
    /// Tool names to append to the specialist's allowlist. Names of <see cref="AdditionalTools"/>
    /// are not added automatically: registering a tool and granting the specialist permission to
    /// call it are separate decisions, and an execution-capable tool should have to say so.
    /// </summary>
    public IReadOnlyList<string> AdditionalSpecialistToolNames { get; init; } = [];

    /// <summary>
    /// Tool names that should also reach the PRIMARY tool registry — the ordinary chat link — rather
    /// than the specialist alone. Keep this to read-only discovery tools; execution and
    /// order-management tools stay specialist-only, which is the boundary the core set observes.
    /// </summary>
    public IReadOnlyList<string> AdditionalPrimaryReadToolNames { get; init; } = [];

    /// <summary>
    /// Appended to the specialist's system prompt, never a replacement for it. The core prompt
    /// carries the allowed-symbols list and the rule that a non-tradable candidate must not be
    /// presented as actionable; replacing it would silently drop both.
    /// </summary>
    public string? SpecialistPromptAppendix { get; init; }
}
