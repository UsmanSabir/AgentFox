using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TradingAgent.Broker;
using TradingAgent.Config;
using TradingAgent.Manager;
using TradingAgent.Models;
using TradingAgent.Safety;
using TradingAgent.Trading;
using TradingAgent.Watchlist;

namespace TradingAgent.Tools;

/// <summary>
/// Places MULTIPLE orders from one (typically plan-approved) tip in a single browser session.
///
/// Each input order is validated independently (action/symbol/quantity, market-order rule, value cap)
/// and turned into a "group": a plain order is a one-element group; a BUY carrying a target becomes a
/// two-element [BUY, take-profit SELL] group. The broker runs every group in one browser session —
/// stopping a buy→sell pair if the buy fails, but always continuing to the next independent order.
///
/// The same session-wide gates as <see cref="PlaceOrderTool"/> apply once for the batch: AutoExecute,
/// confidence threshold, and the duplicate filter. Per-order failures (bad fields, value cap) are
/// reported in the result rather than aborting the whole batch.
/// </summary>
public sealed class PlaceOrdersTool : BaseTool
{
    private static readonly Dictionary<string, int> _confidenceRank = new()
    {
        ["NONE"] = 0, ["LOW"] = 1, ["MEDIUM"] = 2, ["HIGH"] = 3
    };

    private static readonly JsonSerializerOptions _snakeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly TradingAgent.Manager.TradingManager _manager;
    private readonly IOptions<TradingAgentOptions> _agentOptions;
    private readonly TradingPolicyProvider _policyProvider;
    private readonly IOptions<AhkConfig> _ahkConfig;
    private readonly PendingTakeProfitStore _pendingSells;
    private readonly ApprovalIntentRegistry _intentRegistry;
    private readonly ILogger<PlaceOrdersTool> _logger;
    private readonly MonitoredUniverse? _universe;

    public override string Name => "place_orders";

