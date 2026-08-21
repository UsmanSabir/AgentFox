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
/// <c>/trading</c> endpoints for the durable record: executions, events, reconciliation runs.
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
///   <item><description><c>/events</c></description></item>
///   <item><description><c>/executions</c></description></item>
///   <item><description><c>/executions/{executionId}/resolve</c></description></item>
///   <item><description><c>/reconciliation</c></description></item>
/// </list>
/// </summary>
public sealed partial class TradingCoreEndpoints
{
    private static void MapLedgerEndpoints(RouteGroupBuilder trading)
    {
        trading.MapGet("/executions", async (
            int? limit,
            ITradingRepository repository,
            CancellationToken ct) =>
            Results.Ok(await repository.GetExecutionsAsync(limit ?? 100, ct)));

        trading.MapPost("/executions/{executionId}/resolve", async (
            string executionId,
            ResolveUnknownExecutionRequest body,
            HttpContext http,
            ITradingRepository repository,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            var resolution = body.Resolution?.Trim().ToLowerInvariant();
            if (resolution is not ("placed" or "not_placed"))
                return Results.BadRequest(new
                {
                    error = "invalid_resolution",
                    message = "Choose placed or not_placed after checking the broker's own order book/activity."
                });

            var note = body.Note?.Trim();
            if (string.IsNullOrWhiteSpace(note))
                return Results.BadRequest(new
                {
                    error = "resolution_note_required",
                    message = "Record what you checked at the broker before resolving an unknown outcome."
                });

            var resolvedBy = http.User.Identity?.Name ?? "operator";
            var resolvedUtc = DateTime.UtcNow;
            var payload = JsonSerializer.Serialize(new
            {
                resolution,
                note,
                resolvedBy,
                resolvedUtc,
                automaticRetry = false
            }, JsonSerializerOptions.Web);

            if (!await repository.ResolveUnknownExecutionAsync(executionId, resolution, payload, ct))
                return Results.Conflict(new
                {
                    error = "not_unknown",
                    message = "This execution does not exist or is no longer unresolved. Refresh before acting again."
                });

            logger.LogWarning(
                "[Trading] Unknown execution {ExecutionId} manually resolved as {Resolution} by {ResolvedBy}: {Note}",
                executionId, resolution, resolvedBy, note);
            return Results.Ok(new
            {
                executionId,
                state = resolution == "placed" ? "resolved_placed" : "resolved_not_placed",
                resolvedUtc
            });
        }).RequireAuthorization("TradingTrader");

        trading.MapGet("/events", async (
            int? limit,
            ITradingRepository repository,
            CancellationToken ct) =>
            Results.Ok(await repository.GetEventsAsync(limit ?? 200, ct)));

        trading.MapGet("/reconciliation", async (
            int? limit,
            ITradingRepository repository,
            CancellationToken ct) =>
            Results.Ok(await repository.GetReconciliationRunsAsync(limit ?? 100, ct)));
    }
}
