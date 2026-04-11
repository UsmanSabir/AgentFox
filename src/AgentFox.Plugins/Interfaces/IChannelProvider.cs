using Microsoft.Extensions.Logging;

namespace AgentFox.Plugins.Interfaces;

/// <summary>
/// Implemented by a plugin to expose one or more channel types to the core framework.
/// <para>
/// Register an implementation via <see cref="IPluginContext.RegisterChannelProvider"/> inside
/// <see cref="IAgentAwareModule.OnAgentReadyAsync"/>, or call <c>ChannelFactory.Register</c>
/// directly in <see cref="IAppModule.StartAsync"/> if you need providers available before the
/// agent is built (e.g. so they appear in appsettings.json channel loading).
/// </para>
/// </summary>
public interface IChannelProvider
{
    /// <summary>
    /// The channel type name(s) this provider handles (lower-case, e.g. "telegram", "discord").
    /// These values populate the <c>manage_channel</c> tool's enum and are matched
    /// case-insensitively when loading channels from configuration.
    /// </summary>
    IReadOnlyList<string> SupportedTypes { get; }

    /// <summary>
    /// Create a channel instance from a flat string config dictionary.
    /// Returns <c>(channel, null)</c> on success or <c>(null, errorMessage)</c> on failure.
    /// </summary>
    /// <param name="type">Lower-case channel type, e.g. "telegram".</param>
    /// <param name="config">Flat key/value pairs from appsettings or the manage_channel tool.</param>
    /// <param name="logger">Optional logger; the factory may use it for diagnostic output.</param>
    (IChannel? Channel, string? Error) Create(
        string type,
        Dictionary<string, string> config,
        ILogger? logger = null);

    /// <summary>
    /// Returns the config fields required to create a channel of the given type.
    /// Keys are field names; values are short human-readable descriptions shown to the LLM.
    /// Returns <c>null</c> for unknown types.
    /// </summary>
    Dictionary<string, string>? GetRequiredConfig(string type);
}
