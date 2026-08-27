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
/// <c>/trading</c> endpoints for alert list, monitor status, broker feed, depth, movers.
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
///   <item><description><c>/activity</c></description></item>
///   <item><description><c>/alerts</c></description></item>
///   <item><description><c>/feed/depth</c></description></item>
///   <item><description><c>/feed/depth/focus</c></description></item>
///   <item><description><c>/feed/status</c></description></item>
///   <item><description><c>/monitor/status</c></description></item>
///   <item><description><c>/movers</c></description></item>
///   <item><description><c>/movers/sectors</c></description></item>
/// </list>
/// </summary>
public sealed partial class TradingCoreEndpoints
{
    private static void MapMarketEndpoints(RouteGroupBuilder trading)
    {
        // ── Alerts ────────────────────────────────────────────────────────────
        // What the monitor noticed. Read-only for viewers; acknowledging or dismissing is an analyst
        // action because it changes what the next person sees.

        trading.MapGet("/alerts", async (
            string? symbol,
            string? state,
            int? limit,
            ITradingRepository repository,
            CancellationToken ct) =>
            Results.Ok(await repository.GetAlertsAsync(symbol, state, limit ?? 100, ct)));

        trading.MapGet("/monitor/status", (
            WatchlistMonitorWorker monitor,
            AlertBroadcaster broadcaster) => Results.Ok(new
            {
                monitor.Status.Enabled,
                monitor.Status.MarketOpen,
                monitor.Status.LastPassUtc,
                monitor.Status.LastPassMs,
                monitor.Status.SymbolsCovered,
                monitor.Status.AlertsRaised,
                monitor.Status.AlertsSuppressed,
                // The effective settings it is running with, so "why did it not alert" is answerable
                // from the UI instead of by reading JSON on disk.
                monitor.Status.IntervalSeconds,
                monitor.Status.ConfirmPasses,
                monitor.Status.Trigger,
                monitor.Status.Warnings,
                monitor.Status.Message,
                liveSubscribers = broadcaster.SubscriberCount
            }));

        // Live broker-feed health. Every failure mode of the feed is silent — a lost subscription, a
        // dead session and a quiet market all look like "no quotes" — so this is the surface that
        // tells them apart without reading Debug logs.
        trading.MapGet("/feed/status", (AhkFeedWorker feed) => Results.Ok(feed.GetStatus()));

        // Depth for the currently followed symbol. GET never changes the subscription. The explicit
        // POST below delegates to the same get_market_depth tool used by chat, which keeps one
        // subscription/action path and gives operators a deterministic diagnostic surface.
        trading.MapGet("/feed/depth", (AhkDepthBook depth, AhkFeedWorker feed, string? symbol) =>
        {
            var target = (symbol ?? depth.SubscribedSymbol)?.Trim().ToUpperInvariant();
            if (target is null) return Results.Ok(new { subscribed = (string?)null, rows = 0 });

            var entry = depth.Get("REG", target);
            return Results.Ok(new
            {
                symbol = target,
                subscribed = depth.SubscribedSymbol,
                marketStatus = feed.MarketStatus,
                bestBid = entry?.BestBid,
                bestAsk = entry?.BestAsk,
                spread = entry?.Spread,
                totalBidVolume = entry?.TotalBidVolume,
                totalAskVolume = entry?.TotalAskVolume,
                imbalance = entry?.Imbalance,
                levels = entry?.Levels,
                orders = entry?.Orders,
                levelsAtUtc = entry?.LevelsAtUtc,
                ordersAtUtc = entry?.OrdersAtUtc,
                totalRowsEverSeen = depth.RowsSeen
            });
        });

        trading.MapPost("/feed/depth/focus", async (
            MarketDepthTool tool,
            string symbol,
            int? waitSeconds) =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["symbol"] = symbol,
                ["wait_seconds"] = waitSeconds ?? 6
            });

