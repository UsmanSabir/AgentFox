using AgentFox.Plugins.Interfaces;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Broker;
using TradingAgent.Config;
using TradingAgent.Safety;
using TradingAgent.Tools;

namespace TradingAgent;

/// <summary>
/// AgentFox plugin that adds PSX trading agent capabilities.
///
/// Discovered automatically from the plugins/ folder — no changes needed in the main app.
///
/// What it registers:
///   Tools     : parse_signal, check_market, place_order, log_signal
///   Channel   : whatsapp-bridge (via WhatsAppBridgeChannelProvider, auto-discovered)
///   Services  : AhkBroker (singleton), DuplicateSignalFilter (singleton)
///   Prompt    : Injects trading workflow instructions into the agent system prompt
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
///     "PortalUrl":        "https://www.ahktrading.com",
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
public sealed class TradingAgentModule : IAgentAwareModule
{
    private IServiceProvider? _services;

    public string Name => "trading-agent";

    // ── IAppModule ────────────────────────────────────────────────────────────

    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        services.Configure<TradingAgentOptions>(
            config.GetSection($"Plugins:{TradingAgentOptions.SectionName}"));

        services.Configure<AhkConfig>(
            config.GetSection($"Plugins:{AhkConfig.SectionName}"));

        services.AddSingleton<AhkBroker>();

        services.AddSingleton<DuplicateSignalFilter>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<TradingAgentOptions>>().Value;
            return new DuplicateSignalFilter(TimeSpan.FromMinutes(opts.DuplicateWindowMinutes));
        });
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }

    public Task StartAsync(IServiceProvider services)
    {
        _services = services;
        return Task.CompletedTask;
    }

    // ── IAgentAwareModule ─────────────────────────────────────────────────────

    public Task OnAgentReadyAsync(IPluginContext context)
    {
        var chatClient    = _services!.GetRequiredService<IChatClient>();
        var agentOptions  = _services!.GetRequiredService<IOptions<TradingAgentOptions>>();
        var ahkConfig     = _services!.GetRequiredService<IOptions<AhkConfig>>();
        var broker        = _services!.GetRequiredService<AhkBroker>();
        var dedup         = _services!.GetRequiredService<DuplicateSignalFilter>();
        var loggers       = _services!.GetRequiredService<ILoggerFactory>();

        // The browser is launched ON DEMAND by PlaceOrderAsync and torn down once the order finishes
        // (see AhkConfig.CloseBrowserAfterOrder). We deliberately do NOT start it at agent startup, so
        // no Chromium window appears until an order is actually placed.

        // Register the four trading tools
        context.RegisterTool(new ParseSignalTool(
            chatClient,
            loggers.CreateLogger<ParseSignalTool>()));

        context.RegisterTool(new CheckMarketTool());

        context.RegisterTool(new PlaceOrderTool(
            broker, agentOptions, ahkConfig, dedup,
            loggers.CreateLogger<PlaceOrderTool>()));

        context.RegisterTool(new LogSignalTool(
            ahkConfig,
            loggers.CreateLogger<LogSignalTool>()));

        // Inject trading workflow into agent system prompt
        context.ContributeToSystemPrompt(
            contributorId: "trading-agent",
            fragmentProvider: () =>
            {
                var cfg = agentOptions.Value;
                return $"""

                    ## PSX Trading Agent

                    You are a PSX (Pakistan Stock Exchange) trading assistant with 4 tools:
                    - parse_signal   : extract a structured signal from a WhatsApp message
                    - check_market   : verify PSX is currently open (Mon–Fri 09:15–15:30 PKT)
                    - place_order    : execute a trade on the AHK portal (browser automation)
                    - log_signal     : persist every detected signal to disk

                    ### Workflow for every incoming message
                    1. Call parse_signal(message) — always, even if the message looks like noise
                    2. If is_signal=false: state briefly that no signal was detected, then stop
                    3. Call check_market()
                    4. Call log_signal(executed=false, execution_reason="pending evaluation")
                    5. If AutoExecute={cfg.AutoExecute} AND market is open AND confidence >= {cfg.MinConfidence}:
                       a. Call place_order(...) with the extracted fields
                       b. Call log_signal again with executed=true and the outcome
                    6. Reply with a single concise paragraph summarising what was found and done

                    ### Rules
                    - Never skip log_signal — record every signal regardless of outcome
                    - Never call place_order without a prior check_market that returned is_open=true
                    - If AutoExecute is false, note that in your summary — do not place the order
                    - HITL: if place_order requires human approval, wait for /approve or /reject
                    """;
            });

        var logger = loggers.CreateLogger<TradingAgentModule>();
        logger.LogInformation(
            "[TradingAgent] Ready. AutoExecute={Auto} MinConfidence={Min} DupWindow={Dup}min",
            agentOptions.Value.AutoExecute,
            agentOptions.Value.MinConfidence,
            agentOptions.Value.DuplicateWindowMinutes);

        return Task.CompletedTask;
    }
}
