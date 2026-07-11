using AgentFox.Plugins;
using Microsoft.Extensions.Options;

namespace TradingAgent.Config;

/// <summary>
/// Exposes only the trading settings that the runtime overlay actually consumes. Execution
/// controls and credentials remain on the dedicated, audited trading management surface.
/// </summary>
public sealed class TradingPluginConfigDefinitionProvider : IPluginConfigDefinitionProvider
{
    private readonly IOptions<TradingAgentOptions> _options;

    public TradingPluginConfigDefinitionProvider(IOptions<TradingAgentOptions> options) =>
        _options = options;

    public IEnumerable<PluginConfigDefinition> GetDefinitions()
    {
        var options = _options.Value;
        yield return new PluginConfigDefinition
        {
            PluginName = "trading-agent",
            DisplayName = "PSX Trading Agent",
            Description = "Runtime policy overrides used by the isolated PSX specialist.",
            Fields =
            [
                new PluginConfigFieldDefinition
                {
                    Key = "executionMode",
                    Label = "Execution mode",
                    Description = "Controls whether proposals can progress to deterministic execution.",
                    Type = "select",
                    DefaultValue = options.ExecutionMode,
                    Options = ["Disabled", "Paper", "Shadow", "ApprovalRequired", "BoundedAuto"]
                },
                new PluginConfigFieldDefinition
                {
                    Key = "autoExecute",
                    Label = "Auto execute",
                    Description = "Allows automatic execution only when the selected mode and safety policy permit it.",
                    Type = "boolean",
                    DefaultValue = options.AutoExecute
                },
                new PluginConfigFieldDefinition
                {
                    Key = "minConfidence",
                    Label = "Minimum confidence",
                    Description = "Minimum parsed signal confidence accepted by the runtime policy.",
                    Type = "select",
                    DefaultValue = options.MinConfidence,
                    Options = ["LOW", "MEDIUM", "HIGH"]
                }
            ]
        };
    }
}
