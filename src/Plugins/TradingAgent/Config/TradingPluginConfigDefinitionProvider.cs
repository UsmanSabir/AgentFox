using AgentFox.Plugins;
using Microsoft.Extensions.Options;

namespace TradingAgent.Config;

/// <summary>
/// Exposes the trading settings that the runtime overlay actually consumes, plus the AHK broker
/// connection (portal URL and credentials) as a separate definition. Credential values are declared
/// <c>Sensitive</c>: the web layer masks them on read and the config store encrypts them at rest,
/// and changes are applied by <see cref="TradingAgent.Broker.AhkBroker"/> on the next browser
/// session (see the credential-change listener in <see cref="TradingAgentModule"/>).
/// </summary>
public sealed class TradingPluginConfigDefinitionProvider : IPluginConfigDefinitionProvider
{
    /// <summary>Plugin-config name holding the AHK broker connection overlay.</summary>
    public const string BrokerPluginName = "trading-agent-broker";

    private readonly IOptions<TradingAgentOptions> _options;
    private readonly IOptions<AhkConfig> _ahk;

    public TradingPluginConfigDefinitionProvider(
        IOptions<TradingAgentOptions> options,
        IOptions<AhkConfig> ahk)
    {
        _options = options;
        _ahk = ahk;
    }

    public IEnumerable<PluginConfigDefinition> GetDefinitions()
    {
        var options = _options.Value;
        yield return new PluginConfigDefinition
        {
            PluginName = "trading-agent",
            DisplayName = "PSX Trading Agent",
            Description = "Runtime policy overrides used by the isolated PSX specialist. Credentials, "
                        + "risk limits, approvals, and the kill switch remain in the dedicated Trading page.",
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
                },
                new PluginConfigFieldDefinition
                {
                    Key = "killSwitch",
                    Label = "Kill switch",
                    Description = "Emergency stop — blocks every order immediately, independent of AutoExecute, " +
                                  "execution mode, and the LLM. Takes effect on the next order without a restart.",
                    Type = "boolean",
                    DefaultValue = options.KillSwitch
                }
            ]
        };

        var ahk = _ahk.Value;
        yield return new PluginConfigDefinition
        {
            PluginName = BrokerPluginName,
            DisplayName = "AHK Broker Connection",
            Description = "Portal URL and login credentials for the AHK browser broker. " +
                          "Changes take effect on the next broker session; leave a field blank " +
                          "to keep using the value from appsettings.",
            Fields =
            [
                new PluginConfigFieldDefinition
                {
                    Key = "portalUrl",
                    Label = "Portal URL",
                    Description = "AHK trading portal address.",
                    DefaultValue = ahk.PortalUrl
                },
                new PluginConfigFieldDefinition
                {
                    Key = "username",
                    Label = "Username",
                    Description = "AHK portal login user id.",
                    DefaultValue = ahk.Username
                },
                new PluginConfigFieldDefinition
                {
                    Key = "password",
                    Label = "Password",
                    Description = "AHK portal login password. Stored encrypted; shown masked.",
                    Sensitive = true,
                    // Never expose the appsettings secret itself; the mask only signals "configured".
                    DefaultValue = string.IsNullOrEmpty(ahk.Password) ? null : PluginConfigSecrets.Mask
                },
                new PluginConfigFieldDefinition
                {
                    Key = "tradingPin",
                    Label = "Trading PIN",
                    Description = "PIN entered on the order form. Stored encrypted; shown masked.",
                    Sensitive = true,
                    DefaultValue = string.IsNullOrEmpty(ahk.TradingPin) ? null : PluginConfigSecrets.Mask
                }
            ]
        };
    }
}
