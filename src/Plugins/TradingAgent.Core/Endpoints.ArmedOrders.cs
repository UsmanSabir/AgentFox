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
/// <c>/trading</c> endpoints for level-armed orders, protective stops, the approval window.
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
///   <item><description><c>/approval/arm</c></description></item>
///   <item><description><c>/approval/disarm</c></description></item>
///   <item><description><c>/armed-orders</c></description></item>
///   <item><description><c>/armed-orders/{armedId}</c></description></item>
///   <item><description><c>/protective-stops/{stopId}</c></description></item>
/// </list>
/// </summary>
public sealed partial class TradingCoreEndpoints
{
    private static void MapArmedOrdersEndpoints(RouteGroupBuilder trading)
    {
        // ── Armed orders: orders waiting on a level or an event ────────────────
        // Evaluated by the monitor pass. Prefer the broker's NATIVE stop where one fits: it rests at the
        // exchange and fires whether or not this process is running, whereas an armed order here only
        // fires while AgentFox is up and the market is open. The response says which kind you got.

        trading.MapGet("/armed-orders", async (
            bool? all,
            ITradingRepository repository,
            ApprovalGate approvals,
            IMarketCalendar calendar,
            IOptions<TradingAgentOptions> options,
            CancellationToken ct) =>
        {
            var orders = await repository.GetArmedOrdersAsync(armedOnly: !(all ?? false), ct);
            var stops  = await repository.GetProtectiveStopsAsync(openOnly: !(all ?? false), ct);
            var window = approvals.ArmedWindow;
            var pktNow = calendar.GetStatus().PktNow;
            return Results.Ok(new
            {
                // Projected rather than serialized straight from the record: an enum defaults to its
                // NUMBER on the wire, so the client would receive triggerKind: 0 and have to know the
                // ordinal. Names are the stable contract here.
                orders = orders.Select(o => new
                {
                    o.ArmedId,
                    o.Symbol,
                    triggerKind = o.TriggerKind.ToString(),
                    // The recomputed level, not the stored column: a trailing trigger's level moves,
                    // and a panel showing where it WAS is the one thing worse than showing nothing.
                    triggerPrice = o.EffectiveTriggerPrice,
                    triggerAlertKind = o.TriggerAlertKind?.ToString(),
                    o.TriggerPercent,
                    o.ReferencePrice,
                    o.Trailing,
                    o.Action,
                    o.Quantity,
                    o.OrderType,
                    o.Price,
                    o.LimitPrice,
                    o.State,
                    o.ArmedUtc,
                    o.ExpiresUtc,
                    o.FiredUtc,
                    o.ExecutionId,
                    o.StateReason,
                    o.Note,
                    o.SourceAlertId,
                    o.ProtectiveStopId,
                    o.PersistentUntilFilled
                }),
                // Sent with the orders because a stop and the entry it protects are one thing to
                // read: "did my buy fill, and is the stop actually at the broker" is a single
                // question, and answering half of it is how a position looks covered when it is not.
                protectiveStops = stops.Select(s => new
                {
                    s.StopId,
                    s.Symbol,
                    s.ParentArmedId,
                    s.StopTrigger,
                    s.StopLimit,
                    s.DesiredQuantity,
                    s.Recurring,
                    s.State,
                    s.BaselineQuantity,
                    s.PlacedQuantity,
                    lastPlacedSessionDate = s.LastPlacedSessionDate?.ToString("yyyy-MM-dd"),
                    s.LastOrderNo,
                    s.LocalBackstopArmedId,
                    s.CreatedUtc,
                    s.FillConfirmedUtc,
                    s.ClosedUtc,
                    s.StateReason,
                    s.Note,
                    // Whether a native stop is resting at the broker RIGHT NOW, which is the only
                    // form of protection that survives this process being down. A superseded row can
                    // still have its OLD native order resting until the worker confirms the cancel —
                    // see ProtectiveStopWorker.RetireSupersededAsync — so it counts here too.
                    restingToday = s.State is "active" or "superseded_pending_cancel"
                                   && s.LastPlacedSessionDate == DateOnly.FromDateTime(pktNow)
                                   && s.PlacedQuantity > 0
                }),
                // Surfaced together because an armed ORDER that cannot be approved will not fire, and
                // that pairing is the first thing to check when one does not.
                approval = new
                {
                    mode = options.Value.Approval.Mode,
                    armedUntilUtc = window?.UntilUtc,
                    armedBy = window?.By,
                    autoApprovedThisSession = approvals.AutoApprovedThisSession,
                    maxOrdersPerSession = options.Value.Approval.Auto.MaxOrdersPerSession
                },
                monitorRequired = true,
                caveat = "An armed order is evaluated by the monitor, so it only fires while AgentFox is "
                       + "running and the market is open. A native broker stop has neither limitation."
            });
        });

        trading.MapPost("/armed-orders", async (
            ArmOrderRequest body,
            ITradingRepository repository,
            MonitoredUniverse universe,
            ApprovalGate approvals,
            ProtectiveStopWorker protectiveStops,
            CompositeLiveQuoteSource quotes,
            IOptions<TradingAgentOptions> options,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            string symbol;
            try { symbol = PsxDataClient.NormalizeStockSymbol(body.Symbol ?? ""); }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_symbol", message = ex.Message });
            }

            var action = (body.Action ?? "").Trim().ToUpperInvariant();
            if (action is not ("BUY" or "SELL"))
                return Results.BadRequest(new { error = "invalid_action", message = "Action must be BUY or SELL." });

            if (body.Quantity is not > 0)
                return Results.BadRequest(new { error = "invalid_quantity", message = "Quantity must be positive." });

            if (!Enum.TryParse<ArmedTriggerKind>(body.TriggerKind, ignoreCase: true, out var kind))
                return Results.BadRequest(new
                {
                    error = "invalid_trigger",
                    message = $"Trigger kind must be one of {string.Join(", ", Enum.GetNames<ArmedTriggerKind>())}."
                });

            AlertKind? alertKind = null;
            decimal? triggerPercent = null;
            decimal? referencePrice = null;
            var trailing = false;
            var triggerLevel = body.TriggerPrice;

            if (kind == ArmedTriggerKind.Event)
            {
                if (!Enum.TryParse<AlertKind>(body.TriggerAlertKind, ignoreCase: true, out var parsed))
                    return Results.BadRequest(new
                    {
                        error = "invalid_alert_kind",
                        message = $"Event triggers need an alert kind: {string.Join(", ", Enum.GetNames<AlertKind>())}."
                    });
                alertKind = parsed;
                triggerLevel = null;
            }
            else if (PercentTrigger.IsPercent(kind))
            {
                // A percent trigger is "if it drops 3%", which needs a size of move and a price to
                // measure it from. The level is DERIVED from those two and stored alongside them, so a
                // reader that only knows about levels still sees a real number — see ArmedOrder.
                if (body.TriggerPercent is not > 0 || body.TriggerPercent > PercentTrigger.MaxPercent)
                    return Results.BadRequest(new
                    {
                        error = "invalid_trigger_percent",
                        message = $"A percent trigger needs a move between 0 and {PercentTrigger.MaxPercent}%."
                    });

                triggerPercent = Math.Round(body.TriggerPercent.Value, 2, MidpointRounding.AwayFromZero);

                // The caller normally sends the price it was showing the operator, so the level armed
                // is exactly the level they were quoted. Capturing it here is the fallback for a
                // scripted caller with no screen — one snapshot, and only on that path.
                referencePrice = body.ReferencePrice is > 0 ? body.ReferencePrice : null;
                if (referencePrice is null)
                {
                    try
                    {
                        var snapshot = await quotes.GetQuotesAsync(ct);
                        if (snapshot.Quotes.TryGetValue(symbol, out var quote) && quote.Current is > 0)
                            referencePrice = quote.Current;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex,
                            "[ArmedOrders] Could not capture a reference price for {Symbol}.", symbol);
                    }
                }

                if (referencePrice is not > 0)
                    return Results.BadRequest(new
                    {
                        error = "no_reference_price",
                        message = $"A percent trigger measures the move from a price, and no live price "
                                + $"for '{symbol}' is available right now. Send referencePrice, or arm a "
                                + "PriceBelow/PriceAbove trigger at an exact level instead."
                    });

                triggerLevel = PercentTrigger.Level(kind, referencePrice, triggerPercent);
                if (triggerLevel is not > 0)
                    return Results.BadRequest(new
                    {
                        error = "invalid_trigger_percent",
                        message = $"{triggerPercent}% from {referencePrice} does not produce a usable "
                                + "price level."
                    });

                trailing = body.Trailing;
            }
            else if (body.TriggerPrice is not > 0)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_trigger_price",
                    message = "A price trigger needs a positive level."
                });
            }

            // Refuse up front rather than at fire time. An armed order for a non-tradable symbol would
            // sit there looking like protection and be rejected by the risk engine the moment it
            // mattered — which is the failure mode worth designing out.
            if (!await universe.IsTradableAsync(symbol, ct))
                return Results.BadRequest(new
                {
                    error = "not_tradable",
                    message = $"'{symbol}' is not in the selected execution universe, so an order for it would be refused "
                            + "by the risk engine. Arming one would be protection in name only."
                });

            // Identical reasoning, different reason: an armed order exists precisely to fire while
            // nobody is watching, and that is what a manual-only symbol has switched off. Arming one
            // would leave a trigger that gets refused at the boundary the moment it mattered.
            if (await universe.IsManualOnlyAsync(symbol, ct))
                return Results.BadRequest(new
                {
                    error = "manual_only",
                    message = $"'{symbol}' is set to manual-only, so nothing may fire for it unattended — "
                            + "an armed order would be refused at the moment it triggered. Place the order "
                            + "yourself when the level comes, or switch automation back on for the symbol "
                            + "on the watchlist."
                });

            // Same reasoning as the tradability check above, applied to the stop's own geometry: an
            // armed stop whose limit sits on the wrong side of its trigger is refused by the risk
            // engine at fire time, which is the one moment it was supposed to work. Checking it here
            // means the order is either armed and fillable, or never armed at all.
            //
            // The direction comes from the TRIGGER, not the side — a BUY armed to fire on a falling
            // price wants its limit BELOW the trigger, and judging it by the side alone is what
            // refused a legitimate FFC dip-buy live on 2026-08-18. See StopLimitRule.
            var stopProblem = StopLimitRule.Validate(
                action,
                (body.OrderType ?? "LIMIT").Trim().ToUpperInvariant(),
                PercentTrigger.FiresOnRisingPrice(kind),
                body.Price,
                body.LimitPrice);

            if (stopProblem is not null)
                return Results.BadRequest(new
                {
                    error = "invalid_stop_limit",
                    message = $"This order would be refused when it fired: {stopProblem}"
                });

            if (body.PersistentUntilFilled
                && PersistentOrderDecisions.ValidateEligibility(body.OrderType) is { } persistenceProblem)
            {
                return Results.BadRequest(new
                {
                    error = "order_not_persistable",
                    message = persistenceProblem
                });
            }

            var order = new ArmedOrder
            {
                ArmedId          = Guid.NewGuid().ToString("N"),
                Symbol           = symbol,
                TriggerKind      = kind,
                TriggerPrice     = triggerLevel,
                TriggerAlertKind = alertKind,
                TriggerPercent   = triggerPercent,
                ReferencePrice   = referencePrice,
                Trailing         = trailing,
                Action           = action,
                Quantity         = body.Quantity!.Value,
                OrderType        = (body.OrderType ?? "LIMIT").Trim().ToUpperInvariant(),
                Price            = body.Price,
                LimitPrice       = body.LimitPrice,
                PersistentUntilFilled = body.PersistentUntilFilled,
                // Default an expiry: an entry trigger left open indefinitely can fire months later
                // against a thesis nobody remembers forming.
                ExpiresUtc       = body.ExpiresUtc
                                   ?? DateTime.UtcNow.AddDays(Math.Clamp(body.ExpiresInDays ?? 30, 1, 365)),
                Note             = body.Note,
                SourceAlertId    = body.SourceAlertId
            };

            // A stop may only be attached to a BUY: it protects a position this entry creates, and
            // attaching one to a SELL would arm a second sell of stock the first one is disposing of.
            ProtectiveStop? attached = null;
            if (body.AttachStop is { } attach)
            {
                if (action != "BUY")
                    return Results.BadRequest(new
                    {
                        error = "stop_requires_buy",
                        message = "A protective stop can only be attached to a BUY entry."
                    });

                if (attach.StopTrigger is not > 0)
                    return Results.BadRequest(new
                    {
                        error = "invalid_stop_trigger",
                        message = "A protective stop needs a positive trigger price."
                    });

                var entryPrice = order.Price ?? order.TriggerPrice;
                if (entryPrice is { } entry && attach.StopTrigger >= entry)
                    return Results.BadRequest(new
                    {
                        error = "stop_above_entry",
                        message = $"A protective stop at {attach.StopTrigger} sits at or above the entry "
                                + $"({entry}), so it would trigger immediately rather than protect anything."
                    });

                // Default the limit just below the trigger. A stop limit set exactly AT its trigger
                // routinely misses the move that triggered it, which is protection in name only.
                var stopLimit = attach.StopLimit
                    ?? Math.Round(attach.StopTrigger!.Value * 0.99m, 2, MidpointRounding.AwayFromZero);

                if (stopLimit > attach.StopTrigger)
                    return Results.BadRequest(new
                    {
                        error = "invalid_stop_limit",
                        message = $"A SELL stop's limit ({stopLimit}) must be at or below its trigger "
                                + $"({attach.StopTrigger}), or it cannot fill once triggered."
                    });

                attached = new ProtectiveStop
                {
                    StopId        = Guid.NewGuid().ToString("N"),
                    Symbol        = symbol,
                    ParentArmedId = order.ArmedId,
                    StopTrigger   = attach.StopTrigger!.Value,
                    StopLimit     = stopLimit,
                    // Sized at fill time, never here: what matters is what the entry actually buys.
                    DesiredQuantity = 0,
                    Recurring     = attach.Recurring,
                    State         = "pending_fill",
                    Note          = attach.Quantity is { } wanted
                                        ? $"Requested cover: {wanted} share(s)."
                                        : null
                };
            }

            var id = await repository.SaveArmedOrderAsync(order, ct);
            if (attached is not null)
            {
                await repository.SaveProtectiveStopAsync(attached, ct);

                // Not awaited, on purpose. A fill is proved by holdings RISING, which needs the
                // holding from before the entry went in — and an entry armed at a level the price is
                // already touching can trigger before the periodic pass ever takes that reading.
                // Kicking it here shrinks the window from minutes to seconds without making the
                // operator wait on a page scrape.
                protectiveStops.CaptureBaselineSoon(attached.StopId);

                logger.LogWarning(
                    "[ProtectiveStops] {StopId} attached to entry {ArmedId}: SELL stop {Trigger}/{Limit} "
                    + "on {Symbol}, recurring={Recurring}.",
                    attached.StopId, id, attached.StopTrigger, attached.StopLimit, symbol,
                    attached.Recurring);
            }

            var unattended = approvals.DescribeUnattendedPolicy();
            logger.LogWarning(
                "[ArmedOrders] Armed {ArmedId}: {Action} {Qty} {Symbol} on {Kind} {Level}{Basis}. "
                + "Approval mode {Mode}.",
                id, action, order.Quantity, symbol, kind,
                order.TriggerPrice?.ToString() ?? alertKind?.ToString() ?? "",
                triggerPercent is null
                    ? ""
                    : $" ({triggerPercent}% from {referencePrice}"
                      + (trailing ? ", trailing" : "") + ")",
                options.Value.Approval.Mode);

            return Results.Ok(new
            {
                armedId = id,
                order = new
                {
                    order.ArmedId,
                    order.Symbol,
                    triggerKind = order.TriggerKind.ToString(),
                    order.TriggerPrice,
                    triggerAlertKind = order.TriggerAlertKind?.ToString(),
                    order.TriggerPercent,
                    order.ReferencePrice,
                    order.Trailing,
                    order.PersistentUntilFilled,
                    order.Action,
                    order.Quantity,
                    order.OrderType,
                    order.Price,
                    order.LimitPrice,
                    order.State,
                    order.ArmedUtc,
                    order.ExpiresUtc,
                    order.Note
                },
                // Said plainly at arm time, not discovered at fire time — and asked of the gate rather
                // than re-derived here, since the execution mode matters as much as the approval mode.
                willFireUnattended = unattended.WillFireUnattended,
                note = unattended.Explanation,
                attachedStop = attached is null ? null : new
                {
                    attached.StopId,
                    attached.StopTrigger,
                    attached.StopLimit,
                    attached.Recurring,
                    attached.State,
                    // The honest version of "and then it's protected". Each clause is a real gap the
                    // operator would otherwise find out about from a position that was not covered.
                    note = "The stop is dormant until the entry is confirmed filled by an increase in "
                         + "your holdings, which is checked every few minutes while the market is open. "
                         + (attached.Recurring
                                ? "It is then re-placed at the broker each session, because outstanding "
                                + "orders are cleared at the close."
                                : "It is placed once and NOT re-placed after that session, so the "
                                + "position stops being protected the next day.")
                }
            });
        }).RequireAuthorization("TradingTrader");

        trading.MapDelete("/armed-orders/{armedId}", async (
            string armedId,
            ITradingRepository repository,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            var cancelled = await repository.TrySetArmedOrderStateAsync(
                armedId, "armed", "cancelled", "Disarmed by the operator.", ct: ct);
            if (cancelled) logger.LogWarning("[ArmedOrders] {ArmedId} disarmed.", armedId);
            return cancelled
                ? Results.Ok(new { armedId, state = "cancelled" })
                : Results.NotFound(new
                {
                    error = "not_armed",
                    message = "No armed order with that id is currently armed (it may have already fired)."
                });
        }).RequireAuthorization("TradingAnalyst");

        // A stop is disarmed rather than cancelled, and the difference is not pedantry: this broker
        // exposes no way to retract a resting order, so an order already at the exchange stays there.
        // Reporting "cancelled" would be a lie about a live sell order.
        trading.MapDelete("/protective-stops/{stopId}", async (
            string stopId,
            ITradingRepository repository,
            IMarketCalendar calendar,
            IBrokerOrderCanceller canceller,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            var stop = (await repository.GetProtectiveStopsAsync(openOnly: true, ct))
                .FirstOrDefault(s => s.StopId == stopId);

            if (stop is null)
                return Results.NotFound(new
                {
                    error = "not_found",
                    message = "No open protective stop with that id."
                });

            var restingToday = stop.State == "active"
                               && stop.LastPlacedSessionDate == DateOnly.FromDateTime(calendar.GetStatus().PktNow)
                               && stop.PlacedQuantity > 0;

            // Best-effort broker-side cancel before closing the row. Unlike an automated supersede,
            // this never blocks on the result: the operator asked to disarm, and refusing that because
            // a cancel could not be confirmed would trap them with a stop they explicitly want gone.
            string? cancelNote = null;
            if (restingToday && stop.LastOrderNo is { Length: > 0 } orderNo)
            {
                try
                {
                    var result = await canceller.CancelOrderAsync(orderNo, ct);
                    cancelNote = result.Gone
                        ? $"Broker order {orderNo} confirmed cancelled."
                        : $"Broker order {orderNo} could NOT be confirmed cancelled: {result.Message} " +
                          "It may still be resting until the exchange clears it at the close.";
                    if (!result.Gone)
                        logger.LogWarning(
                            "[ProtectiveStops] {StopId} ({Symbol}) disarmed but broker order {OrderNo} " +
                            "was not confirmed cancelled: {Message}",
                            stopId, stop.Symbol, orderNo, result.Message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    cancelNote = $"Broker order {orderNo} cancel attempt failed: {ex.Message} " +
                                 "It may still be resting until the exchange clears it at the close.";
                    logger.LogWarning(ex,
                        "[ProtectiveStops] {StopId} ({Symbol}) disarmed but cancelling broker order " +
                        "{OrderNo} threw.", stopId, stop.Symbol, orderNo);
                }
            }

            await repository.TrySetProtectiveStopStateAsync(
                stopId, stop.State, "closed", "Disarmed by the operator.", ct);

            if (stop.LocalBackstopArmedId is { } backstopId)
                await repository.TrySetArmedOrderStateAsync(
                    backstopId, "armed", "cancelled",
                    "The protective stop it backed was disarmed by the operator.", ct: ct);

            logger.LogWarning(
                "[ProtectiveStops] {StopId} ({Symbol}) disarmed. Native order resting today: {Resting}.",
                stopId, stop.Symbol, restingToday);

            return Results.Ok(new
            {
                stopId,
                state = "closed",
                brokerOrderStillResting = restingToday,
                message = cancelNote is not null
                    ? $"This system will no longer manage the stop. {cancelNote}"
                    : "Disarmed. No native stop was resting at the broker for this session."
            });
        }).RequireAuthorization("TradingAnalyst");

        // ── Approval window ───────────────────────────────────────────────────

        trading.MapPost("/approval/arm", (
            ArmApprovalRequest? body,
            ApprovalGate approvals,
            IMarketCalendar calendar,
            IOptions<TradingAgentOptions> options,
            HttpContext http) =>
        {
            var until = approvals.Arm(body?.Minutes, http.User.Identity?.Name ?? "operator");

            // Report what is actually IN FORCE, not merely what was granted. Arming disarms itself while
            // the market is closed, so returning the granted window alone would tell the caller they are
            // armed when the gate considers them not — the same "green tick that means nothing" the
            // order-book verification exists to avoid.
            var effective = approvals.ArmedWindow;
            var mode = options.Value.Approval.Mode;
            var marketOpen = calendar.GetStatus().IsOpen;

            return Results.Ok(new
            {
                grantedUntilUtc = until,
                inForce = effective is not null,
                armedUntilUtc = effective?.UntilUtc,
                note = effective is not null
                    ? null
                    : !marketOpen && options.Value.Approval.Window.DisarmAtMarketClose
                        ? "Granted, but NOT in force: the market is closed and arming does not survive "
                        + "the close. It will need re-arming during a session."
                        : !mode.Equals("Armed", StringComparison.OrdinalIgnoreCase)
                            ? $"Granted, but approval mode is '{mode}' — only 'Armed' mode consults the "
                            + "window. Set Approval.Mode to Armed for this to have any effect."
                            : "Granted, but not currently in force."
            });
        }).RequireAuthorization("TradingRiskManager");

        trading.MapPost("/approval/disarm", (
            ApprovalGate approvals,
            HttpContext http) =>
        {
            approvals.Disarm(http.User.Identity?.Name ?? "operator");
            return Results.Ok(new { armed = false });
        }).RequireAuthorization("TradingAnalyst");

    }
}
