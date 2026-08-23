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
/// <c>/trading</c> endpoints for monitor run and the alert lifecycle, including the SSE feed.
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
///   <item><description><c>/alerts/bulk</c></description></item>
///   <item><description><c>/alerts/stream</c></description></item>
///   <item><description><c>/alerts/{alertId}/ack</c></description></item>
///   <item><description><c>/alerts/{alertId}/dismiss</c></description></item>
///   <item><description><c>/monitor/run</c></description></item>
/// </list>
/// </summary>
public sealed partial class TradingCoreEndpoints
{
    private static void MapAlertsEndpoints(RouteGroupBuilder trading)
    {
        // Run a pass now rather than waiting for the next tick. Analyst-level because it costs a
        // portal request and can raise alerts.
        trading.MapPost("/monitor/run", async (
            WatchlistMonitorWorker monitor,
            CancellationToken ct) =>
            Results.Ok(await monitor.RunPassAsync("manual", ct)))
            .RequireAuthorization("TradingAnalyst");

        trading.MapPost("/alerts/{alertId}/ack", async (
            string alertId,
            ITradingRepository repository,
            CancellationToken ct) =>
        {
            var ok = await repository.SetAlertStateAsync(alertId, "acknowledged", ct);
            return ok ? Results.Ok(new { alertId, state = "acknowledged" }) : Results.NotFound();
        }).RequireAuthorization("TradingAnalyst");

        trading.MapPost("/alerts/{alertId}/dismiss", async (
            string alertId,
            ITradingRepository repository,
            CancellationToken ct) =>
        {
            var ok = await repository.SetAlertStateAsync(alertId, "dismissed", ct);
            return ok ? Results.Ok(new { alertId, state = "dismissed" }) : Results.NotFound();
        }).RequireAuthorization("TradingAnalyst");

        trading.MapPost("/alerts/bulk", async (
            BulkAlertActionRequest body,
            ITradingRepository repository,
            CancellationToken ct) =>
        {
            var action = (body.Action ?? "").Trim().ToLowerInvariant();
            if (action is not ("acknowledge" or "dismiss"))
                return Results.BadRequest(new
                {
                    error = "invalid_action",
                    message = "Bulk alert action must be acknowledge or dismiss."
                });

            var ids = body.All ? null : body.AlertIds;
            if (!body.All && (ids is null || ids.Count == 0))
                return Results.BadRequest(new
                {
                    error = "no_alerts",
                    message = "Select at least one alert, or set all=true."
                });

            var target = action == "acknowledge" ? "acknowledged" : "dismissed";
            // Mark-read only moves unread alerts. Dismiss is the auditable soft-delete operation and
            // intentionally also hides acknowledged rows.
            var changed = await repository.SetAlertsStateAsync(
                ids, target, action == "acknowledge" ? "new" : null, ct);
            return Results.Ok(new { changed, state = target });
        }).RequireAuthorization("TradingAnalyst");

        // Live alert stream. SSE rather than polling so a level break reaches an open page in seconds.
        // The client reads it with fetch (not EventSource) because the /api group needs the management
        // API key header, which EventSource cannot send — the same reason the host's chat stream does.
        trading.MapGet("/alerts/stream", async (
            HttpContext http,
            AlertBroadcaster broadcaster,
            CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            // Proxies that buffer would defeat the point of a stream.
            http.Response.Headers["X-Accel-Buffering"] = "no";

            // An immediate comment frame so the client knows it is connected even on a quiet market.
            await http.Response.WriteAsync(": connected\n\n", ct);
            await http.Response.Body.FlushAsync(ct);

            try
            {
                await foreach (var alert in broadcaster.SubscribeAsync(ct))
                {
                    await http.Response.WriteAsync(
                        $"data: {SerializeAlertForSse(alert)}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Client navigated away; nothing to report.
            }
        });

    }

    // The REST endpoints use ASP.NET's web JSON defaults (camelCase). Keep the SSE contract identical:
    // serializing with the reflection defaults produces PascalCase properties that the browser client
    // cannot read even though the event itself arrives successfully.
    internal static string SerializeAlertForSse(AlertRecord alert) =>
        JsonSerializer.Serialize(alert, JsonSerializerOptions.Web);
}
