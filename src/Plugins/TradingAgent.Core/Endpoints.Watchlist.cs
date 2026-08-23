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
        // When ExecutionUniverseSource is Watchlist, edits also change the execution universe; every
        // order still crosses the same deterministic risk, calendar, sizing, and reconciliation gates.

        trading.MapGet("/watchlist", async (
            MonitoredUniverse universe,
            ITradingRepository repository,
            PsxDataClient dataClient,
            IOptions<TradingAgentOptions> options,
            CancellationToken ct) =>
        {
            await universe.SeedIfNeededAsync(ct: ct);
            var snapshot = await repository.GetWatchlistAsync(ct);
            var tradable = (await universe.ForExecutionAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // The configured half of the deny set, so the UI can show a row as locked rather than
            // offering a toggle that silently cannot take effect.
            var manualByConfig = universe.ConfiguredManualOnly()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                    // The stored toggle, and the effective answer after the configured list is folded
                    // in. They differ for a config-pinned symbol, which is exactly when the UI must
                    // not present a toggle as if it would work.
                    autoTradeEnabled = e.AutoTradeEnabled,
                    manualOnly = !e.AutoTradeEnabled || manualByConfig.Contains(e.Symbol),
                    manualOnlyLocked = manualByConfig.Contains(e.Symbol),
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
                executionUniverseSource = options.Value.ExecutionUniverseSource.ToString(),
                tradableSymbols = tradable.Count,
                maxSymbols = options.Value.Watchlist.MaxSymbols,
                // Symbols pinned manual-only by configuration, including any not on the watchlist at
                // all: they still block automation, and an operator looking for "why did nothing fire"
                // needs to see them somewhere.
                configuredManualOnly = universe.ConfiguredManualOnly()
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

            var isTradable = await universe.IsTradableAsync(symbol, ct);
            var isManualOnly = await universe.IsManualOnlyAsync(symbol, ct);
            return Results.Ok(new
            {
                symbol,
                added,
                tradable = isTradable,
                manualOnly = isManualOnly,
                // Said up front rather than discovered at order time. Two different limits, so they
                // are reported separately: one says no order may exist, the other says only yours may.
                message = !isTradable
                    ? $"'{symbol}' will be monitored and charted, but it is not in the selected execution universe, so an "
                      + "order for it would be rejected by the risk engine."
                    : isManualOnly
                        ? $"'{symbol}' is set to manual-only: it will be monitored, charted and alerted "
                          + "on, but no automation will trade it — you place its orders yourself."
                        : null,
                warning
            });
        }).RequireAuthorization("TradingAnalyst");

        trading.MapGet("/watchlist/presets/{index}", async (
            string index,
            MonitoredUniverse universe,
            ITradingRepository repository,
            PsxDataClient dataClient,
            AhlAnalyticsClient analytics,
            AhkPortalClient brokerPortal,
            IOptions<TradingAgentOptions> options,
            CancellationToken ct) =>
        {
            var normalized = NormalizeWatchlistPreset(index);
            if (normalized is null)
                return Results.BadRequest(new
                {
                    error = "unknown_watchlist_preset",
                    message = "Choose KSE100 or KSE30."
                });

            var preset = await LoadWatchlistPresetAsync(
                normalized, dataClient, analytics, brokerPortal, ct);
            if (preset.Symbols.Count == 0)
                return Results.Json(new
                {
                    error = "watchlist_preset_unavailable",
                    message = preset.Error ?? $"No {normalized} constituents are currently available."
                }, statusCode: StatusCodes.Status503ServiceUnavailable);

            await universe.SeedIfNeededAsync(ct: ct);
            var current = await repository.GetWatchlistAsync(ct);
            var watched = current.Entries.Select(entry => entry.Symbol)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var members = preset.Symbols.ToHashSet(StringComparer.OrdinalIgnoreCase);

            return Results.Ok(new
            {
                index = normalized,
                label = normalized == "KSE100" ? "KSE 100" : "KSE 30",
                source = preset.Source,
                sourceUrl = preset.SourceUrl,
                count = members.Count,
                alreadyWatched = members.Count(watched.Contains),
                missing = members.Count(symbol => !watched.Contains(symbol)),
                outsideIndex = watched.Count(symbol => !members.Contains(symbol)),
                projectedMergeCount = watched.Union(members, StringComparer.OrdinalIgnoreCase).Count(),
                maxSymbols = Math.Max(1, options.Value.Watchlist.MaxSymbols),
                grantsTradingPermission =
                    options.Value.ExecutionUniverseSource == TradingExecutionUniverseSource.Watchlist,
                warning = preset.Warning
            });
        });

        trading.MapPost("/watchlist/presets/{index}", async (
            string index,
            WatchlistPresetRequest body,
            MonitoredUniverse universe,
            ITradingRepository repository,
            PsxDataClient dataClient,
            AhlAnalyticsClient analytics,
            AhkPortalClient brokerPortal,
            IOptions<TradingAgentOptions> options,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            var normalized = NormalizeWatchlistPreset(index);
            if (normalized is null)
                return Results.BadRequest(new
                {
                    error = "unknown_watchlist_preset",
                    message = "Choose KSE100 or KSE30."
                });

            var mode = body.Mode?.Trim().ToLowerInvariant();
            if (mode is not ("merge" or "replace"))
                return Results.BadRequest(new
                {
                    error = "invalid_watchlist_preset_mode",
                    message = "Mode must be 'merge' (add missing) or 'replace'."
                });

            var preset = await LoadWatchlistPresetAsync(
                normalized, dataClient, analytics, brokerPortal, ct);
            if (preset.Symbols.Count == 0)
                return Results.Json(new
                {
                    error = "watchlist_preset_unavailable",
                    message = preset.Error ?? $"No {normalized} constituents are currently available."
                }, statusCode: StatusCodes.Status503ServiceUnavailable);

            await universe.SeedIfNeededAsync(ct: ct);
            var current = await repository.GetWatchlistAsync(ct);
            var currentSymbols = current.Entries.Select(entry => entry.Symbol)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var projectedCount = mode == "replace"
                ? preset.Symbols.Count
                : currentSymbols.Union(preset.Symbols, StringComparer.OrdinalIgnoreCase).Count();
            var limit = Math.Max(1, options.Value.Watchlist.MaxSymbols);
            if (projectedCount > limit)
                return Results.BadRequest(new
                {
                    error = "watchlist_full",
                    message = $"This would create a {projectedCount}-symbol watchlist, above the {limit}-symbol limit. "
                            + "Use Replace, remove symbols, or raise Plugins:TradingAgent:Watchlist:MaxSymbols."
                });

            var result = await repository.ApplyWatchlistSymbolsAsync(
                preset.Symbols, mode == "replace", $"index:{normalized}", ct);
            universe.Invalidate();
            logger.LogInformation(
                "[Watchlist] Applied {Index} in {Mode} mode via {Source}: {Added} added, {Removed} removed, {Preserved} preserved.",
                normalized, mode, preset.Source, result.Added, result.Removed, result.Preserved);

            return Results.Ok(new
            {
                index = normalized,
                mode,
                source = preset.Source,
                sourceUrl = preset.SourceUrl,
                total = result.Total,
                added = result.Added,
                removed = result.Removed,
                preserved = result.Preserved,
                warning = preset.Warning,
                message = $"{(mode == "replace" ? "Replaced the watchlist with" : "Added missing members from")} "
                        + $"{(normalized == "KSE100" ? "KSE 100" : "KSE 30")}. "
                        + (options.Value.ExecutionUniverseSource == TradingExecutionUniverseSource.Watchlist
                            ? "The watchlist is the selected execution universe, so its members are tradable subject to every risk control."
                            : "This changes monitoring only; AllowedSymbols and trading permissions were not changed.")
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
                normalized, body.AlertsEnabled, body.Notes, body.Pinned, body.AutoTradeEnabled, ct);
            universe.Invalidate();
            if (!updated) return Results.NotFound(new { symbol = normalized, updated });

            // Switching automation back ON cannot lift a configured pin, and saying so here is the
            // difference between "the toggle is broken" and "this symbol is pinned in appsettings".
            var lockedByConfig = body.AutoTradeEnabled == true
                && universe.ConfiguredManualOnly().Contains(normalized, StringComparer.OrdinalIgnoreCase);

            return Results.Ok(new
            {
                symbol = normalized,
                updated,
                manualOnly = await universe.IsManualOnlyAsync(normalized, ct),
                message = lockedByConfig
                    ? $"'{normalized}' stays manual-only: it is listed in "
                      + "Plugins:TradingAgent:ManualOnlySymbols, which the API cannot override. "
                      + "Remove it there and restart to let automation trade it."
                    : null
            });
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
            var seed = universe.ConfiguredAllowedSymbols();
            var count = await repository.ResetWatchlistAsync(seed, MonitoredUniverse.SeedHash(seed), ct);
            universe.Invalidate();
            logger.LogInformation("[Watchlist] Reset to AllowedSymbols ({Count}) via web API.", count);
            return Results.Ok(new { symbols = count });
        }).RequireAuthorization("TradingAnalyst");

    }

    private static string? NormalizeWatchlistPreset(string index) =>
        index.Trim().Replace("-", "").Replace(" ", "").ToUpperInvariant() switch
        {
            "KSE100" => "KSE100",
            "KSE30" => "KSE30",
            _ => null
        };

    private static async Task<WatchlistPresetMembers> LoadWatchlistPresetAsync(
        string index,
        PsxDataClient dataClient,
        AhlAnalyticsClient analytics,
        AhkPortalClient brokerPortal,
        CancellationToken ct)
    {
        var official = await dataClient.GetIndexConstituentsAsync(index, ct);
        if (official.Symbols.Count > 0)
            return new(official.Symbols, "Official PSX", official.SourceUrl);

        // Market Movers already receives this same AHL whole-market snapshot. It is a fallback only:
        // index membership should not require a broker login when the public exchange page works.
        var snapshot = await analytics.GetMarketSnapshotAsync(
            allowHandshake: brokerPortal.HasSession, ct: ct);
        var fromAhl = snapshot?.Equities?
            .Where(pair => pair.Value.ListedIn?.Contains(index, StringComparer.OrdinalIgnoreCase) == true)
            .Select(pair => pair.Key.Trim().ToUpperInvariant())
            .Where(symbol => symbol.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        return fromAhl.Count > 0
            ? new(fromAhl, "AHL Market Movers", null,
                "The official PSX page was unavailable, so membership came from the cached Market Movers source.")
            : new([], "Unavailable", official.SourceUrl, null,
                $"{official.Error ?? "The official PSX page returned no members."} "
                + $"The Market Movers fallback is also unavailable: {analytics.LastError ?? "no AHL snapshot"}");
    }

    private sealed record WatchlistPresetMembers(
        IReadOnlyList<string> Symbols,
        string Source,
        string? SourceUrl,
        string? Warning = null,
        string? Error = null);
}