    public override string Description =>
        "Place MULTIPLE orders from an approved plan in a SINGLE browser session — use this for a tip " +
        "with several orders (e.g. two buy/sell pairs). Each BUY that includes a 'target' also gets a " +
        "take-profit SELL placed at the target after its BUY succeeds; within that pair the sell is " +
        "skipped if the buy fails, while separate orders are independent. Enforces AutoExecute, " +
        "MinConfidence, per-order value cap, and the duplicate filter. Only call once check_market " +
        "confirms the market is open (and, when plan mode is on, after the plan is approved).";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["orders"] = new()
        {
            Type        = "array",
            Required    = true,
            Description = "The orders to place, in order. Each: action (BUY/SELL), symbol, this tip's own " +
                          "confidence, price (limit; omit for market), optional quantity (OMIT to auto-size from " +
                          "the per-stock budget), optional target (take-profit sell price for a BUY), order_type.",
            JsonSchema  = """
                {
                  "type": "array",
                  "minItems": 1,
                  "items": {
                    "type": "object",
                    "properties": {
                      "action":     { "type": "string", "enum": ["BUY", "SELL"] },
                      "symbol":     { "type": "string", "description": "PSX ticker e.g. LUCK" },
                      "confidence": { "type": "string", "enum": ["HIGH", "MEDIUM", "LOW", "NONE"], "description": "THIS tip's own confidence. Each order is gated against MinConfidence individually." },
                      "quantity":   { "type": "number", "description": "Number of shares. Omit to auto-size from the per-stock budget using the limit price." },
                      "price":      { "type": "number", "description": "Limit price in PKR. Omit for market order." },
                      "target":     { "type": "number", "description": "Take-profit/sell price. Pairs a SELL with a BUY." },
                      "order_type": { "type": "string", "enum": ["LIMIT", "MARKET"] }
                    },
                    "required": ["action", "symbol", "confidence"]
                  }
                }
                """
        },
        ["confidence"]  = new() { Type = "string", Description = "Fallback confidence for orders that don't carry their own. Optional — prefer per-order confidence.", Required = false },
        ["raw_message"] = new() { Type = "string", Description = "Original message text (for duplicate check).",     Required = false },
    };

    public PlaceOrdersTool(
        TradingAgent.Manager.TradingManager manager,
        IOptions<TradingAgentOptions> agentOptions,
        TradingPolicyProvider policyProvider,
        IOptions<AhkConfig> ahkConfig,
        PendingTakeProfitStore pendingSells,
        ApprovalIntentRegistry intentRegistry,
        ILogger<PlaceOrdersTool> logger,
        MonitoredUniverse? universe = null)
    {
        _manager      = manager;
        _agentOptions = agentOptions;
        _policyProvider = policyProvider;
        _ahkConfig    = ahkConfig;
        _pendingSells = pendingSells;
        _intentRegistry = intentRegistry;
        _logger       = logger;
        _universe     = universe;
    }

    /// <summary>One order as supplied by the model (snake_case JSON).</summary>
    private sealed record OrderInput(
        string? Action,
        string? Symbol,
        string? Confidence,
        int? Quantity,
        decimal? Price,
        decimal? Target,
        string? OrderType);

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var opts = _agentOptions.Value;
        var policy = _policyProvider.Current();
        var ahk  = _ahkConfig.Value;

        // ── Batch-wide gates (run once) ───────────────────────────────────────
        // AutoExecute and the duplicate filter are properties of the MESSAGE, so they gate the whole
        // batch. Confidence is per-tip and is gated individually in BuildGroup — a single weak tip no
        // longer blocks the strong ones in the same message. A batch-level 'confidence' (if supplied) is
        // only a fallback for orders that omit their own.
        if (!policy.AutoExecute)
            return ToolResult.Ok(Skipped("AutoExecute is disabled. Signals logged but not executed."));

        var batchConfidence = arguments.GetValueOrDefault("confidence")?.ToString()?.ToUpperInvariant();
        var rawMessage      = arguments.GetValueOrDefault("raw_message")?.ToString() ?? "";

        // ── Parse the orders array (robust to whatever concrete type it arrives as) ──
        List<OrderInput> orders;
        try
        {
            var json = JsonSerializer.Serialize(arguments.GetValueOrDefault("orders"));
            orders = JsonSerializer.Deserialize<List<OrderInput>>(json, _snakeOptions) ?? new();
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Could not parse 'orders': {ex.Message}");
        }

        if (orders.Count == 0)
            return ToolResult.Fail("'orders' must contain at least one order.");

        // ── Resolve live prices for BUY tips that gave no entry price ("accumulate on dips") ──
        // Only when AutoBuyWithoutEntryPrice is on; otherwise those orders are logged, not executed.
        var livePrices = await ResolveLivePricesAsync(orders, policy);

        // ── Validate each order into a group (or a skip reason) ────────────────
        // A manual-only symbol is skipped like any other rejected tip, so the rest of the batch still
        // runs — this is a per-symbol restriction, and failing the whole message because one name is
        // hand-managed would be a worse answer than reporting that one line. Same reasoning as the
        // single-order tool: see PlaceOrderTool's manual-only gate.
        var manualOnly = _universe is not null
            ? await _universe.ManualOnlyAsync()
            : (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var validated = orders.Select(o => (
            order: o,
            plan: manualOnly.Contains(o.Symbol?.Trim().ToUpperInvariant() ?? "")
                ? new OrderPlan(null,
                    $"{o.Symbol?.Trim().ToUpperInvariant()} is set to manual-only — it is operated by "
                    + "hand. Report the setup to the operator instead of placing it.", false, false)
                : BuildGroup(o, policy, ahk, livePrices, batchConfidence))).ToList();
        var groups    = validated.Where(v => v.plan.Group is not null)
                                 .Select(v => v.plan.Group!)
                                 .ToList();

        // ── Execute the runnable groups in one session ────────────────────────
        IReadOnlyList<IReadOnlyList<OrderResult>> grouped;
        try
        {
            if (groups.Count == 0)
            {
                grouped = Array.Empty<IReadOnlyList<OrderResult>>();
            }
            else
            {
                // Bind this validated batch to an immutable, one-time, expiring intent. TradingManager
                // recomputes the hash before submission, so any drift after this point is rejected.
                var intent = ApprovalIntent.Create(groups, rawMessage, policy.Version,
                    TimeSpan.FromSeconds(Math.Max(10, opts.ApprovalIntentTtlSeconds)));
                _intentRegistry.Register(intent);

                var execution = await _manager.ExecuteGroupsAsync(
                    groups, rawMessage, ExecutionAuthorization.HostToolGate(intent: intent));
                if (!execution.Executed)
                    return ToolResult.Ok(Skipped(execution.Reason));
                grouped = execution.Groups;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlaceOrders] Broker error executing batch of {Count} group(s).", groups.Count);
            return ToolResult.Fail($"Broker error: {ex.Message}");
        }

        // ── Stitch results back to each input order ───────────────────────────
        var report = new List<object>(validated.Count);
        var gi = 0;
        foreach (var (order, plan) in validated)
        {
            if (plan.Group is null)
            {
                report.Add(new { action = order.Action, symbol = order.Symbol, placed = false, reason = plan.Skip });
                continue;
            }

            var results = grouped[gi++];

            // If the paired take-profit SELL failed transiently (its BUY hasn't filled yet), queue it for
            // background retry instead of just reporting the failure.
            var retryScheduled = false;
            if (plan.PairedSell && results.Count > 1 && !results[1].Success
                && policy.RetryFailedTakeProfit
                && PendingTakeProfitStore.IsRetryable(results[1].Message)
                && order.Target is > 0)
            {
                retryScheduled = _pendingSells.Schedule(
                    plan.Group[0].Symbol, plan.Group[0].Quantity ?? 0, order.Target.Value,
                    policy.TakeProfitRetryIntervalMinutes, rawMessage);
            }

            report.Add(BuildOrderReport(order, plan, results, retryScheduled));
        }

        var placed   = report.Count(r => r.GetType().GetProperty("success")?.GetValue(r) is true);
        _logger.LogInformation("[PlaceOrders] Batch complete: {Total} order(s), {Placed} succeeded.", report.Count, placed);

        return ToolResult.Ok(JsonSerializer.Serialize(new { orders = report }));
    }

    /// <summary>The outcome of validating one order: an execution group, or a skip reason.</summary>
    private sealed record OrderPlan(
        IReadOnlyList<TradingSignal>? Group,
        string? Skip,
        bool PairedSell,
        bool ResolvedFromMarket);

    /// <summary>
    /// Fetches the live last-trade price for every BUY order that named a stock but gave no entry price
    /// (and is not an explicit market order). Returns empty when the feature is off
    /// (AutoBuyWithoutEntryPrice=false) or there is nothing to resolve — BuildGroup then skips those
    /// orders (logged, not executed) rather than guessing a price.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, decimal?>> ResolveLivePricesAsync(
        IReadOnlyList<OrderInput> orders, TradingPolicySnapshot policy)
    {
        if (!policy.AutoBuyWithoutEntryPrice)
            return new Dictionary<string, decimal?>();

        var symbols = orders
            .Where(o => o.Action?.ToUpperInvariant() == "BUY"
                        && !o.Price.HasValue
                        && (o.OrderType?.ToUpperInvariant() ?? "LIMIT") != "MARKET")
            .Select(o => o.Symbol?.Trim().ToUpperInvariant() ?? "")
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();

        if (symbols.Count == 0)
            return new Dictionary<string, decimal?>();

        try
        {
            return await _manager.GetMarketPricesAsync(symbols);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlaceOrders] Could not fetch live prices for {Count} symbol(s).", symbols.Count);
            return new Dictionary<string, decimal?>();
        }
    }

    /// <summary>
    /// Validates one input order and builds its execution group: null group + a skip reason when the
    /// order is invalid or breaches a gate; otherwise [primary] or [BUY, take-profit SELL]. When the
    /// order gave no entry price, the limit is resolved from the live market price (less the dip
    /// discount) and no take-profit SELL is paired — that limit rests below market and may not fill.
    /// </summary>
    private OrderPlan BuildGroup(
        OrderInput o, TradingPolicySnapshot policy, AhkConfig ahk,
        IReadOnlyDictionary<string, decimal?> livePrices, string? batchConfidence)
    {
        var action = o.Action?.ToUpperInvariant() ?? "";
        var symbol = o.Symbol?.ToUpperInvariant() ?? "";

        if (action is not ("BUY" or "SELL") || string.IsNullOrEmpty(symbol))
            return new(null, $"Invalid order — action must be BUY/SELL and symbol is required (got action='{o.Action}', symbol='{o.Symbol}').", false, false);

        // Per-tip confidence gate: this order's own confidence (falling back to a batch-level value, then
        // NONE) must meet MinConfidence. One weak tip is skipped without affecting the others.
        var confidence = (o.Confidence ?? batchConfidence ?? "NONE").ToUpperInvariant();
        if (!MeetsConfidence(confidence, policy.MinConfidence))
            return new(null, $"Confidence '{confidence}' is below minimum '{policy.MinConfidence}'.", false, false);

        var orderType = o.OrderType?.ToUpperInvariant() ?? "LIMIT";

        // Resolve a missing entry price for a BUY ("accumulate on dips") from the live market price,
        // less the configured dip discount, so the order rests just below market.
        decimal? price         = o.Price;
        var resolvedFromMarket = false;
        if (action == "BUY" && !price.HasValue && orderType != "MARKET")
        {
            if (!policy.AutoBuyWithoutEntryPrice)
                return new(null, "No entry price in tip and AutoBuyWithoutEntryPrice is disabled — logged for manual review, not executed.", false, false);

            if (!livePrices.TryGetValue(symbol, out var live) || live is not > 0)
                return new(null, $"Could not read a live market price for {symbol} to size a no-entry-price BUY.", false, false);

            var dip = Math.Clamp(ahk.DipDiscountPercent, 0m, 100m) / 100m;
            price = Math.Round(live.Value * (1m - dip), 2, MidpointRounding.AwayFromZero);
            resolvedFromMarket = true;
        }

        var isMarket = orderType == "MARKET" || !price.HasValue;

        // Quantity: explicit positive honoured; present-but-non-positive rejected; omitted ⇒ sized from
        // the per-stock budget and the limit price (a market order needs an explicit quantity).
        int qty;
        if (o.Quantity.HasValue)
        {
            if (o.Quantity.Value <= 0)
                return new(null, $"Invalid quantity '{o.Quantity}' — must be a positive integer.", false, resolvedFromMarket);
            qty = o.Quantity.Value;
        }
        else
        {
            if (isMarket)
                return new(null, "Cannot size from the per-stock budget without a limit price (market order). Provide a price or an explicit quantity.", false, resolvedFromMarket);

            var sized = PositionSizer.ComputeQuantity(ahk.PerStockBudgetPkr, price!.Value, ahk.BudgetBufferPercent);
            if (sized is null)
                return new(null, $"Per-stock budget {ahk.PerStockBudgetPkr:N0} PKR (less {ahk.BudgetBufferPercent}% buffer) is too small to buy one share at {price!.Value:F2} PKR.", false, resolvedFromMarket);

            // Same guard as the single-order tool: with no quantity the ceiling is the BUDGET, not
            // whatever the caller intended, so an omission can quietly become a much larger position.
            if (_agentOptions.Value.RequireExplicitQuantity)
                return new(null,
                    $"No quantity supplied, and TradingAgent.RequireExplicitQuantity is enabled. Budget "
                    + $"sizing would have placed {sized.Value} share(s) @ {price!.Value:F2} "
                    + $"≈ {sized.Value * price!.Value:N0} PKR. Re-issue with an explicit quantity.",
                    false, resolvedFromMarket);

            qty = sized.Value;
            _logger.LogWarning(
                "[PlaceOrders] NO QUANTITY SUPPLIED for {Symbol} — auto-sized from the per-stock budget "
                + "to {Qty} share(s) @ {Price} ≈ {Value:N0} PKR. If a specific quantity was intended, "
                + "it was NOT honoured.",
                o.Symbol, qty, price, qty * price!.Value);
        }

        if (isMarket)
        {
            if (!ahk.AllowMarketOrders)
                return new(null, "Market orders are disabled (Ahk.AllowMarketOrders=false). Provide a limit price.", false, resolvedFromMarket);
        }
        else
        {
            var value = qty * price!.Value;
            if (value > ahk.MaxOrderValuePkr)
                return new(null, $"Order value {value:N0} PKR exceeds limit of {ahk.MaxOrderValuePkr:N0} PKR.", false, resolvedFromMarket);
        }

        var group = new List<TradingSignal>
        {
            new()
            {
                Action     = action,
                Symbol     = symbol,
                EntryPrice = price,
                Quantity   = qty,
                OrderType  = isMarket ? "MARKET" : "LIMIT",
                Confidence = "HIGH"
            }
        };

        // Pair a take-profit SELL with a BUY that carries a target — EXCEPT when the entry was resolved
        // from the market at a dip discount: that limit rests below market and may not fill, so pairing a
        // sell would risk selling shares not yet held. The sell exits exactly the shares the BUY acquired,
        // so it is intentionally NOT re-checked against MaxOrderValuePkr.
        var pairedSell = false;
        if (policy.AutoPlaceTargetSell && action == "BUY" && o.Target is > 0 && !resolvedFromMarket)
        {
            group.Add(new TradingSignal
            {
                Action     = "SELL",
                Symbol     = symbol,
                EntryPrice = o.Target,
                Quantity   = qty,
                OrderType  = "LIMIT",
                Confidence = "HIGH"
            });
            pairedSell = true;
        }

        return new(group, null, pairedSell, resolvedFromMarket);
    }

    private static object BuildOrderReport(
        OrderInput order,
        OrderPlan plan,
        IReadOnlyList<OrderResult> results,
        bool retryScheduled)
    {
        var primary = results[0];

        object? followUpSell = null;
        if (plan.PairedSell)
        {
            if (results.Count > 1)
            {
                var sell = results[1];
                followUpSell = new
                {
                    placed            = true,
                    success           = sell.Success,
                    order_id          = sell.OrderId,
                    price             = sell.SubmittedPrice ?? order.Target,
                    requested_price   = order.Target,
                    price_adjustment  = sell.PriceAdjustment,
                    retry_scheduled   = retryScheduled,
                    message           = sell.Message,
                    screenshot_before = sell.ScreenshotBefore,
                    screenshot_after  = sell.ScreenshotAfter
                };
            }
            else
            {
                followUpSell = new { placed = false, reason = "Buy order did not succeed; take-profit sell skipped." };
            }
        }
        else if (plan.ResolvedFromMarket && order.Target is > 0)
        {
            followUpSell = new { placed = false, reason = "Entry resolved from live market at a dip — take-profit SELL not auto-placed (the limit may not fill yet)." };
        }

        return new
        {
            action                     = primary.Action,
            symbol                     = primary.Symbol,
            confidence                 = order.Confidence,
            quantity                   = plan.Group![0].Quantity,
            price                      = primary.SubmittedPrice ?? plan.Group![0].EntryPrice,
            requested_price            = plan.Group![0].EntryPrice,
            price_adjustment           = primary.PriceAdjustment,
            auto_sized                 = order.Quantity is null,
            entry_resolved_from_market = plan.ResolvedFromMarket,
            success                    = primary.Success,
            order_id                   = primary.OrderId,
            message                    = primary.Message,
            screenshot_before          = primary.ScreenshotBefore,
            screenshot_after           = primary.ScreenshotAfter,
            follow_up_sell             = followUpSell
        };
    }

    private static bool MeetsConfidence(string actual, string minimum) =>
        _confidenceRank.GetValueOrDefault(actual, 0) >=
        _confidenceRank.GetValueOrDefault(minimum, 3);

    private static string Skipped(string reason) =>
        JsonSerializer.Serialize(new { skipped = true, reason });
}
