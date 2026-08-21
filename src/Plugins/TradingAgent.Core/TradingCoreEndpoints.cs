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
/// The <c>/trading</c> management API. Split out of the entry module so every edition serves the
/// same endpoints from the same code — an edition adds routes, it does not re-map these.
///
/// <para>
/// Mapping one of these routes a second time does not override it: two endpoints with the same
/// template, method, and precedence make the request ambiguous and routing throws at request time.
/// Premium behavior therefore arrives through dependency injection (a provider a handler already
/// consults), not by shadowing a route.
/// </para>
/// </summary>
public sealed class TradingCoreEndpoints
{
    /// <summary>Not instantiable — every member is static. The type exists as a type so it can
    /// serve as this code's <c>ILogger&lt;T&gt;</c> category, which a static class cannot do.</summary>
    private TradingCoreEndpoints() { }

    public static void MapCoreEndpoints(IEndpointRouteBuilder endpoints)
    {
        var trading = endpoints.MapGroup("/trading")
            .RequireAuthorization("ManagementViewer");

        trading.MapGet("/status", async (
            ITradingRepository repository,
            TradingPolicyProvider policyProvider,
            IMarketCalendar calendar,
            TradingReconciliationState reconciliation,
            IOptions<TradingAgentOptions> options,
            CancellationToken ct) =>
        {
            var ledger = await repository.GetStatusAsync(ct);
            var policy = policyProvider.Current();
            var market = calendar.GetStatus();
            var brokerState = reconciliation.Current;
            var configured = options.Value;
            var reconciliationFresh = DateTime.UtcNow - brokerState.CheckedUtc
                <= TimeSpan.FromSeconds(Math.Max(10, configured.ReconciliationMaxAgeSeconds));
            var liveMode = policy.ExecutionMode.Equals("ApprovalRequired", StringComparison.OrdinalIgnoreCase)
                || policy.ExecutionMode.Equals("BoundedAuto", StringComparison.OrdinalIgnoreCase);
            return Results.Ok(new
            {
                policy,
                ledger,
                market,
                reconciliation = brokerState,
                killSwitch = policy.KillSwitch,
                reconciliationFresh,
                liveExecutionReady = liveMode
                    && policy.AutoExecute
                    && !policy.KillSwitch
                    && brokerState.Supported
                    && brokerState.Healthy
                    && reconciliationFresh,
                checkedUtc = DateTime.UtcNow
            });
        });

        trading.MapGet("/account", async (
            IBrokerAccountReader accountReader,
            CancellationToken ct) =>
            Results.Ok(await accountReader.ReadAccountAsync(ct)))
            .RequireAuthorization("TradingTrader");

        // Explicitly user-initiated: unlike the passive timer, this may harvest the authenticated
        // browser cookies (or log in when necessary), then reconciles immediately on that same direct
        // API session. This closes the confusing gap where "the browser is logged in" but the passive
        // reconciliation reader has never been given a session of its own.
        trading.MapPost("/reconciliation/run", async (
            AhkPortalClient brokerPortal,
            BrokerReconciliationWorker worker,
            CancellationToken ct) =>
        {
            var sessionEstablished = await brokerPortal.EnsureSessionAsync(ct);
            var snapshot = await worker.RunNowAsync(ct);
            return Results.Ok(new { sessionEstablished, reconciliation = snapshot });
        }).RequireAuthorization("TradingTrader");

        // Dedicated, no-restart kill switch: flips the runtime policy overlay (same store the
        // generic /plugin-config/trading-agent editor writes to) so TradingRiskEngine picks it up
        // on the very next order via TradingPolicyProvider — nothing to restart.
        trading.MapPost("/kill-switch", async (
            KillSwitchRequest body,
            PluginConfigManager configManager,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            await configManager.MergeConfigAsync("trading-agent", new Dictionary<string, object?>
            {
                ["killSwitch"] = body.Active
            });
            logger.LogWarning("[TradingAgent] Kill switch {State} via web API. Reason: {Reason}",
                body.Active ? "ACTIVATED" : "cleared", body.Reason ?? "(none given)");
            return Results.Ok(new { killSwitch = body.Active });
        }).RequireAuthorization("ManagementAdministrator");

        // The dashboard speaks in outcomes ("book profit", "sell if it drops") rather than leaking
        // broker vocabulary into every button. This registry is the single contract for those choices.
        trading.MapGet("/order-intents", (IRuntimePluginOptions<AhkConfig> brokerConfig) =>
            Results.Ok(new
            {
                intents = OrderIntentRegistry.All,
                capabilities = new
                {
                    marketOrdersEnabled = brokerConfig.Current.AllowMarketOrders,
                    brokerOrderTypes = new[] { "LIMIT", "MARKET", "STOPLOSS" },
                    conditionalTriggerTypes = Enum.GetNames<ArmedTriggerKind>()
                }
            }));

        // An authenticated, human-friendly adapter over the existing deterministic manager. Policy,
        // reconciliation, market window, risk caps, idempotency, and the kill switch remain downstream.
        trading.MapPost("/orders", async (
            DashboardOrderRequest body,
            MonitoredUniverse universe,
            TradingAgent.Manager.TradingManager manager,
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

            if (!universe.IsTradable(symbol))
                return Results.BadRequest(new
                {
                    error = "not_tradable",
                    message = $"'{symbol}' is not in AllowedSymbols, so the risk engine will not trade it."
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
                RawMessage = $"dashboard:{intent.Id}"
            };

            IReadOnlyList<IReadOnlyList<TradingSignal>> groups = [[signal]];
            var policy = policyProvider.Current();
            var source = "dashboard-order:" + (
                string.IsNullOrWhiteSpace(body.ClientRequestId)
                    ? Guid.NewGuid().ToString("N")
                    : body.ClientRequestId.Trim());

            // The TradingTrader request is the approval event. Bind it to the exact order and let the
            // manager consume/re-hash the one-time intent exactly as it does for a host tool approval.
            var approvalIntent = ApprovalIntent.Create(
                groups, source, policy.Version,
                TimeSpan.FromSeconds(Math.Max(10, options.Value.ApprovalIntentTtlSeconds)));
            intentRegistry.Register(approvalIntent);
            var authorization = ExecutionAuthorization.HostToolGate(
                http.User.Identity?.Name ?? "trading-dashboard", approvalIntent);

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
                    o.ProtectiveStopId
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
                    // form of protection that survives this process being down.
                    restingToday = s.State == "active"
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
            if (!universe.IsTradable(symbol))
                return Results.BadRequest(new
                {
                    error = "not_tradable",
                    message = $"'{symbol}' is not in AllowedSymbols, so an order for it would be refused "
                            + "by the risk engine. Arming one would be protection in name only."
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

            await repository.TrySetProtectiveStopStateAsync(
                stopId, stop.State, "closed", "Disarmed by the operator.", ct);

            if (stop.LocalBackstopArmedId is { } backstopId)
                await repository.TrySetArmedOrderStateAsync(
                    backstopId, "armed", "cancelled",
                    "The protective stop it backed was disarmed by the operator.", ct: ct);

            var restingToday = stop.State == "active"
                               && stop.LastPlacedSessionDate == DateOnly.FromDateTime(calendar.GetStatus().PktNow)
                               && stop.PlacedQuantity > 0;

            logger.LogWarning(
                "[ProtectiveStops] {StopId} ({Symbol}) disarmed. Native order resting today: {Resting}.",
                stopId, stop.Symbol, restingToday);

            return Results.Ok(new
            {
                stopId,
                state = "closed",
                brokerOrderStillResting = restingToday,
                message = restingToday
                    ? $"This system will no longer manage the stop, but a native SELL stop for "
                    + $"{stop.PlacedQuantity} {stop.Symbol} was placed at the broker today and CANNOT be "
                    + "cancelled from here. Cancel it in the portal if you do not want it to fire."
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

        // ── Alerts ────────────────────────────────────────────────────────────
        // What the monitor noticed. Read-only for viewers; acknowledging or dismissing is an analyst
        // action because it changes what the next person sees.

        trading.MapGet("/alerts", async (
            string? symbol,
            string? state,
            int? limit,
            ITradingRepository repository,
            CancellationToken ct) =>
            Results.Ok(await repository.GetAlertsAsync(symbol, state, limit ?? 100, ct)));

        trading.MapGet("/monitor/status", (
            WatchlistMonitorWorker monitor,
            AlertBroadcaster broadcaster) => Results.Ok(new
            {
                monitor.Status.Enabled,
                monitor.Status.MarketOpen,
                monitor.Status.LastPassUtc,
                monitor.Status.LastPassMs,
                monitor.Status.SymbolsCovered,
                monitor.Status.AlertsRaised,
                monitor.Status.AlertsSuppressed,
                // The effective settings it is running with, so "why did it not alert" is answerable
                // from the UI instead of by reading JSON on disk.
                monitor.Status.IntervalSeconds,
                monitor.Status.ConfirmPasses,
                monitor.Status.Trigger,
                monitor.Status.Warnings,
                monitor.Status.Message,
                liveSubscribers = broadcaster.SubscriberCount
            }));

        // Live broker-feed health. Every failure mode of the feed is silent — a lost subscription, a
        // dead session and a quiet market all look like "no quotes" — so this is the surface that
        // tells them apart without reading Debug logs.
        trading.MapGet("/feed/status", (AhkFeedWorker feed) => Results.Ok(feed.GetStatus()));

        // Depth for the currently followed symbol. GET never changes the subscription. The explicit
        // POST below delegates to the same get_market_depth tool used by chat, which keeps one
        // subscription/action path and gives operators a deterministic diagnostic surface.
        trading.MapGet("/feed/depth", (AhkDepthBook depth, AhkFeedWorker feed, string? symbol) =>
        {
            var target = (symbol ?? depth.SubscribedSymbol)?.Trim().ToUpperInvariant();
            if (target is null) return Results.Ok(new { subscribed = (string?)null, rows = 0 });

            var entry = depth.Get("REG", target);
            return Results.Ok(new
            {
                symbol = target,
                subscribed = depth.SubscribedSymbol,
                marketStatus = feed.MarketStatus,
                bestBid = entry?.BestBid,
                bestAsk = entry?.BestAsk,
                spread = entry?.Spread,
                totalBidVolume = entry?.TotalBidVolume,
                totalAskVolume = entry?.TotalAskVolume,
                imbalance = entry?.Imbalance,
                levels = entry?.Levels,
                orders = entry?.Orders,
                levelsAtUtc = entry?.LevelsAtUtc,
                ordersAtUtc = entry?.OrdersAtUtc,
                totalRowsEverSeen = depth.RowsSeen
            });
        });

        trading.MapPost("/feed/depth/focus", async (
            MarketDepthTool tool,
            string symbol,
            int? waitSeconds) =>
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["symbol"] = symbol,
                ["wait_seconds"] = waitSeconds ?? 6
            });

            return result.Success
                ? Results.Content(result.Output, "application/json")
                : Results.BadRequest(new { error = result.Error ?? result.Output });
        }).RequireAuthorization("TradingAnalyst");

        // ── Market movers (AHL analytics) ──────────────────────────────────────
        // One snapshot fetch backs every screen, so the dashboard can poll a few of these without
        // multiplying upstream traffic — AhlAnalyticsClient caches the snapshot for its configured TTL.
        trading.MapGet("/movers", async (
            AhlAnalyticsClient analytics,
            AhkPortalClient brokerPortal,
            string? screen,
            string? index,
            string? sectorCode,
            int? limit,
            decimal? minTurnover,
            decimal? minPrice,
            CancellationToken ct) =>
        {
            if (!analytics.Enabled)
                return Results.Ok(new { enabled = false, rows = Array.Empty<object>() });

            var parsed = AhlMovers.ParseScreen(screen ?? "gainers");
            if (parsed is null)
                return Results.BadRequest(new { error = $"Unknown screen '{screen}'.", valid = AhlMovers.ScreenNames });

            // A passive dashboard must never CREATE a broker session. Once AhkFeed already has one,
            // however, the SSO hop is just an authenticated GET and cannot cost another login. This
            // closes the gap where the screen claimed "no portal session" beside a healthy live feed.
            var snapshot = await analytics.GetMarketSnapshotAsync(
                allowHandshake: brokerPortal.HasSession, ct: ct);
            if (snapshot is null)
            {
                // Report WHY. "Could not be reached" covers no-session, a throttle and a rejected
                // POST, and those need different responses from the operator — hasToken alone
                // distinguishes the first from the rest.
                return Results.Ok(new
                {
                    enabled = true,
                    available = false,
                    hasToken = analytics.HasToken,
                    brokerSessionAvailable = brokerPortal.HasSession,
                    handshakeCoolingDown = analytics.HandshakeInCooldown,
                    error = analytics.LastError,
                    rows = Array.Empty<object>()
                });
            }

            var filter = new AhlMovers.Filter(index, sectorCode, minTurnover, null, minPrice);
            return Results.Ok(new
            {
                enabled = true,
                available = true,
                screen = parsed.Value.ToString(),
                marketState = snapshot.MarketState,
                asOf = snapshot.LastUpdate,
                breadth = AhlMovers.MarketBreadth(snapshot),
                rows = AhlMovers.Run(snapshot, parsed.Value, limit ?? 15, filter)
            });
        });

        // Sector rotation for the same session, from the same cached snapshot.
        trading.MapGet("/movers/sectors", async (
            AhlAnalyticsClient analytics,
            AhkPortalClient brokerPortal,
            string? index,
            CancellationToken ct) =>
        {
            if (!analytics.Enabled)
                return Results.Ok(new { enabled = false, sectors = Array.Empty<object>() });

            var snapshot = await analytics.GetMarketSnapshotAsync(
                allowHandshake: brokerPortal.HasSession, ct: ct);
            if (snapshot is null)
                return Results.Ok(new
                {
                    enabled = true,
                    available = false,
                    hasToken = analytics.HasToken,
                    brokerSessionAvailable = brokerPortal.HasSession,
                    handshakeCoolingDown = analytics.HandshakeInCooldown,
                    error = analytics.LastError,
                    sectors = Array.Empty<object>()
                });

            return Results.Ok(new
            {
                enabled = true,
                available = true,
                marketState = snapshot.MarketState,
                asOf = snapshot.LastUpdate,
                sectors = AhlMovers.SectorRotation(snapshot, new AhlMovers.Filter(index))
            });
        });

        // What the agent is doing right now, and what it just did.
        //
        // The status endpoints above each answer for ONE subsystem and answer in state ("healthy",
        // "12 symbols"). This answers in events, across all of them, in order — which is the only
        // form that says whether the thing that just happened on screen (a browser window opening,
        // an order not appearing) was this system's doing and why.
        //
        // The counts always describe the whole retained window regardless of `limit`, so a collapsed
        // panel can show an issue badge while asking for a single entry. `afterSeq` is offered for a
        // caller that only wants what is new — but note the log folds a repeated activity into its
        // existing entry, so a live view should read the whole window rather than merge deltas.
        trading.MapGet("/activity", (
            TradingActivityLog activity,
            AhkBroker broker,
            AhkFeedWorker feed,
            WatchlistMonitorWorker monitor,
            IMarketCalendar calendar,
            long? afterSeq,
            int? limit) =>
        {
            var (warnings, errors) = activity.IssueCounts();
            var market = calendar.GetStatus();

            return Results.Ok(new
            {
                lastSeq = activity.LastSeq,
                warnings,
                errors,
                retentionMinutes = (int)TradingActivityLog.Retention.TotalMinutes,
                now = new
                {
                    // The single most useful "right now" fact: whether a browser window on screen is
                    // this system driving the portal.
                    browserBusy  = broker.BrowserHoldsTradingScreen,
                    marketOpen   = market.IsOpen,
                    marketReason = market.Reason,
                    feedHealthy  = feed.GetStatus().Healthy,
                    monitorLastPassUtc = monitor.Status.LastPassUtc
                },
                activities = activity.Snapshot(afterSeq ?? 0, limit ?? TradingActivityLog.Capacity)
            });
        });

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

        // ── Proposals: the signal inbox ────────────────────────────────────────
        // A proposal is what the specialist produced from a signal that arrived while nobody was
        // watching (a WhatsApp tip overnight). It used to be write-only — created, listed, never
        // resolved — so the table only grew. It now has a lifecycle:
        //   proposed → executing → executed | rejected | expired

        trading.MapGet("/proposals", async (
            int? limit,
            bool? openOnly,
            ITradingRepository repository,
            CancellationToken ct) =>
            // Open-only by default: an empty inbox is the normal state, and a list dominated by
            // last month's resolved proposals is what made this feel like a log.
            Results.Ok(openOnly ?? true
                ? await repository.GetOpenProposalsAsync(ct)
                : await repository.GetProposalsAsync(limit ?? 100, ct)));

        trading.MapPost("/proposals/{proposalId}/execute", async (
            string proposalId,
            ITradingRepository repository,
            TradingAgent.Manager.TradingManager manager,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            var proposal = await repository.GetProposalAsync(proposalId, ct);
            if (proposal is null)
                return Results.NotFound(new { error = "unknown_proposal", proposalId });

            if (proposal.Status is "executed" or "rejected" or "expired")
                return Results.Conflict(new
                {
                    error = "already_resolved",
                    proposalId,
                    status = proposal.Status,
                    message = $"This proposal is already {proposal.Status}"
                            + (proposal.StateReason is { } r ? $": {r}" : ".")
                });

            // Claim it before touching the broker. The compare-and-set is what stops a double click
            // from submitting the same orders twice — whoever loses the race gets the conflict below
            // rather than a second live order.
            if (!await repository.TrySetProposalStateAsync(
                    proposalId, proposal.Status, "executing", ct: ct))
                return Results.Conflict(new
                {
                    error = "already_claimed",
                    proposalId,
                    message = "Another request is already executing this proposal."
                });

            var orders = ParseProposalOrders(proposal.Proposal);
            if (orders.Count == 0)
            {
                await repository.TrySetProposalStateAsync(
                    proposalId, "executing", "rejected",
                    "The proposal contains no executable orders.", ct: ct);
                return Results.BadRequest(new
                {
                    error = "no_orders",
                    message = "The proposal contains no orders that could be executed."
                });
            }

            try
            {
                // Straight through the deterministic manager: policy, calendar, risk engine, kill
                // switch, idempotency and audit all still apply. This endpoint adds no execution path,
                // it only supplies the orders a human approved.
                // Each order as its own group: they are independent, so one failing must not skip the
                // rest (grouping means "stop at the first failure", which is for a buy→sell pair).
                var groups = orders.Select(o => (IReadOnlyList<TradingSignal>)[o]).ToList();
                var result = await manager.ExecuteGroupsAsync(
                    groups, $"proposal:{proposalId}", ct: ct);

                await repository.TrySetProposalStateAsync(
                    proposalId, "executing",
                    result.Executed ? "executed" : "proposed",
                    result.Executed ? null : $"Execution refused: {result.Reason}",
                    string.IsNullOrWhiteSpace(result.ExecutionId) ? null : result.ExecutionId, ct);

                logger.LogInformation(
                    "[Trading] Proposal {ProposalId} execution {Outcome} (execution {ExecutionId}).",
                    proposalId, result.Executed ? "accepted" : "refused", result.ExecutionId);

                return Results.Ok(new
                {
                    proposalId,
                    // Refused returns to 'proposed' deliberately: the reason is usually transient
                    // (market closed, reconciliation stale, approval required), so the proposal stays
                    // actionable rather than being burned by a failed attempt.
                    status = result.Executed ? "executed" : "proposed",
                    accepted = result.Executed,
                    isReplay = result.IsReplay,
                    executionId = result.ExecutionId,
                    reason = result.Reason
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await repository.TrySetProposalStateAsync(
                    proposalId, "executing", "proposed", $"Execution failed: {ex.Message}", ct: ct);
                logger.LogError(ex, "[Trading] Proposal {ProposalId} execution failed.", proposalId);
                return Results.Problem(title: "execution_failed", detail: ex.Message, statusCode: 502);
            }
        }).RequireAuthorization("TradingTrader");

        trading.MapPost("/proposals/{proposalId}/reject", async (
            string proposalId,
            ProposalRejectRequest? body,
            ITradingRepository repository,
            ILogger<TradingCoreEndpoints> logger,
            CancellationToken ct) =>
        {
            var proposal = await repository.GetProposalAsync(proposalId, ct);
            if (proposal is null)
                return Results.NotFound(new { error = "unknown_proposal", proposalId });

            var moved = await repository.TrySetProposalStateAsync(
                proposalId, proposal.Status, "rejected",
                body?.Reason ?? "Rejected by the operator.", ct: ct);

            if (!moved)
                return Results.Conflict(new { error = "already_resolved", status = proposal.Status });

            logger.LogInformation("[Trading] Proposal {ProposalId} rejected: {Reason}",
                proposalId, body?.Reason ?? "(no reason given)");
            return Results.Ok(new { proposalId, status = "rejected" });
        }).RequireAuthorization("TradingAnalyst");

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

    // The REST endpoints use ASP.NET's web JSON defaults (camelCase). Keep the SSE contract identical:
    // serializing with the reflection defaults produces PascalCase properties that the browser client
    // cannot read even though the event itself arrives successfully.
    internal static string SerializeAlertForSse(AlertRecord alert) =>
        JsonSerializer.Serialize(alert, JsonSerializerOptions.Web);

    /// <summary>
    /// Reads the executable orders out of a stored proposal.
    ///
    /// <para>
    /// The proposal JSON is authored by the specialist, so this is deliberately forgiving about shape
    /// but strict about substance: anything without a symbol and a BUY/SELL action is skipped rather
    /// than guessed at. Nothing here bypasses validation — the risk engine still re-checks every field
    /// before an order reaches the broker.
    /// </para>
    /// </summary>
    private static List<TradingSignal> ParseProposalOrders(JsonElement proposal)
    {
        var orders = new List<TradingSignal>();
        if (!proposal.TryGetProperty("orders", out var array) || array.ValueKind != JsonValueKind.Array)
            return orders;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            string? Text(params string[] names)
            {
                foreach (var n in names)
                {
                    if (item.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString();
                }
                return null;
            }

            decimal? Number(params string[] names)
            {
                foreach (var n in names)
                {
                    if (!item.TryGetProperty(n, out var v)) continue;
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
                    if (v.ValueKind == JsonValueKind.String
                        && decimal.TryParse(v.GetString(), NumberStyles.Any,
                            CultureInfo.InvariantCulture, out var parsed)) return parsed;
                }
                return null;
            }

            var action = Text("action", "side")?.Trim().ToUpperInvariant();
            var symbol = Text("symbol", "scrip")?.Trim().ToUpperInvariant();
            if (action is not ("BUY" or "SELL") || string.IsNullOrWhiteSpace(symbol)) continue;

            orders.Add(new TradingSignal
            {
                IsSignal   = true,
                Action     = action,
                Symbol     = symbol,
                Quantity   = (int?)Number("quantity", "qty", "volume"),
                EntryPrice = Number("entry_price", "entryPrice", "price", "trigger"),
                LimitPrice = Number("limit_price", "limitPrice"),
                Target     = Number("target", "take_profit"),
                StopLoss   = Number("stop_loss", "stopLoss", "stop"),
                OrderType  = (Text("order_type", "orderType") ?? "LIMIT").Trim().ToUpperInvariant()
            });
        }

        return orders;
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

public sealed record KillSwitchRequest(bool Active, string? Reason = null);

/// <summary>A ticker to add to the monitoring watchlist.</summary>
public sealed record WatchlistSymbolRequest(string? Symbol);

/// <summary>An ad-hoc assessment request from the chart pane.</summary>
public sealed record AssessRequest(string? Symbol, string? Interval = null, string? Context = null);

/// <summary>Why a proposal was rejected — recorded so a terminal state is explicable later.</summary>
public sealed record ProposalRejectRequest(string? Reason = null);

public sealed record ResolveUnknownExecutionRequest(string? Resolution, string? Note);

/// <summary>An order to hold until a price level is reached or an alert kind fires.</summary>
/// <param name="TriggerPercent">
/// Size of the move, in percent, for a PercentDrop/PercentRise trigger. Ignored by every other kind.
/// </param>
/// <param name="ReferencePrice">
/// The price a percent trigger measures its move from. Send the price the operator was looking at, so
/// the level armed is the level they were quoted; omitted, it is captured from the live feed.
/// </param>
/// <param name="Trailing">
/// Percent triggers only. The reference follows the price in the favourable direction — the high for a
/// drop trigger, the low for a rise — making a drop trigger a trailing stop. Never moves back.
/// </param>
public sealed record ArmOrderRequest(
    string? Symbol,
    string? Action,
    int? Quantity,
    string? TriggerKind,
    decimal? TriggerPrice = null,
    string? TriggerAlertKind = null,
    string? OrderType = "LIMIT",
    decimal? Price = null,
    decimal? LimitPrice = null,
    DateTime? ExpiresUtc = null,
    int? ExpiresInDays = null,
    string? Note = null,
    string? SourceAlertId = null,
    AttachStopRequest? AttachStop = null,
    decimal? TriggerPercent = null,
    decimal? ReferencePrice = null,
    bool Trailing = false);

/// <summary>An immediate order submitted from a registry choice in the trading dashboard.</summary>
public sealed record DashboardOrderRequest(
    string? OrderIntentId,
    string? Symbol,
    int? Quantity,
    decimal? Price = null,
    decimal? TriggerPrice = null,
    decimal? LimitPrice = null,
    string? ClientRequestId = null);

/// <summary>Auditable bulk alert state change. Dismiss is the UI's soft-delete operation.</summary>
public sealed record BulkAlertActionRequest(
    string? Action,
    IReadOnlyList<string>? AlertIds = null,
    bool All = false);

/// <summary>
/// A protective stop to attach to a BUY entry, armed only once the entry is confirmed filled.
/// </summary>
/// <param name="Quantity">
/// Shares to protect. Null follows the entry's own quantity, clamped to what actually fills.
/// </param>
/// <param name="Recurring">
/// Re-place the native stop every session. On by default, because this venue clears outstanding
/// orders at the close — a one-shot stop protects the position for a single day and then lapses
/// silently.
/// </param>
public sealed record AttachStopRequest(
    decimal? StopTrigger,
    decimal? StopLimit = null,
    int? Quantity = null,
    bool Recurring = true);

/// <summary>How long to suspend order confirmation for.</summary>
public sealed record ArmApprovalRequest(int? Minutes = null);

/// <summary>Per-symbol watchlist fields the user controls. Null means "leave unchanged".</summary>
public sealed record WatchlistUpdateRequest(
    bool? AlertsEnabled = null,
    string? Notes = null,
    bool? Pinned = null);

public sealed record WatchlistReorderRequest(IReadOnlyList<string>? Symbols);

/// <summary>Optional depth override for a manually triggered backfill; null uses the configured years.</summary>
/// <summary>
/// A backfill trigger. <paramref name="Symbols"/> scopes which dates count as missing — the dates those
/// symbols were never requested for — not which symbols are stored; a session fetch returns the whole
/// market regardless. Null or empty means every archived symbol.
/// </summary>
public sealed record CandleBackfillRequest(int? Years = null, IReadOnlyList<string>? Symbols = null);
