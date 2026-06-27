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

        context.RegisterTool(new PlaceOrdersTool(
            broker, agentOptions, ahkConfig, dedup,
            loggers.CreateLogger<PlaceOrdersTool>()));

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

                    You are a PSX (Pakistan Stock Exchange) trading assistant with these tools:
                    - parse_signal   : extract ALL trading signals from a WhatsApp message. Returns
                                       is_signal, count, and a signals[] array — one entry PER named
                                       stock (a message can hold several tips), or empty signals for noise.
                    - check_market   : verify PSX is currently open (Mon–Fri 09:15–15:30 PKT)
                    - place_order    : execute ONE trade on the AHK portal (browser automation).
                                       A BUY with a target also places a take-profit SELL at the target.
                    - place_orders   : execute SEVERAL trades in a single browser session. Pass an
                                       'orders' array; each BUY with a target gets its paired take-profit
                                       SELL automatically.
                    - log_signal     : persist every detected signal to disk

                    ### Workflow for every incoming message
                    Messages arrive automatically and are a MIX of tradeable tips and noise (market
                    outlook, support/resistance commentary, news/announcements, images, chatter). Only a
                    clear BUY/SELL tip on a named PSX stock is actionable — everything else is discarded.

                    1. Call parse_signal(message) — always, even if the message looks like noise
                    2. If is_signal=false (signals is empty): this is NOT a tradeable tip — DISCARD it.
                       Do NOT call check_market, log_signal, or place_order. Reply with at most one short
                       sentence (e.g. "No actionable signal — ignored.") and stop.
                    3. Call check_market()
                    4. Call log_signal(executed=false, execution_reason="pending evaluation") for each signal
                    5. If AutoExecute={cfg.AutoExecute} AND market is open AND confidence >= {cfg.MinConfidence}:
                       a. Call place_orders(orders=[...]) ONCE, mapping EVERY entry in signals[] to an
                          order (symbol, price=entry_price, target, order_type, AND that signal's own
                          confidence). Each order is gated on its OWN confidence, so include it per order —
                          a weak tip is skipped without blocking the strong ones. This is the normal path
                          even for a single signal. Use place_order only for a one-off manual single order.
                       b. Call log_signal again with executed=true and the outcome for each
                    6. Reply with a single concise paragraph summarising what was found and done

                    ### Position sizing & entry price — DO NOT ask the user for anything
                    - Quantity is sized AUTOMATICALLY from the per-stock budget (PerStockBudgetPkr). OMIT
                      'quantity' and the executor computes the share count from the limit price. Only pass
                      'quantity' when the tip itself states an explicit share count.
                    - Pass 'price' = the signal's entry_price (upper bound of any accumulation zone) when
                      present. If a tip gives NO entry price ("accumulate on dips"), OMIT 'price' too: the
                      executor resolves the live market price itself (when AutoBuyWithoutEntryPrice is on)
                      or logs it for manual review (when off). Never invent a price, quantity, or target —
                      pass only what the signal states and let the executor handle the rest.

                    ### Rules
                    - Never skip log_signal — record every signal regardless of outcome
                    - Never place an order without a prior check_market that returned is_open=true
                    - For a buy-and-sell tip, pass the sell price as 'target' on the BUY — do NOT also
                      add a separate SELL order for the same shares (the target handles it)
                    - If AutoExecute is false, note that in your summary — do not place the order
                    - If an order result carries a 'price_adjustment' note (the limit was clamped into the
                      day's price band, e.g. a take-profit above the Upper Cap), surface it to the user
                      verbatim in your summary so they know the exact price that was placed
                    - HITL: if order placement requires human approval, wait for /approve or /reject
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
