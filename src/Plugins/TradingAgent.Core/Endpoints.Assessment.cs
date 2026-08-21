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
/// <c>/trading</c> endpoints for assessment jobs and ad-hoc symbol assessment.
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
///   <item><description><c>/alerts/{alertId}/assess</c></description></item>
///   <item><description><c>/alerts/{alertId}/assessment-jobs</c></description></item>
///   <item><description><c>/assess</c></description></item>
///   <item><description><c>/assessment-jobs</c></description></item>
///   <item><description><c>/assessment-jobs/{jobId}</c></description></item>
/// </list>
/// </summary>
public sealed partial class TradingCoreEndpoints
{
    private static void MapAssessmentEndpoints(RouteGroupBuilder trading)
    {
        // ── Assessment (LLM confidence, on demand) ────────────────────────────
        // Deliberately NOT automatic: a model call per alert would cost real money and hit rate limits
        // on a busy day, and most alerts are read and dismissed in a second without needing one. The
        // numbers stay deterministic — this only adds a judgement over them.

        trading.MapPost("/assessment-jobs", (
            AssessRequest body,
            AssessmentJobCoordinator jobs,
            StockAssessmentService assessments,
            CandleAnalysisService analysis,
            PsxDataClient dataClient,
            MonitoredUniverse universe) =>
        {
            if (string.IsNullOrWhiteSpace(body.Symbol))
                return Results.BadRequest(new { error = "symbol_required" });

            try
            {
                var symbol = PsxDataClient.NormalizeStockSymbol(body.Symbol);
                var interval = body.Interval?.Trim() ?? "1D";
                var key = $"symbol|{symbol}|{interval}|{body.Context?.Trim()}";
                var submitted = jobs.Submit(key, async jobCt =>
                    (object)await AssessSymbolAsync(
                        symbol, interval, body.Context, null,
                        assessments, analysis, dataClient, universe, jobCt));

                return Results.Accepted($"/api/trading/assessment-jobs/{submitted.JobId}", new
                {
                    jobId = submitted.JobId,
                    state = "queued",
                    reused = submitted.Reused
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_symbol", message = ex.Message });
            }
            catch (AssessmentQueueFullException ex)
            {
                return Results.Json(new { error = "assessment_queue_full", message = ex.Message },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }).RequireAuthorization("TradingAnalyst");

        trading.MapGet("/assessment-jobs/{jobId}", (
            string jobId,
            AssessmentJobCoordinator jobs) =>
        {
            var job = jobs.Get(jobId);
            return job is null
                ? Results.NotFound(new { error = "unknown_assessment_job", jobId })
                : Results.Ok(job);
        }).RequireAuthorization("TradingAnalyst");

        trading.MapPost("/alerts/{alertId}/assessment-jobs", async (
            string alertId,
            ITradingRepository repository,
            AssessmentJobCoordinator jobs,
            StockAssessmentService assessments,
            CandleAnalysisService analysis,
            PsxDataClient dataClient,
            MonitoredUniverse universe,
            CancellationToken requestCt) =>
        {
            var alert = await repository.GetAlertAsync(alertId, requestCt);
            if (alert is null) return Results.NotFound(new { error = "unknown_alert", alertId });

            try
            {
                var submitted = jobs.Submit($"alert|{alertId}", async jobCt =>
                {
                    var key = StockAssessmentService.CacheKeyFor(
                        alert.Symbol, alert.LevelPrice, alert.Interval);
                    if (assessments.TryGetCached(key, out var cached))
                        return (object)new
                        {
                            alertId,
                            alert.Symbol,
                            alert.Kind,
                            assessment = cached,
                            evidence = (object?)null
                        };

                    var context =
                        $"MONITOR ALERT: {alert.Kind} on {alert.Symbol} at {alert.Price} "
                        + $"(level {alert.LevelPrice?.ToString() ?? "n/a"}, "
                        + $"weekly-confirmed: {alert.WeeklyConfirmed}, "
                        + $"raised from a still-forming bar: {alert.FromLiveBar}). {alert.Summary}";
                    var result = await AssessSymbolAsync(
                        alert.Symbol, alert.Interval, context, key,
                        assessments, analysis, dataClient, universe, jobCt);
                    return (object)new { alertId, alert.Symbol, alert.Kind, result.assessment, result.evidence };
                });

                return Results.Accepted($"/api/trading/assessment-jobs/{submitted.JobId}", new
                {
                    jobId = submitted.JobId,
                    state = "queued",
                    reused = submitted.Reused
                });
            }
            catch (AssessmentQueueFullException ex)
            {
                return Results.Json(new { error = "assessment_queue_full", message = ex.Message },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }).RequireAuthorization("TradingAnalyst");

        trading.MapPost("/assess", async (
            AssessRequest body,
            StockAssessmentService assessments,
            CandleAnalysisService analysis,
            PsxDataClient dataClient,
            MonitoredUniverse universe,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Symbol))
                return Results.BadRequest(new { error = "symbol_required" });

            try
            {
                var result = await AssessSymbolAsync(
                    body.Symbol, body.Interval, body.Context, null,
                    assessments, analysis, dataClient, universe, ct);
                return Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_symbol", message = ex.Message });
            }
            catch (CandleAnalysisException ex)
            {
                return Results.NotFound(new { error = "no_candles", message = ex.Message });
            }
            // The caller hung up (navigated away, or its own fetch timeout fired mid-model-call).
            // Nothing to report and nobody left to report it to, but it must still be caught: an
            // OperationCanceledException escaping the handler reaches the exception page and breaks
            // into the debugger on what is a routine event.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogDebug("[Trading] Assessment for {Symbol} abandoned — caller disconnected.",
                    body.Symbol);
                return Results.StatusCode(499);
            }
            // Guard on ct, not the exception type: a dead local-model connection or the SDK's own
            // network timeout also throws OperationCanceledException, and that is a real failure that
            // should come back as a 502 rather than crash.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "[Trading] Assessment failed for {Symbol}.", body.Symbol);
                return Results.Problem(title: "assessment_failed", detail: ex.Message, statusCode: 502);
            }
        }).RequireAuthorization("TradingAnalyst");

        trading.MapPost("/alerts/{alertId}/assess", async (
            string alertId,
            ITradingRepository repository,
            StockAssessmentService assessments,
            CandleAnalysisService analysis,
            PsxDataClient dataClient,
            MonitoredUniverse universe,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            var alert = await repository.GetAlertAsync(alertId, ct);
            if (alert is null) return Results.NotFound(new { error = "unknown_alert", alertId });

            // An alert already knows its own symbol, level and interval, so a repeat click can be
            // answered before fetching anything — the generic /assess path has to analyze first just to
            // learn which level it is about.
            //
            // The SAME key is then handed to the assessment below. Deriving it twice is what broke this
            // the first time: a level-less alert (a trend flip has no level) hashed to a different key
            // than the inner path's fallback to the suggested entry, so this short-circuit could never
            // hit and every repeat click paid for the full evidence gather.
            var key = StockAssessmentService.CacheKeyFor(
                alert.Symbol, alert.LevelPrice, alert.Interval);
            if (assessments.TryGetCached(key, out var cached))
                return Results.Ok(new { alertId, alert.Symbol, alert.Kind, assessment = cached });

            try
            {
                // The alert itself becomes context, so the verdict answers the question the alert
                // actually raised rather than a generic "is this a good stock".
                var context =
                    $"MONITOR ALERT: {alert.Kind} on {alert.Symbol} at {alert.Price} "
                    + $"(level {alert.LevelPrice?.ToString() ?? "n/a"}, "
                    + $"weekly-confirmed: {alert.WeeklyConfirmed}, "
                    + $"raised from a still-forming bar: {alert.FromLiveBar}). {alert.Summary}";

                var result = await AssessSymbolAsync(
                    alert.Symbol, alert.Interval, context, key,
                    assessments, analysis, dataClient, universe, ct);

                return Results.Ok(new { alertId, alert.Symbol, alert.Kind, result.assessment, result.evidence });
            }
            catch (CandleAnalysisException ex)
            {
                return Results.NotFound(new { error = "no_candles", message = ex.Message });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogDebug("[Trading] Alert assessment {AlertId} abandoned — caller disconnected.",
                    alertId);
                return Results.StatusCode(499);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "[Trading] Alert assessment failed for {AlertId}.", alertId);
                return Results.Problem(title: "assessment_failed", detail: ex.Message, statusCode: 502);
            }
        }).RequireAuthorization("TradingAnalyst");

    }