            return result.Success
                ? Results.Content(result.Output, "application/json")
                : Results.BadRequest(new { error = result.Error ?? result.Output });
        }).RequireAuthorization("TradingAnalyst");

        // ── Market movers (AHL analytics) ──────────────────────────────────────
        // One snapshot fetch backs every screen, so the dashboard can poll a few of these without
        // multiplying upstream traffic — AhlAnalyticsClient caches the snapshot for its configured TTL.
        trading.MapGet("/movers", async (
            AhlAnalyticsClient analytics,
            IAnalyticsSsoUrlProvider ssoProvider,
            string? screen,
            string? index,
            string? sectorCode,
            int? limit,
            decimal? minTurnover,
            decimal? minPrice,
            CancellationToken ct) =>
        {
            if (!analytics.Enabled)
                return Results.Ok(new { enabled = false, rows = Array.Empty<object>() });

            var parsed = AhlMovers.ParseScreen(screen ?? "gainers");
            if (parsed is null)
                return Results.BadRequest(new { error = $"Unknown screen '{screen}'.", valid = AhlMovers.ScreenNames });

            // A passive dashboard must never CREATE a broker session. Once AhkFeed already has one,
            // however, the SSO hop is just an authenticated GET and cannot cost another login. This
            // closes the gap where the screen claimed "no portal session" beside a healthy live feed.
            var snapshot = await analytics.GetMarketSnapshotAsync(
                allowHandshake: ssoProvider.CanHandshakeSafely, ct: ct);
            if (snapshot is null)
            {
                // Report WHY. "Could not be reached" covers no-session, a throttle and a rejected
                // POST, and those need different responses from the operator — hasToken alone
                // distinguishes the first from the rest.
                return Results.Ok(new
                {
                    enabled = true,
                    available = false,
                    hasToken = analytics.HasToken,
                    brokerSessionAvailable = ssoProvider.CanHandshakeSafely,
                    handshakeCoolingDown = analytics.HandshakeInCooldown,
                    error = analytics.LastError,
                    rows = Array.Empty<object>()
                });
            }

            var filter = new AhlMovers.Filter(index, sectorCode, minTurnover, null, minPrice);
            return Results.Ok(new
            {
                enabled = true,
                available = true,
                screen = parsed.Value.ToString(),
                marketState = snapshot.MarketState,
                asOf = snapshot.LastUpdate,
                breadth = AhlMovers.MarketBreadth(snapshot),
                rows = AhlMovers.Run(snapshot, parsed.Value, limit ?? 15, filter)
            });
        });

        // Sector rotation for the same session, from the same cached snapshot.
        trading.MapGet("/movers/sectors", async (
            AhlAnalyticsClient analytics,
            IAnalyticsSsoUrlProvider ssoProvider,
            string? index,
            CancellationToken ct) =>
        {
            if (!analytics.Enabled)
                return Results.Ok(new { enabled = false, sectors = Array.Empty<object>() });

            var snapshot = await analytics.GetMarketSnapshotAsync(
                allowHandshake: ssoProvider.CanHandshakeSafely, ct: ct);
            if (snapshot is null)
                return Results.Ok(new
                {
                    enabled = true,
                    available = false,
                    hasToken = analytics.HasToken,
                    brokerSessionAvailable = ssoProvider.CanHandshakeSafely,
                    handshakeCoolingDown = analytics.HandshakeInCooldown,
                    error = analytics.LastError,
                    sectors = Array.Empty<object>()
                });

            return Results.Ok(new
            {
                enabled = true,
                available = true,
                marketState = snapshot.MarketState,
                asOf = snapshot.LastUpdate,
                sectors = AhlMovers.SectorRotation(snapshot, new AhlMovers.Filter(index))
            });
        });

        // What the agent is doing right now, and what it just did.
        //
        // The status endpoints above each answer for ONE subsystem and answer in state ("healthy",
        // "12 symbols"). This answers in events, across all of them, in order — which is the only
        // form that says whether the thing that just happened on screen (a browser window opening,
        // an order not appearing) was this system's doing and why.
        //
        // The counts always describe the whole retained window regardless of `limit`, so a collapsed
        // panel can show an issue badge while asking for a single entry. `afterSeq` is offered for a
        // caller that only wants what is new — but note the log folds a repeated activity into its
        // existing entry, so a live view should read the whole window rather than merge deltas.
        trading.MapGet("/activity", (
            TradingActivityLog activity,
            AhkBroker broker,
            AhkFeedWorker feed,
            WatchlistMonitorWorker monitor,
            IMarketCalendar calendar,
            long? afterSeq,
            int? limit) =>
        {
            var (warnings, errors) = activity.IssueCounts();
            var market = calendar.GetStatus();

            return Results.Ok(new
            {
                lastSeq = activity.LastSeq,
                warnings,
                errors,
                retentionMinutes = (int)TradingActivityLog.Retention.TotalMinutes,
                now = new
                {
                    // The single most useful "right now" fact: whether a browser window on screen is
                    // this system driving the portal.
                    browserBusy  = broker.BrowserHoldsTradingScreen,
                    marketOpen   = market.IsOpen,
                    marketReason = market.Reason,
                    feedHealthy  = feed.GetStatus().Healthy,
                    monitorLastPassUtc = monitor.Status.LastPassUtc
                },
                activities = activity.Snapshot(afterSeq ?? 0, limit ?? TradingActivityLog.Capacity)
            });
        });

    }
}
