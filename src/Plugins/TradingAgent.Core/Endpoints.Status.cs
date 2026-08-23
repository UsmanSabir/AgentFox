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
            AhkPortalClient brokerPortal,
            BrokerReconciliationWorker worker,
            CancellationToken ct) =>
        {
            var sessionEstablished = await brokerPortal.EnsureSessionAsync(ct);
            var snapshot = await worker.RunNowAsync(ct);
            return Results.Ok(new { sessionEstablished, reconciliation = snapshot });
        }).RequireAuthorization("TradingTrader");

        // Dedicated, no-restart kill switch: flips the runtime policy overlay (same store the
        // generic /plugin-config/trading-agent editor writes to) so TradingRiskEngine picks it up
        // on the very next order via TradingPolicyProvider — nothing to restart.
        trading.MapPost("/kill-switch", async (
            KillSwitchRequest body,
            PluginConfigManager configManager,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            await configManager.MergeConfigAsync("trading-agent", new Dictionary<string, object?>
            {
                ["killSwitch"] = body.Active
            });
            logger.LogWarning("[TradingAgent] Kill switch {State} via web API. Reason: {Reason}",
                body.Active ? "ACTIVATED" : "cleared", body.Reason ?? "(none given)");
            return Results.Ok(new { killSwitch = body.Active });
        }).RequireAuthorization("ManagementAdministrator");

        // The dashboard speaks in outcomes ("book profit", "sell if it drops") rather than leaking
        // broker vocabulary into every button. This registry is the single contract for those choices.
        trading.MapGet("/order-intents", (IRuntimePluginOptions<AhkConfig> brokerConfig) =>
            Results.Ok(new
            {
                intents = OrderIntentRegistry.All,
                capabilities = new
                {
                    marketOrdersEnabled = brokerConfig.Current.AllowMarketOrders,
                    brokerOrderTypes = new[] { "LIMIT", "MARKET", "STOPLOSS" },
                    conditionalTriggerTypes = Enum.GetNames<ArmedTriggerKind>()
                }
            }));

    }
}
