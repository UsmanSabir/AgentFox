using AgentFox.Plugins.Interfaces;
using AgentFox.Plugins.Research;
using AgentFox.Plugins;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;
using TradingAgent.AhlAnalytics;
using TradingAgent.Analysis;
using TradingAgent.Broker;
using TradingAgent.Models;
using TradingAgent.Config;
using TradingAgent.Feed;
using TradingAgent.Manager;
using TradingAgent.Market;
using TradingAgent.Observability;
using TradingAgent.Persistence;
using TradingAgent.Research;
using TradingAgent.Risk;
using TradingAgent.Reconciliation;
using TradingAgent.Safety;
using TradingAgent.Tools;
using TradingAgent.Trading;
using TradingAgent.Watchlist;

namespace TradingAgent;

/// <summary>
/// <c>/trading</c> endpoints for policy posture, account, and the kill switch.
///
/// <para>
/// One area of the management API. These were a single 1,855-line MapEndpoints method; the
/// split is by area so a route change is reviewable and so an edition adding endpoints does
/// not collide with core edits. Registration order across areas does not matter — endpoint
/// routing matches on template precedence, not on the order routes were mapped.
/// </para>
///
/// <para>Routes here:</para>
/// <list type="bullet">
///   <item><description><c>/account</c></description></item>
///   <item><description><c>/kill-switch</c></description></item>
///   <item><description><c>/order-intents</c></description></item>
///   <item><description><c>/reconciliation/run</c></description></item>
///   <item><description><c>/status</c></description></item>
/// </list>
/// </summary>
public sealed partial class TradingCoreEndpoints
{
    private static void MapStatusEndpoints(RouteGroupBuilder trading)
    {
        trading.MapGet("/status", async (
            ITradingRepository repository,
            TradingPolicyProvider policyProvider,
            IMarketCalendar calendar,
            TradingReconciliationState reconciliation,
            IOptions<TradingAgentOptions> options,
            CancellationToken ct) =>
        {
            var ledger = await repository.GetStatusAsync(ct);
            var policy = policyProvider.Current();
            var market = calendar.GetStatus();
            var brokerState = reconciliation.Current;
            var configured = options.Value;
            var reconciliationFresh = DateTime.UtcNow - brokerState.CheckedUtc
                <= TimeSpan.FromSeconds(Math.Max(10, configured.ReconciliationMaxAgeSeconds));
            var liveMode = policy.ExecutionMode.Equals("ApprovalRequired", StringComparison.OrdinalIgnoreCase)
                || policy.ExecutionMode.Equals("BoundedAuto", StringComparison.OrdinalIgnoreCase);
            return Results.Ok(new
            {
                policy,
                ledger,
                market,
                reconciliation = brokerState,
                killSwitch = policy.KillSwitch,
                tradingStop = policy.TradingStop.ToWire(),
                entriesBlocked = policy.EntriesBlocked,
                reconciliationFresh,
                liveExecutionReady = liveMode
                    && policy.AutoExecute
                    && !policy.KillSwitch
                    && brokerState.Supported
                    && brokerState.Healthy
                    && reconciliationFresh,
                checkedUtc = DateTime.UtcNow
            });
        });

        trading.MapGet("/account", async (
            IBrokerAccountReader accountReader,
            CancellationToken ct) =>
            Results.Ok(await accountReader.ReadAccountAsync(ct)))
            .RequireAuthorization("TradingTrader");

        // Explicitly user-initiated: unlike the passive timer, this may request immediate session
        // establishment, but it still obeys the SAME global login cooldown/backoff as background
        // recovery. It can never create one login per click during a portal outage.
        trading.MapPost("/reconciliation/run", async (
            IActiveSessionEstablisher establisher,
            BrokerReconciliationWorker worker,
            CancellationToken ct) =>
        {
            var sessionEstablished = await establisher.EstablishSessionAsync(ct);
            var snapshot = await worker.RunNowAsync(ct);
            return Results.Ok(new { sessionEstablished, reconciliation = snapshot });
        }).RequireAuthorization("TradingTrader");

        // How this account trades, and whether its owner has confirmed it (plan step 6, owner 2026-11-28). New hosted
        // tenants are provisioned in ApprovalRequired; nobody is switched silently — every account shows a notice until
        // its owner confirms a mode here, and keeping the current one is a valid confirmation.
        trading.MapGet("/mode", (TradingPolicyProvider policy, PluginConfigManager configs) =>
        {
            var current = policy.Current();
            var runtime = configs.GetConfig("trading-agent");
            var confirmedAt = runtime.TryGetValue("modeConfirmedAtUtc", out var at) ? at?.ToString() : null;
            return Results.Ok(new
            {
                executionMode = current.ExecutionMode,
                autoExecute = current.AutoExecute,
                confirmed = !string.IsNullOrWhiteSpace(confirmedAt),
                confirmedAtUtc = confirmedAt,
                recommended = "ApprovalRequired",
                choices = TradingModeChoices.All
            });
        });

        trading.MapPost("/mode", async (
            TradingModeRequest body,
            PluginConfigManager configs,
            IConfiguration configuration,
            ILogger<TradingCoreEndpoints> logger) =>
        {
            var choice = TradingModeChoices.All.FirstOrDefault(c =>
                string.Equals(c.Mode, body.Mode?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (choice is null)
                return Results.BadRequest(new { error = "invalid_mode", message = "Choose ApprovalRequired, BoundedAuto or Paper." });

            // ApprovalRequired with no approval gate configured is refused at STARTUP (TradingSafetyStartupValidator), so
            // accepting it here would leave an agent that does not come back after its next restart.
            if (choice.Mode == "ApprovalRequired")
            {
                var watched = configuration.GetSection("Hitl:RequireApprovalForTools").Get<string[]>() ?? [];
                if (!configuration.GetValue<bool>("Hitl:Enabled")
                    || !new[] { "place_order", "place_orders" }.All(t => watched.Contains(t, StringComparer.OrdinalIgnoreCase)))
                    return Results.BadRequest(new
                    {
                        error = "approvals_not_configured",
                        message = "Approval for each order needs the approval gate switched on for place_order and "
                                  + "place_orders. Ask your administrator, or choose another mode."
                    });
            }

            var saved = await configs.MergeConfigAsync("trading-agent", new Dictionary<string, object?>
            {
                ["executionMode"] = choice.Mode,
                ["autoExecute"] = true,
                ["modeConfirmedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            });
            if (!saved)
                return Results.Json(new { error = "not_saved", message = "The choice could not be saved. Try again." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            logger.LogWarning("[TradingAgent] Trading mode confirmed as {Mode} via web API.", choice.Mode);
            return Results.Ok(new { executionMode = choice.Mode, confirmed = true });
        }).RequireAuthorization("TradingRiskManager");

        // The decision log for rules running in Shadow (RuleRollout): every pass where a shadow rule would have
        // decided differently from the acting one. Read-only; this is what the owner reviews before switching a
        // rule On.
        trading.MapGet("/shadow-decisions", async (
            ITradingRepository repository, int? days, string? rule, CancellationToken ct) =>
        {
            if (repository is not IRuleShadowLog log)
                return Results.Ok(new { supported = false, decisions = Array.Empty<RuleShadowDecision>() });
            var since = DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 7, 1, 90));
            var decisions = await log.GetRuleShadowDecisionsAsync(since, string.IsNullOrWhiteSpace(rule) ? null : rule, ct);
            return Results.Ok(new { supported = true, sinceUtc = since, count = decisions.Count, decisions });
        });

        // Dedicated, no-restart kill switch: flips the runtime policy overlay (same store the
        // generic /plugin-config/trading-agent editor writes to) so TradingRiskEngine picks it up
        // on the very next order via TradingPolicyProvider — nothing to restart.
        trading.MapPost("/kill-switch", async (
            KillSwitchRequest body,
            PluginConfigManager configManager,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            TradingStopMode mode;
            if (body.Mode is { Length: > 0 } requested)
            {
                if (TradingStopModes.Parse(requested) is not { } parsed)
                    return Results.BadRequest(new
                    {
                        error = "invalid_mode",
                        message = "Mode must be Off, EntriesOff or FullStop."
                    });
                mode = parsed;
            }
            else
            {
                // An older caller that knows only the switch keeps exactly the behaviour it always had.
                mode = body.Active ? TradingStopMode.FullStop : TradingStopMode.Off;
            }

            // Both keys, always together: tradingStop is authoritative, and killSwitch is kept equal to
            // "FullStop" so anything that still reads the legacy key reads the same stop.
            await configManager.MergeConfigAsync("trading-agent", new Dictionary<string, object?>
            {
                ["tradingStop"] = mode.ToWire(),
                ["killSwitch"] = mode == TradingStopMode.FullStop
            });
            logger.LogWarning("[TradingAgent] Trading stop set to {Mode} via web API. Reason: {Reason}",
                mode.ToWire(), body.Reason ?? "(none given)");
            return Results.Ok(new
            {
                killSwitch = mode == TradingStopMode.FullStop,
                tradingStop = mode.ToWire()
            });
        }).RequireAuthorization("ManagementAdministrator");

        // The dashboard speaks in outcomes ("book profit", "sell if it drops") rather than leaking
        // broker vocabulary into every button. This registry is the single contract for those choices.
        trading.MapGet("/order-intents", (IRuntimePluginOptions<AhkConfig> brokerConfig, HttpContext http) =>
            Results.Ok(new
            {
                intents = OrderIntentRegistry.All,
                capabilities = new
                {
                    marketOrdersEnabled = brokerConfig.Current.AllowMarketOrders,
                    brokerOrderTypes = new[] { "LIMIT", "MARKET", "STOPLOSS" },
                    // Rule is listed only where something can evaluate one; elsewhere it is refused at arm.
                    conditionalTriggerTypes = Enum.GetNames<ArmedTriggerKind>()
                        .Where(kind => kind != nameof(ArmedTriggerKind.Rule)
                                       || http.RequestServices.GetService<IArmedOrderConditionEvaluator>() is not null)
                        .ToArray()
                }
            }));

    }
}