    /// <summary>
    /// Assembles the evidence for one symbol and asks for a verdict on it.
    ///
    /// <para>
    /// The evidence is the same deterministic read the chart draws (<see cref="CandleAnalysisService"/>)
    /// plus the portal's quote, listing status and news — so the assessment judges exactly what the
    /// user is looking at. Caching is keyed on symbol + level + session, because clicking twice on the
    /// same situation should not cost two model calls, while a level that has moved is a different
    /// question and deserves a fresh answer.
    /// </para>
    /// </summary>
    private static async Task<dynamic> AssessSymbolAsync(
        string symbol,
        string? interval,
        string? context,
        string? cacheKey,
        StockAssessmentService assessments,
        CandleAnalysisService analysis,
        PsxDataClient dataClient,
        MonitoredUniverse universe,
        CancellationToken ct)
    {
        var minutes = PsxDataClient.ResolveInterval(interval) ?? PsxCandle.DailyIntervalMinutes;
        var candles = await analysis.AnalyzeAsync(symbol, minutes, ct: ct);
        var normalized = candles.Symbol;

        // News, index backdrop and listing status. Fail-soft: a news outage must not block a verdict
        // that is mostly grounded in candles, but the model is told the section is missing.
        StockResearchData? research = null;
        try
        {
            research = await dataClient.GatherAsync(normalized, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Left null; the evidence records it explicitly below.
        }

        var snapshot = candles.Snapshot;
        var evidence = new
        {
            symbol = normalized,
            interval = candles.Interval,
            tradable = universe.IsTradable(normalized),
            technical = snapshot,
            weekly = new
            {
                bars = candles.Multi.WeeklyBars,
                alignment = candles.Multi.Alignment.ToString(),
                breakdown = candles.Multi.WeeklyBreakdown,
                entry_level_confirmed = candles.Multi.EntryLevelConfirmedWeekly,
                confirmed_supports = candles.Multi.ConfirmedSupports,
                confirmed_resistances = candles.Multi.ConfirmedResistances,
                notes = candles.Multi.Notes
            },
            quote = research?.Quote ?? candles.Quote,
            kse100_index = research?.IndexQuote,
            listing_status = research?.ListingStatus,
            company_news = research?.CompanyNews,
            market_news = research?.MarketNews,
            news_available = research is not null,
            warnings = candles.Warnings,
            retrieved_at_utc = candles.RetrievedAtUtc
        };

        var assessment = await assessments.AssessAsync(new StockAssessmentRequest
        {
            Symbol       = normalized,
            Evidence     = evidence,
            Context      = context,
            ContextLabel = "WHAT PROMPTED THIS ASSESSMENT",
            IsDelisted   = research?.ListingStatus.IsDelisted == true,
            // A caller that already knows the situation's identity supplies the key; otherwise it is
            // derived from the level this analysis is actually about.
            CacheKey     = cacheKey ?? StockAssessmentService.CacheKeyFor(
                normalized, snapshot.SuggestedEntry, candles.Interval)
        }, ct);

        return new { symbol = normalized, assessment, evidence };
    }
}
