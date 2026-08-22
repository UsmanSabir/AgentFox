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
/// <c>/trading</c> endpoints for the watched universe.
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
///   <item><description><c>/watchlist</c></description></item>
///   <item><description><c>/watchlist/reorder</c></description></item>
///   <item><description><c>/watchlist/reset</c></description></item>
///   <item><description><c>/watchlist/{symbol}</c></description></item>
/// </list>
/// </summary>
public sealed partial class TradingCoreEndpoints
{
    private static void MapWatchlistEndpoints(RouteGroupBuilder trading)
    {
        // ── Watchlist ─────────────────────────────────────────────────────────
        // The user's monitoring universe. Reads are viewer-level; edits require TradingAnalyst.
        // Nothing here can widen what may be traded — AllowedSymbols stays configuration-only, and
        // each entry reports whether an order for it would pass the risk engine.

        trading.MapGet("/watchlist", async (
            MonitoredUniverse universe,
            ITradingRepository repository,
            PsxDataClient dataClient,
            IOptions<TradingAgentOptions> options,
            CancellationToken ct) =>
        {
            await universe.SeedIfNeededAsync(ct: ct);
            var snapshot = await repository.GetWatchlistAsync(ct);
            var tradable = universe.ForExecution().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var symbols = snapshot.Entries.Select(e => e.Symbol).ToList();
            var barCounts = await repository.GetDailyBarCountsAsync(symbols, ct);
            var openAlerts = await repository.GetOpenAlertCountsAsync(ct);
            IReadOnlyDictionary<string, PsxLiveQuote> marketWatch;
            try { marketWatch = await dataClient.GetMarketWatchAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Company names are presentation metadata. A portal outage must not take down the
                // user's watchlist, chart access, or trading controls.
                marketWatch = new Dictionary<string, PsxLiveQuote>(StringComparer.OrdinalIgnoreCase);
            }

            // Reported per symbol because a freshly added symbol has no deep history until a backfill
            // reaches it, and without it there is no weekly confirmation to quote. The threshold is
            // shared with the archive card so the two cannot disagree about who is ready.
            const int weeklyReadyBars = MultiTimeframeAnalyzer.MinimumDailyBarsForWeekly;

            return Results.Ok(new
            {
                entries = snapshot.Entries.Select(e => new
                {
                    symbol = e.Symbol,
                    companyName = marketWatch.GetValueOrDefault(e.Symbol)?.CompanyName,
                    // Current session move against the previous close. It stays null when the
                    // market-watch quote is unavailable; unknown must not be presented as flat.
                    dayChangePercent = marketWatch.GetValueOrDefault(e.Symbol)?.ChangePercent,
                    addedUtc = e.AddedUtc,
                    source = e.Source,
                    sortOrder = e.SortOrder,
                    pinned = e.Pinned,
                    alertsEnabled = e.AlertsEnabled,
                    notes = e.Notes,
                    tradable = tradable.Contains(e.Symbol),
                    archivedBars = barCounts.GetValueOrDefault(e.Symbol),
                    hasWeeklyHistory = barCounts.GetValueOrDefault(e.Symbol) >= weeklyReadyBars,
                    openAlerts = openAlerts.GetValueOrDefault(e.Symbol)
                }),
                seededUtc = snapshot.SeededUtc,
                // True when AllowedSymbols has changed since the watchlist was seeded. Surfaced so the
                // UI can offer a reset; the watchlist is never re-seeded automatically, because that
                // would silently discard the user's edits.
                configuredListChanged =
                    snapshot.SeedHash is not null && snapshot.SeedHash != universe.CurrentSeedHash(),
                tradableSymbols = tradable.Count,
                maxSymbols = options.Value.Watchlist.MaxSymbols
            });
        });

        trading.MapPost("/watchlist", async (
            WatchlistSymbolRequest body,
            MonitoredUniverse universe,
            ITradingRepository repository,
            PsxDataClient dataClient,
            IOptions<TradingAgentOptions> options,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            string symbol;
            try
            {
                symbol = PsxDataClient.NormalizeStockSymbol(body.Symbol ?? "");
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_symbol", message = ex.Message });
            }

            await universe.SeedIfNeededAsync(ct: ct);
            var existing = await repository.GetWatchlistAsync(ct);
            var limit = Math.Max(1, options.Value.Watchlist.MaxSymbols);
            if (existing.Entries.Count >= limit
                && !existing.Entries.Any(e => e.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.BadRequest(new
                {
                    error = "watchlist_full",
                    message = $"The watchlist already holds its maximum of {limit} symbols. "
                            + "Remove one, or raise Plugins:TradingAgent:Watchlist:MaxSymbols."
                });
            }

            // Catch a typo at the point of entry rather than letting it become a permanently empty
            // chart. A portal outage must not block editing, so an unreachable market watch warns.
            string? warning = null;
            if (options.Value.Watchlist.ValidateAgainstMarketWatch)
            {
                try
                {
                    // Validated against the PSX market watch specifically, NOT the composite. The
                    // broker feed only carries what has been subscribed and has ticked, so a symbol
                    // absent from it is routine — using it here would reject valid tickers. PSX
                    // covers the whole market, which is exactly what a typo check needs.
                    var quotes = await dataClient.GetMarketWatchAsync(ct);
                    if (quotes.Count > 0 && !quotes.ContainsKey(symbol))
                    {
                        return Results.BadRequest(new
                        {
                            error = "unknown_symbol",
                            message = $"'{symbol}' is not in the current PSX market watch. Check the ticker."
                        });
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "[Watchlist] Symbol validation skipped; market watch unavailable.");
                    warning = "Could not reach the PSX market watch, so the ticker was not verified.";
                }
            }

            var added = await repository.AddWatchlistSymbolAsync(symbol, "user", ct);
            universe.Invalidate();
            if (added)
                logger.LogInformation("[Watchlist] Added {Symbol} via web API.", symbol);

            return Results.Ok(new
            {
                symbol,
                added,
                tradable = universe.IsTradable(symbol),
                // Said up front rather than discovered at order time.
                message = universe.IsTradable(symbol)
                    ? null
                    : $"'{symbol}' will be monitored and charted, but it is not in AllowedSymbols, so an "
                    + "order for it would be rejected by the risk engine.",
                warning
            });
        }).RequireAuthorization("TradingAnalyst");

        trading.MapDelete("/watchlist/{symbol}", async (
            string symbol,
            MonitoredUniverse universe,
            ITradingRepository repository,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            var normalized = symbol.Trim().ToUpperInvariant();
            // Archived bars and alert history are deliberately kept: they are evidence, and re-adding
            // the symbol should not have to re-download two years of history.
            var removed = await repository.RemoveWatchlistSymbolAsync(normalized, ct);
            universe.Invalidate();
            if (removed) logger.LogInformation("[Watchlist] Removed {Symbol} via web API.", normalized);
            return removed
                ? Results.Ok(new { symbol = normalized, removed })
                : Results.NotFound(new { symbol = normalized, removed });
        }).RequireAuthorization("TradingAnalyst");

        trading.MapPatch("/watchlist/{symbol}", async (
            string symbol,
            WatchlistUpdateRequest body,
            MonitoredUniverse universe,
            ITradingRepository repository,
            CancellationToken ct) =>
        {
            var normalized = symbol.Trim().ToUpperInvariant();
            var updated = await repository.UpdateWatchlistSymbolAsync(
                normalized, body.AlertsEnabled, body.Notes, body.Pinned, ct);
            universe.Invalidate();
            return updated
                ? Results.Ok(new { symbol = normalized, updated })
                : Results.NotFound(new { symbol = normalized, updated });
        }).RequireAuthorization("TradingAnalyst");

        trading.MapPost("/watchlist/reorder", async (
            WatchlistReorderRequest body,
            MonitoredUniverse universe,
            ITradingRepository repository,
            CancellationToken ct) =>
        {
            var reordered = body.Symbols is { Count: > 0 }
                && await repository.ReorderWatchlistAsync(body.Symbols, ct);
            if (!reordered)
                return Results.BadRequest(new
                {
                    error = "invalid_watchlist_order",
                    message = "The submitted order must contain every watched symbol exactly once. Refresh and try again."
                });

            universe.Invalidate();
            return Results.Ok(new { reordered = true, symbols = body.Symbols!.Count });
        }).RequireAuthorization("TradingAnalyst");

        trading.MapPost("/watchlist/reset", async (
            MonitoredUniverse universe,
            ITradingRepository repository,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            // Explicitly discards the user's edits — which is the point of a reset, and why it is a
            // separate endpoint from the automatic first-run seeding.
            var seed = universe.ForExecution();
            var count = await repository.ResetWatchlistAsync(seed, MonitoredUniverse.SeedHash(seed), ct);
            universe.Invalidate();
            logger.LogInformation("[Watchlist] Reset to AllowedSymbols ({Count}) via web API.", count);
            return Results.Ok(new { symbols = count });
        }).RequireAuthorization("TradingAnalyst");

    }
}
