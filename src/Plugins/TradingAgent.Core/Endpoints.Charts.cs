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
using TradingAgent.Chart;
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
/// <c>/trading</c> endpoints for the chart series, indicators, levels, and level-anchored plan.
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
///   <item><description><c>/candles</c></description></item>
/// </list>
/// </summary>
public sealed partial class TradingCoreEndpoints
{
    private static void MapChartsEndpoints(RouteGroupBuilder trading)
    {
        // ── Candles (chart data) ──────────────────────────────────────────────
        // Everything the chart needs in ONE request: the bars, the indicator lines, the levels with
        // their touch counts and weekly confirmation, and the level-anchored trade math. It is served
        // from CandleAnalysisService — the same code path analyze_candles uses — so the chart cannot
        // draw one set of levels while the agent quotes another.
        trading.MapGet("/candles", async (
            string symbol,
            string? interval,
            int? bars,
            bool? includeLive,
            CandleAnalysisService analysis,
            MonitoredUniverse universe,
            ChartOverlayCollector overlayCollector,
            IOptions<TradingAgentOptions> options,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            var minutes = PsxDataClient.ResolveInterval(interval);
            if (minutes is null)
                return Results.BadRequest(new
                {
                    error = "unsupported_interval",
                    message = $"Interval '{interval}' is not supported. Use "
                            + "1M, 1W, 1D, 60m, 30m, 15m, or 5m."
                });

            try
            {
                var result = await analysis.AnalyzeAsync(
                    symbol, minutes.Value, bars, includeLive ?? true, ct);

                var candles = result.Candles;
                var closes = IndicatorSeries.Closes(candles);
                var sma20 = IndicatorSeries.Sma(closes, 20);
                var sma50 = IndicatorSeries.Sma(closes, 50);
                var rsi14 = IndicatorSeries.Rsi(closes, TechnicalOptions.From(
                    options.Value.Scan).RsiPeriod);

                // Weekly-confirmed levels are the structural ones. Matching by price (within the
                // configured confluence tolerance) rather than by identity, because the weekly analysis
                // derives its own level objects from resampled bars.
                var tolerance = options.Value.Scan.ConfluenceTolerancePercent;
                bool ConfirmedWeekly(decimal price, IEnumerable<ConfluenceLevel> confirmed) =>
                    confirmed.Any(c => price > 0
                        && Math.Abs(c.Price - price) / price * 100m <= tolerance);

                var technical = TechnicalOptions.From(options.Value.Scan);
                var snapshot = result.Snapshot;

                // Edition overlays: projections, predicted points, a next target, a confidence band.
                // Always present and always the same shape, so the client has one code path; empty
                // for the community edition, which registers no provider. The collector owns the
                // failure boundary — a provider that throws or overruns its budget is dropped and the
                // chart still renders, because this is a read path a person is waiting on.
                var overlays = await overlayCollector.CollectAsync(
                    result.Symbol,
                    result.Interval,
                    candles.Select(c => new DateTimeOffset(
                        c.BucketStartUtc ?? c.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                        TimeSpan.Zero).ToUnixTimeSeconds()).ToList(),
                    ct);

                return Results.Ok(new
                {
                    symbol = result.Symbol,
                    interval = result.Interval,
                    // The thresholds this analysis actually classified against, so the chart can draw
                    // the same bands rather than assuming the textbook 30/70.
                    thresholds = new
                    {
                        rsiOversold = technical.RsiOversold,
                        rsiOverbought = technical.RsiOverbought
                    },
                    tradable = universe.IsTradable(result.Symbol),
                    barsAnalyzed = candles.Count,
                    sessionsAvailable = result.SessionsAvailable,
                    // The last bar may still be forming; the chart labels it so a half-formed candle is
                    // never read as a settled close.
                    usesLiveBar = snapshot.UsesLiveBar,

                    candles = candles.Select((c, i) => new
                    {
                        // Seconds since epoch: what lightweight-charts expects, and unambiguous for an
                        // intraday series where several bars share one session date.
                        time = new DateTimeOffset(
                            c.BucketStartUtc ?? c.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                            TimeSpan.Zero).ToUnixTimeSeconds(),
                        date = c.Date.ToString("yyyy-MM-dd"),
                        open = c.Open,
                        high = c.High,
                        low = c.Low,
                        close = c.Close,
                        volume = c.Volume,
                        isLive = c.IsLive,
                        sma20 = sma20[i],
                        sma50 = sma50[i],
                        rsi14 = rsi14[i]
                    }),

                    levels = new
                    {
                        supports = snapshot.Supports.Select(l => new
                        {
                            price = l.Price,
                            touches = l.Touches,
                            origin = l.Origin,
                            weeklyConfirmed = ConfirmedWeekly(l.Price, result.Multi.ConfirmedSupports),
                            distancePercent = snapshot.Close > 0
                                ? Math.Round((snapshot.Close - l.Price) / snapshot.Close * 100m, 2)
                                : (decimal?)null
                        }),
                        resistances = snapshot.Resistances.Select(l => new
                        {
                            price = l.Price,
                            touches = l.Touches,
                            origin = l.Origin,
                            weeklyConfirmed = ConfirmedWeekly(l.Price, result.Multi.ConfirmedResistances),
                            distancePercent = snapshot.Close > 0
                                ? Math.Round((l.Price - snapshot.Close) / snapshot.Close * 100m, 2)
                                : (decimal?)null
                        })
                    },

                    overlays,

                    plan = new
                    {
                        entry = snapshot.SuggestedEntry,
                        stop = snapshot.SuggestedStop,
                        target = snapshot.SuggestedTarget,
                        rewardRisk = snapshot.RewardRiskRatio,
                        // Confirmation of THIS plan's entry level. Deliberately not
                        // weekly.entryLevelConfirmed: that one is computed against the full archived
                        // history, whose nearest support can differ from the level shown for the
                        // requested window. Reporting the wrong one next to the plan would tell the
                        // user a displayed level has no weekly backing when it does.
                        entryWeeklyConfirmed = snapshot.SuggestedEntry is { } entry
                            && ConfirmedWeekly(entry, result.Multi.ConfirmedSupports)
                    },

                    snapshot = new
                    {
                        close = snapshot.Close,
                        asOf = snapshot.AsOf.ToString("yyyy-MM-dd"),
                        dayChangePercent = snapshot.DayChangePercent,
                        zone = snapshot.Zone.ToString(),
                        setup = snapshot.Setup.ToString(),
                        trend = snapshot.Trend,
                        rsi14 = snapshot.Rsi14,
                        atr14 = snapshot.Atr14,
                        atrPercent = snapshot.AtrPercent,
                        sma20 = snapshot.Sma20,
                        sma50 = snapshot.Sma50,
                        volume = snapshot.Volume,
                        averageVolume = snapshot.AverageVolume,
                        volumeRatio = snapshot.VolumeRatio,
                        rangeLow = snapshot.RangeLow,
                        rangeHigh = snapshot.RangeHigh,
                        rangePosition = snapshot.RangePosition,
                        nearestSupport = snapshot.NearestSupport,
                        percentAboveSupport = snapshot.PercentAboveSupport,
                        nearestResistance = snapshot.NearestResistance,
                        percentBelowResistance = snapshot.PercentBelowResistance,
                        reasons = snapshot.Reasons
                    },

                    // Higher-timeframe read. Present for an intraday request too, where it is the
                    // structure an intraday entry must actually be traded against.
                    weekly = new
                    {
                        bars = result.Multi.WeeklyBars,
                        alignment = result.Multi.Alignment.ToString(),
                        breakdown = result.Multi.WeeklyBreakdown,
                        // About the FULL-history nearest support (what analyze_candles reports), which
                        // is not necessarily the level in `plan` — use plan.entryWeeklyConfirmed for that.
                        entryLevelConfirmed = result.Multi.EntryLevelConfirmedWeekly,
                        zone = result.Multi.Weekly?.Zone.ToString(),
                        setup = result.Multi.Weekly?.Setup.ToString(),
                        nearestSupport = result.Multi.Weekly?.NearestSupport,
                        nearestResistance = result.Multi.Weekly?.NearestResistance,
                        notes = result.Multi.Notes
                    },

                    retrievedAtUtc = result.RetrievedAtUtc,
                    warnings = result.Warnings.Concat(snapshot.Warnings).Distinct()
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_symbol", message = ex.Message });
            }
            catch (CandleAnalysisException ex)
            {
                // Nothing to draw, and the message says why (bad ticker vs no trades today).
                return Results.NotFound(new { error = "no_candles", message = ex.Message });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "[Trading] Chart data failed for {Symbol}.", symbol);
                return Results.Problem(
                    title: "candle_analysis_failed", detail: ex.Message, statusCode: 502);
            }
        });

    }
}
