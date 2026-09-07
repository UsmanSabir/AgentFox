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
            // The ONLY sanctioned way to ask what is free to sell: it confirms a cached answer against
            // the broker whenever that answer would refuse or shrink the order. See its own doc.
            SellAvailabilityConfirmer sellAvailabilityConfirmer,
            TradingPolicyProvider policyProvider,
            ApprovalIntentRegistry intentRegistry,
            ITradingRepository repository,
            IOptions<TradingAgentOptions> options,
            ILogger<TradingCoreEndpoints> logger,
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
            // ── A sell whose shares are already committed, explained rather than bounced ──────────
            //
            // This broker sizes every SELL against custody MINUS the quantity already committed to
            // resting SELLs, so a protective stop covering the whole holding refuses even a sell that
            // only REDUCES the position. The operator is then refused on shares they can plainly see
            // they own, with nothing saying their own stop is the cause.
            //
            // Placed OUTSIDE the persistent branch below because the constraint is a fact about the
            // account, not about how the order is submitted. It lived inside it until 2026-09-03, so
            // an ordinary immediate sell — the common case — skipped the check entirely and met the
            // broker's own refusal instead.
            //
            // Every rule about WHEN a cached answer is good enough, and why it costs a broker read when
            // it is not, is in SellAvailabilityConfirmer and SellRefusalRule. Read them before changing
            // the shape of this. Asked ONCE for the whole request: the keep-working branch below sizes
            // from this same answer rather than taking its own reading of the same account.
            ConfirmedSellAvailability? sellAvailability = null;
            if (intent.Action.Equals("SELL", StringComparison.OrdinalIgnoreCase))
            {
                sellAvailability = await sellAvailabilityConfirmer.ForSellAsync(
                    symbol, body.Quantity.Value, ct);

                if (sellAvailability.BrokerConfirmed
                    && SellRefusalRule.MayRefuse(sellAvailability.Decision))
                    return await UnsellableHoldingConflictAsync(
                        symbol, sellAvailability, repository, ct);
            }

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
                    var availability = sellAvailability!;

                    // The clamp below is the QUIET half of the 2026-09-07 stale-snapshot incident: it
                    // reduces the order instead of refusing it, so a figure a poll interval out of date
                    // produced a smaller sell than was asked for and no error for anyone to notice. It
                    // may therefore only size on an answer that was either confirmed with the broker or
                    // already covered the request — which is exactly what the confirmer returns.
                    //
                    // A confirming read that FAILED sizes nothing here. Carrying the full quantity to
                    // the execution boundary is strictly better than clamping on a figure just shown to
                    // be untrustworthy: TradingManager re-reads the broker book for every independent
                    // SELL, so the order is still bounded by custody — just bounded by a current
                    // reading instead of a stale one. Same reasoning as the released-stop branch in
                    // WatchlistMonitorWorker, and the same reason this is not a refusal.
                    if (availability.MustDeferSizing)
                    {
                        logger.LogWarning(
                            "[TradingAgent] {Symbol} keep-working SELL of {Quantity} is sized at the "
                            + "execution boundary: {Why}",
                            symbol, effectiveQuantity, availability.ConfirmationFailure);
                    }
                    else if (!availability.Decision.Known)
                    {
                        // A keep-working order must not be built on an unknown availability: it
                        // re-places itself for days, so a wrong size outlives the reading that produced
                        // it. An immediate sell is left to the execution boundary instead.
                        return Results.Conflict(new
                        {
                            error = "sell_availability_unknown",
                            message = availability.Decision.Reason
                        });
                    }
                    else
                    {
                        effectiveQuantity = Math.Min(
                            effectiveQuantity, availability.Decision.AvailableQuantity);
                        if (effectiveQuantity != body.Quantity.Value)
                        {
                            sellQuantityAdjustment = new SellQuantityAdjustment(
                                0, 0, symbol, body.Quantity.Value, effectiveQuantity).Message;
                        }
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
                    Note = $"New Order: {intent.Label}",
                    // Submitted from the dashboard by a person: the re-placements this intent makes on
                    // later sessions are still their instruction, so they keep working on a
                    // manual-only symbol. See PersistentOrderIntent.OperatorOriginated.
                    OperatorOriginated = true
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

        trading.MapPost("/persistent-orders/{intentId}/resolve-attention", async (
            string intentId,
            ResolvePersistentAttentionRequest body,
            PersistentOrderWorker worker,
            HttpContext http,
            CancellationToken ct) =>
        {
            var resolution = body.Resolution?.Trim().ToLowerInvariant();
            if (resolution is not ("not_filled" or "partial" or "filled"))
                return Results.BadRequest(new
                {
                    error = "invalid_resolution",
                    message = "Choose not_filled, partial, or filled after checking the broker's own "
                        + "order history or statement for that trading date."
                });

            var note = body.Note?.Trim();
            if (string.IsNullOrWhiteSpace(note))
                return Results.BadRequest(new
                {
                    error = "resolution_note_required",
                    message = "Record what you checked at the broker before resolving an unobserved outcome."
                });

            var result = await worker.ResolveAttentionAsync(
                intentId, resolution, body.FilledQuantity, note,
                http.User.Identity?.Name ?? "trading-dashboard", ct);
            return !result.Found
                ? Results.NotFound(new { error = "not_found", message = result.Message })
                : Results.Ok(new
                {
                    intentId,
                    applied = result.Applied,
                    state = result.State,
                    message = result.Message
                });
        }).RequireAuthorization("TradingTrader");

        // Read-only: what the broker is resting for this intent's symbol, and which of those orders this
        // ledger can actually name. The operator-facing half of the orphan problem — where the evidence
        // cannot prove an order is ours, showing it beats cancelling it.
        trading.MapGet("/persistent-orders/{intentId}/broker-orders", async (
            string intentId,
            PersistentOrderWorker worker,
            CancellationToken ct) =>
        {
            var view = await worker.InspectBrokerOrdersAsync(intentId, ct);
            return view.State == "missing"
                ? Results.NotFound(new { error = "not_found", message = view.Message })
                : Results.Ok(new
                {
                    intentId,
                    read = view.Read,
                    state = view.State,
                    message = view.Message,
                    ours = view.Ours.Select(Project),
                    unclaimed = view.Unclaimed.Select(Project)
                });
        }).RequireAuthorization("TradingTrader");

        // Cancel ONE broker order by number, on the instruction of a person who has looked at the
        // broker's own book. Checked against that book before it is sent, so a mistyped number cannot
        // cancel an unrelated position.
        trading.MapPost("/persistent-orders/{intentId}/cancel-broker-order", async (
            string intentId,
            CancelBrokerOrderRequest request,
            PersistentOrderWorker worker,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await worker.CancelBrokerOrderAsync(
                intentId, request.OrderNo ?? "", http.User.Identity?.Name ?? "operator", ct);
            return result.State == "missing"
                ? Results.NotFound(new { error = "not_found", message = result.Message })
                : Results.Ok(new
                {
                    intentId,
                    applied = result.Applied,
                    state = result.State,
                    message = result.Message
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

    private static object Project(BrokerWorkingOrder order) => new
    {
        orderNo = order.OrderNo,
        symbol = order.Symbol,
        side = order.Side,
        remainingQuantity = order.RemainingQuantity,
        price = order.Price
    };

    /// <summary>
    /// This system's OWN protective stops that are currently resting for a symbol, which is the usual
    /// answer to "why can I not sell shares I hold".
    ///
    /// <para>
    /// Deliberately reports only stops with a placed quantity and a broker order number: those are the
    /// ones that actually commit shares at the venue and that the operator can disarm. A stop with no
    /// order behind it commits nothing and naming it would send them to cancel the wrong thing. An
    /// unreadable ledger returns an empty list, so the refusal falls back to its plain wording rather
    /// than failing — the sell is already refused, and a diagnostic must not turn that into a 500.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<BlockingStop>> BlockingStopsAsync(
        ITradingRepository repository,
        string symbol,
        CancellationToken ct)
    {
        try
        {
            return (await repository.GetProtectiveStopsAsync(openOnly: true, ct))
                .Where(s => s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                         && s.State == "active"
                         && s.PlacedQuantity > 0
                         && s.LastOrderNo is { Length: > 0 })
                .Select(s => new BlockingStop(
                    s.StopId,
                    s.LastOrderNo!,
                    s.PlacedQuantity,
                    s.StopTrigger,
                    $"a protective stop at {s.StopTrigger:0.##} (broker order {s.LastOrderNo}) is "
                    + $"holding {s.PlacedQuantity} share(s)"))
                .ToList();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>
    /// Builds the 409 for a sell the broker itself currently has nothing free for.
    ///
    /// <para>
    /// Takes an ALREADY-CONFIRMED availability rather than reading anything: the caller has established
    /// with a fresh broker read that there is genuinely nothing to sell, and the rules governing when
    /// that is allowed to happen live in <see cref="SellRefusalRule"/>. This method only explains it.
    /// </para>
    ///
    /// <para>
    /// It names the blockers from <see cref="ConfirmedSellAvailability.Snapshot"/> — the same read the
    /// number came from — so the sentence and the figure cannot disagree.
    /// </para>
    /// </summary>
    private static async Task<IResult> UnsellableHoldingConflictAsync(
        string symbol,
        ConfirmedSellAvailability availability,
        ITradingRepository repository,
        CancellationToken ct)
    {
        var blocking = await BlockingStopsAsync(repository, symbol, ct);
        var foreign = SellRefusalRule
            .RestingSellsNotPlacedHere(
                availability.Snapshot,
                symbol,
                blocking.Select(s => s.OrderNo.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase))
            .Select(o => new RestingSell(
                o.OrderNo.Trim(),
                o.RemainingQuantity!.Value,
                o.Price,
                $"a resting SELL order (broker order {o.OrderNo.Trim()}) is holding "
                + $"{o.RemainingQuantity!.Value:N0} share(s)"))
            .ToList();

        return Results.Conflict(new
        {
            error = "no_sellable_holding",
            message = SellRefusalRule.Compose(
                symbol,
                availability.Decision.Reason,
                blocking.Select(s => s.Describe).ToList(),
                foreign.Select(o => o.Describe).ToList()),
            basis = availability.Decision.Reason,
            checkedUtc = availability.Snapshot.CheckedUtc,
            blockingStops = blocking,
            restingSells = foreign
        });
    }

    /// <param name="Describe">Server-authored clause, shown verbatim inside the refusal sentence.</param>
    private sealed record BlockingStop(
        string StopId,
        string OrderNo,
        int Quantity,
        decimal Trigger,
        string Describe);

    /// <summary>
    /// A SELL resting at the broker that this system did not place. Reported ALONGSIDE
    /// <see cref="BlockingStop"/> rather than merged into it because only one of the two has a remedy on
    /// this screen: a stop we placed can be stood down for one sell, an order placed elsewhere can only
    /// be cancelled where it was placed. Merging them would offer a release button that cannot work.
    /// </summary>
    private sealed record RestingSell(
        string OrderNo,
        long Quantity,
        decimal? Price,
        string Describe);

    private sealed record CancelBrokerOrderRequest(string? OrderNo);
}
