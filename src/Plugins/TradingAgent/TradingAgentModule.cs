using AgentFox.Plugins;
using AgentFox.Plugins.Interfaces;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TradingAgent;


/// <summary>
/// AgentFox plugin that adds PSX trading agent capabilities.
///
/// Discovered automatically from the plugins/ folder — no changes needed in the main app.
///
/// What it registers:
///   Agent     : isolated trading-agent specialist with a restricted tool allowlist
///   Tools     : parse/check/log/proposal/status/portfolio/research plus private compatibility execution adapters
///   Channel   : whatsapp-bridge (via WhatsAppBridgeChannelProvider, auto-discovered)
///   Services  : deterministic TradingManager, SQLite ledger, risk engine, market calendar, AhkBroker
///   Prompt    : injects only a routing hint into the main agent
///
/// Minimum appsettings.json additions:
/// <code>
/// "Modules": "cli,web,webhook",
/// "Plugins": {
///   "TradingAgent": {
///     "AutoExecute":            false,
///     "MinConfidence":          "HIGH",
///     "ParserModelKey":         "CheapModel",
///     "DuplicateWindowMinutes": 60
///   },
///   "Ahk": {
///     "PortalUrl":        "https://web.ahletrade.com/",
///     "Username":         "",
///     "Password":         "",
///     "TradingPin":       "",
///     "DefaultQty":       100,
///     "MaxOrderValuePkr": 50000,
///     "SessionDir":       "session_ahk",
///     "LogDir":           "logs/trading"
///   }
/// },
/// "Hitl": {
///   "Enabled": true,
///   "RequireApprovalForTools": ["place_order"]
/// },
/// "Channels": [
///   {
///     "Type":        "whatsapp-bridge",
///     "Enabled":     true,
///     "CallbackUrl": "",
///     "GroupFilter": "PSX Signals"
///   }
/// ]
/// </code>
/// </summary>
public sealed class TradingAgentModule : IAgentAwareModule, IPluginUiContributor
{
    private IServiceProvider? _services;

    public string Name => "trading-agent";

    // Every member below delegates to TradingAgent.Core. This assembly is the ENTRY plugin: it is
    // the DLL the host's loader discovers (it is the one with a .deps.json) and the only assembly a
    // channel provider can live in, because the host gates IChannelProvider registration on the
    // provider type's assembly matching an enabled module's assembly. Keeping it a shim is what lets
    // a separately licensed premium entry compose the identical engine — see EDITION_SPLIT_PLAN.md.

    public IEnumerable<PluginUiPage> GetPages() => TradingAgentRuntime.GetCorePages();

    public void RegisterServices(IServiceCollection services, IConfiguration config) =>
        TradingAgentRuntime.AddCore(services, config);

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        TradingCoreEndpoints.MapCoreEndpoints(endpoints);

    public Task StartAsync(IServiceProvider services)
    {
        _services = services;
        TradingAgentRuntime.Start(services);
        return Task.CompletedTask;
    }

    public Task OnAgentReadyAsync(IPluginContext context)
    {
        TradingAgentRuntime.RegisterCoreTools(context, _services!);
        TradingAgentRuntime.RegisterSpecialist(context, _services!);
        return Task.CompletedTask;
    }
}
