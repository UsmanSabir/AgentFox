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
/// <c>/trading</c> endpoints for direct order placement through TradingManager.
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
///   <item><description><c>/orders</c></description></item>
/// </list>
/// </summary>
public sealed partial class TradingCoreEndpoints
{
    private static void MapOrdersEndpoints(RouteGroupBuilder trading)
    {
        // An authenticated, human-friendly adapter over the existing deterministic manager. Policy,
        // reconciliation, market window, risk caps, idempotency, and the kill switch remain downstream.
        trading.MapPost("/orders", async (
            DashboardOrderRequest body,
            MonitoredUniverse universe,
            TradingAgent.Manager.TradingManager manager,
            PersistentOrderWorker persistentOrders,
            TradingReconciliationState reconciliation,
            TradingPolicyProvider policyProvider,
            ApprovalIntentRegistry intentRegistry,
            IOptions<TradingAgentOptions> options,
            HttpContext http,
            CancellationToken ct) =>
        {
            var intent = OrderIntentRegistry.Find(body.OrderIntentId);
            if (intent is null)
                return Results.BadRequest(new
                {
                    error = "unknown_order_intent",
                    message = $"Unknown order choice '{body.OrderIntentId}'. Refresh and choose one from the registry."
                });

            if (!intent.Submission.Equals("immediate", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new
                {
                    error = "conditional_order_intent",
                    message = $"'{intent.Label}' is a waiting trigger and must be armed, not placed immediately."
                });

            string symbol;
            try { symbol = PsxDataClient.NormalizeStockSymbol(body.Symbol ?? ""); }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_symbol", message = ex.Message });
            }

            if (!await universe.IsTradableAsync(symbol, ct))
                return Results.BadRequest(new
                {
                    error = "not_tradable",
                    message = $"'{symbol}' is not in the selected execution universe, so the risk engine will not trade it."
                });

            if (body.Quantity is not > 0)
                return Results.BadRequest(new { error = "invalid_quantity", message = "Quantity must be positive." });

            decimal? entryPrice = intent.OrderType switch
            {
                "MARKET"   => null,
                "STOPLOSS" => body.TriggerPrice,
                _          => body.Price
            };

            if (intent.OrderType != "MARKET" && entryPrice is not > 0)
                return Results.BadRequest(new
                {
                    error = "price_required",
                    message = intent.OrderType == "STOPLOSS"
                        ? "Enter the price that triggers the stop."
                        : "Enter a positive limit price."
                });

            decimal? stopLimit = null;
            if (intent.OrderType == "STOPLOSS" && entryPrice is { } trigger)
            {
                // A stop limit belongs just through the trigger in the direction price is moving.
                stopLimit = body.LimitPrice ?? decimal.Round(
                    trigger * (intent.Action == "SELL" ? 0.99m : 1.01m), 2,
                    MidpointRounding.AwayFromZero);
            }

            var signal = new TradingSignal
            {
                IsSignal = true,
                Action = intent.Action,
                Symbol = symbol,
                Quantity = body.Quantity,
                OrderType = intent.OrderType,
                EntryPrice = entryPrice,
                LimitPrice = stopLimit,
                Confidence = "HIGH",
                PreservePriceIntent = body.PersistentUntilFilled,
                RawMessage = $"dashboard:{intent.Id}"
            };

            IReadOnlyList<IReadOnlyList<TradingSignal>> groups = [[signal]];
            var policy = policyProvider.Current();
            PersistentOrderIntent? persistent = null;
            var effectiveQuantity = body.Quantity.Value;
            string? sellQuantityAdjustment = null;
            string source;
            if (body.PersistentUntilFilled)
            {
                if (PersistentOrderDecisions.ValidateEligibility(intent.OrderType) is { } persistenceProblem)
                    return Results.BadRequest(new
                    {
                        error = "order_not_persistable",
                        message = persistenceProblem
                    });

                var expires = body.ExpiresUtc
                    ?? DateTime.UtcNow.AddDays(Math.Clamp(body.ExpiresInDays ?? 30, 1, 365));
                if (expires <= DateTime.UtcNow)
                    return Results.BadRequest(new
                    {
                        error = "invalid_expiry",
                        message = "A persistent order must expire in the future."
                    });

                if (intent.Action.Equals("SELL", StringComparison.OrdinalIgnoreCase))
                {
                    var availability = SellQuantityRule.Available(
                        reconciliation.Current,
                        symbol,
                        DateTime.UtcNow,
                        TimeSpan.FromSeconds(Math.Max(
                            10, options.Value.ReconciliationMaxAgeSeconds)));
                    if (!availability.Known)
                        return Results.Conflict(new
                        {
                            error = "sell_availability_unknown",
                            message = availability.Reason
                        });
                    if (availability.AvailableQuantity <= 0)
                        return Results.Conflict(new
                        {
                            error = "no_sellable_holding",
                            message = $"No uncommitted {symbol} shares are available to sell."
                        });

                    effectiveQuantity = Math.Min(effectiveQuantity, availability.AvailableQuantity);
                    if (effectiveQuantity != body.Quantity.Value)
                    {
                        sellQuantityAdjustment = new SellQuantityAdjustment(
                            0, 0, symbol, body.Quantity.Value, effectiveQuantity).Message;
                    }
                }

                persistent = new PersistentOrderIntent
                {
                    IntentId = Guid.NewGuid().ToString("N"),
                    Symbol = symbol,
                    Action = intent.Action,
                    Quantity = effectiveQuantity,
                    OrderType = intent.OrderType,
                    Price = entryPrice,
                    LimitPrice = stopLimit,
                    ExpiresUtc = expires,
                    Note = $"New Order: {intent.Label}"
                };
                signal = persistent.ToSignal(effectiveQuantity);
                groups = [[signal]];
                source = PersistentOrderWorker.BuildSource(
                    persistent.IntentId, PsxTime.Today(), attempt: 1);
            }
            else
            {
                source = "dashboard-order:" + (
                    string.IsNullOrWhiteSpace(body.ClientRequestId)
                        ? Guid.NewGuid().ToString("N")
                        : body.ClientRequestId.Trim());
            }

            // The TradingTrader request is the approval event. Bind it to the exact order and let the
            // manager consume/re-hash the one-time intent exactly as it does for a host tool approval.
            var approvalIntent = ApprovalIntent.Create(
                groups, source, policy.Version,
                TimeSpan.FromSeconds(Math.Max(10, options.Value.ApprovalIntentTtlSeconds)));
            intentRegistry.Register(approvalIntent);
            var authorization = ExecutionAuthorization.HostToolGate(
                http.User.Identity?.Name ?? "trading-dashboard", approvalIntent);

            if (persistent is not null)
            {
                var submission = await persistentOrders.CreateAndSubmitAsync(
                    persistent, authorization, ct);
                var persistentResult = submission.Execution;
                return Results.Ok(new
                {
                    accepted = submission.Accepted,
                    IsReplay = persistentResult?.IsReplay ?? false,
                    ExecutionId = persistentResult?.ExecutionId ?? "",
                    PolicyVersion = persistentResult?.PolicyVersion ?? policy.Version,
                    reason = sellQuantityAdjustment is null
                        ? submission.Reason
                        : sellQuantityAdjustment + " " + submission.Reason,
                    Groups = persistentResult?.Groups
                             ?? Array.Empty<IReadOnlyList<OrderResult>>(),
                    persistentOrder = submission.Intent
                });
            }

            var result = await manager.ExecuteGroupsAsync(groups, source, authorization, ct);
            var brokerResults = result.Groups.SelectMany(group => group).ToList();
            var brokerAccepted = result.Executed
                && brokerResults.Count > 0
                && brokerResults.All(order => order.Success);
            return Results.Ok(new
            {
                accepted = brokerAccepted,
                result.IsReplay,
                result.ExecutionId,
                result.PolicyVersion,
                result.Reason,
                result.Groups
            });
        }).RequireAuthorization("TradingTrader");

        trading.MapGet("/persistent-orders", async (
            bool? all,
            ITradingRepository repository,
            CancellationToken ct) =>
        {
            var orders = await repository.GetPersistentOrdersAsync(openOnly: !(all ?? false), ct);
            var rows = new List<object>(orders.Count);
            foreach (var order in orders)
            {
                var placements = await repository.GetPersistentOrderPlacementsAsync(order.IntentId, ct);
                var canRetry = PersistentOrderDecisions.CanRetryFailedToday(
                    order, placements.LastOrDefault(), DateTime.UtcNow, PsxTime.Today(), out var retryReason);
                rows.Add(new
                {
                    order.IntentId,
                    order.Symbol,
                    order.Action,
                    order.Quantity,
                    order.OrderType,
                    order.Price,
                    order.LimitPrice,
                    order.ExpiresUtc,
                    order.State,
                    order.FilledQuantity,
                    order.RemainingQuantity,
                    lastAttemptSessionDate = order.LastAttemptSessionDate?.ToString("yyyy-MM-dd"),
                    order.AttemptCount,
                    order.LastOrderNo,
                    order.SourceArmedId,
                    order.StateReason,
                    order.Note,
                    order.CreatedUtc,
                    order.UpdatedUtc,
                    order.TerminalUtc,
                    canRetry,
                    retryReason,
                    placements
                });
            }
            return Results.Ok(new { orders = rows });
        }).RequireAuthorization("TradingAnalyst");

        trading.MapPost("/persistent-orders/{intentId}/retry", async (
            string intentId,
            PersistentOrderWorker worker,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await worker.RetryFailedTodayAsync(
                intentId,
                http.User.Identity?.Name ?? "trading-dashboard",
                ct);
            return !result.Found
                ? Results.NotFound(new { error = "not_found", message = result.Message })
                : Results.Ok(new
                {
                    intentId,
                    placed = result.Placed,
                    state = result.State,
                    message = result.Message,
                    executionId = result.ExecutionId
                });
        }).RequireAuthorization("TradingTrader");

        trading.MapDelete("/persistent-orders/{intentId}", async (
            string intentId,
            PersistentOrderWorker worker,
            CancellationToken ct) =>
        {
            var result = await worker.CancelAsync(intentId, ct);
            return result.State == "missing"
                ? Results.NotFound(new { error = "not_found", message = result.Message })
                : Results.Ok(new
                {
                    intentId,
                    completed = result.Completed,
                    state = result.State,
                    message = result.Message
                });
        }).RequireAuthorization("TradingTrader");
    }
}
