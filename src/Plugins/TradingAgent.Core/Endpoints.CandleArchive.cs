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
/// <c>/trading</c> endpoints for daily-history archive status and backfill.
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
///   <item><description><c>/candle-archive</c></description></item>
///   <item><description><c>/candle-archive/backfill</c></description></item>
/// </list>
/// </summary>
public sealed partial class TradingCoreEndpoints
{
    private static void MapCandleArchiveEndpoints(RouteGroupBuilder trading)
    {
        // Candle archive: how much daily history is stored, how much is still missing, and what the
        // backfill is doing right now. Read-only, so any management viewer can see it.
        trading.MapGet("/candle-archive", async (
            CandleBackfillRunner runner,
            CancellationToken ct) => Results.Ok(await runner.GetStatusAsync(ct)));

        // Starts a backfill pass and returns immediately: a two-year pass takes ~18 minutes, so the
        // request must not wait on it. The pass is bound to the application lifetime and is
        // single-flight — a second trigger while one is running reports the running pass rather than
        // starting a competing one, which would double the request rate the portal sees.
        // `symbols` scopes the pass to the dates those symbols are actually missing, which is the only
        // way to fill a symbol added to the archive universe after the deep history was fetched: the
        // dates are all on record, so an unscoped pass finds nothing to do and the symbol stays starved.
        trading.MapPost("/candle-archive/backfill", async (
            CandleBackfillRequest? body,
            CandleBackfillRunner runner,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            var started = runner.TryStart(body?.Years, body?.Symbols);
            logger.LogInformation(
                "[TradingAgent] Candle backfill {Outcome} via web API (years={Years}, symbols={Symbols}).",
                started ? "started" : "already running",
                body?.Years?.ToString() ?? "configured",
                body?.Symbols is { Count: > 0 } s ? string.Join(",", s) : "all archived");

            var status = await runner.GetStatusAsync(ct);
            return Results.Accepted(value: new { started, status });
        }).RequireAuthorization("ManagementAdministrator");

    }
}
