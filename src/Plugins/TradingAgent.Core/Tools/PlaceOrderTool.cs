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

namespace TradingAgent.Tools;

/// <summary>
/// Executes a trade on the AHK portal via browser automation.
///
/// Safety gates enforced (all must pass before AhkBroker is called):
///   1. AutoExecute flag     — skips execution if false
///   2. Confidence gate      — blocks if confidence < MinConfidence
///   3. Order value cap      — blocks if qty × price > MaxOrderValuePkr
///   4. Duplicate filter     — blocks if same raw_message seen within the window
///
/// HITL (human approval) is handled at the AgentOrchestrator layer — NOT here.
/// Add "place_order" to Hitl.RequireApprovalForTools in appsettings.json to enable it.
/// </summary>
public sealed class PlaceOrderTool : BaseTool
{
    private static readonly Dictionary<string, int> _confidenceRank = new()
    {
        ["NONE"] = 0, ["LOW"] = 1, ["MEDIUM"] = 2, ["HIGH"] = 3
    };

    private readonly TradingAgent.Manager.TradingManager _manager;
    private readonly IOptions<TradingAgentOptions> _agentOptions;
    private readonly TradingPolicyProvider _policyProvider;
    private readonly IOptions<AhkConfig> _ahkConfig;
    private readonly PendingTakeProfitStore _pendingSells;
    private readonly ApprovalIntentRegistry _intentRegistry;
    private readonly ILogger<PlaceOrderTool> _logger;

    public override string Name => "place_order";

    public override string Description =>
        "Place a BUY or SELL order on the Arif Habib Kornasif (AHK) trading portal " +
        "using browser automation. Enforces AutoExecute flag, MinConfidence gate, " +
        "order value cap, and duplicate filter. Only call this when check_market " +
        "has confirmed the market is open. Omit 'quantity' to auto-size the position " +
        "from the configured per-stock budget using the limit price. When a BUY tip also gives a target/sell " +
        "price (e.g. 'buy at 50, sell at 55'), pass it as 'target' and a take-profit " +
        "SELL limit order is placed automatically after the BUY succeeds.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["action"]      = new() { Type = "string",  Description = "BUY or SELL",                                   Required = true  },
        ["symbol"]      = new() { Type = "string",  Description = "PSX ticker symbol e.g. OGDC",                   Required = true  },
        ["quantity"]    = new() { Type = "number",  Description = "Number of shares. OMIT to auto-size from the per-stock budget (PerStockBudgetPkr) using the limit price — only pass this when the tip states an explicit share count.", Required = false },
        ["price"]       = new() { Type = "number",  Description = "Limit price in PKR. Omit for market order.",    Required = false },
        ["target"]      = new() { Type = "number",  Description = "Take-profit/sell price. If given with a BUY, a SELL limit order is placed at this price after the BUY succeeds.", Required = false },
        ["order_type"]  = new() { Type = "string",  Description = "LIMIT or MARKET",                               Required = true  },
        ["confidence"]  = new() { Type = "string",  Description = "Signal confidence: HIGH, MEDIUM, or LOW",       Required = true  },
        ["raw_message"] = new() { Type = "string",  Description = "Original message text (for duplicate check).",  Required = false },
    };

    public PlaceOrderTool(
        TradingAgent.Manager.TradingManager manager,
        IOptions<TradingAgentOptions> agentOptions,
        TradingPolicyProvider policyProvider,
        IOptions<AhkConfig> ahkConfig,
        PendingTakeProfitStore pendingSells,
        ApprovalIntentRegistry intentRegistry,
        ILogger<PlaceOrderTool> logger)
    {
        _manager      = manager;
        _agentOptions = agentOptions;
        _policyProvider = policyProvider;
        _ahkConfig    = ahkConfig;
        _pendingSells = pendingSells;
        _intentRegistry = intentRegistry;
        _logger       = logger;
    }

    protected override async Task<ToolResult> ExecuteInternalAsync(
        Dictionary<string, object?> arguments)
    {
        var opts = _agentOptions.Value;
        var policy = _policyProvider.Current();
        var ahk  = _ahkConfig.Value;

        // ── 1. AutoExecute gate ───────────────────────────────────────────────
        if (!policy.AutoExecute)
        {
            _logger.LogInformation("[PlaceOrder] AutoExecute=false — order skipped.");
            return ToolResult.Ok(Skipped("AutoExecute is disabled. Signal logged but not executed."));
        }

        var action     = arguments.GetValueOrDefault("action")?.ToString()?.ToUpperInvariant() ?? "";
        var symbol     = arguments.GetValueOrDefault("symbol")?.ToString()?.ToUpperInvariant() ?? "";
        var confidence = arguments.GetValueOrDefault("confidence")?.ToString()?.ToUpperInvariant() ?? "NONE";
        var orderType  = arguments.GetValueOrDefault("order_type")?.ToString()?.ToUpperInvariant() ?? "LIMIT";
        var rawMessage = arguments.GetValueOrDefault("raw_message")?.ToString() ?? "";

        if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(symbol))
            return ToolResult.Fail("'action' and 'symbol' are required.");

        decimal? price = null;
        if (arguments.TryGetValue("price", out var priceRaw) && priceRaw is not null)
        {
            try { price = Convert.ToDecimal(priceRaw); }
            catch { /* treated as a market order below */ }
        }

        decimal? target = null;
        if (arguments.TryGetValue("target", out var targetRaw) && targetRaw is not null)
        {
            try { target = Convert.ToDecimal(targetRaw); }
            catch { /* unparseable target — no follow-up sell */ }
        }

        // Quantity is OPTIONAL. An explicit positive value is honoured; a present-but-invalid value is
        // rejected (a bad signal must not turn into a real order at some default size); when omitted the
        // position is sized from the per-stock budget below — but only after the confidence gate passes.
        var quantityArg      = arguments.GetValueOrDefault("quantity");
        var quantityProvided = quantityArg is not null && !string.IsNullOrWhiteSpace(quantityArg.ToString());
        var quantity         = 0;
        if (quantityProvided &&
            (!int.TryParse(quantityArg!.ToString(), out quantity) || quantity <= 0))
            return ToolResult.Fail($"Invalid 'quantity' — must be a positive integer (got '{quantityArg}').");

        // ── 2. Confidence gate ────────────────────────────────────────────────
        if (!MeetsConfidence(confidence, policy.MinConfidence))
        {
            _logger.LogInformation(
                "[PlaceOrder] Confidence '{Conf}' below threshold '{Min}' — skipped.",
                confidence, policy.MinConfidence);
            return ToolResult.Ok(Skipped(
                $"Confidence '{confidence}' is below minimum '{policy.MinConfidence}'."));
        }

        // ── Resolve a missing entry price for a BUY ("accumulate on dips") from the live market ──
        // No explicit price + a limit BUY ⇒ read the live last-trade price and place the limit a dip
        // below it. Governed by AutoBuyWithoutEntryPrice: off ⇒ log for manual review, don't execute.
        var resolvedFromMarket = false;
        if (action == "BUY" && !price.HasValue && orderType != "MARKET")
        {
            if (!policy.AutoBuyWithoutEntryPrice)
                return ToolResult.Ok(Skipped(
                    "Tip has no entry price and AutoBuyWithoutEntryPrice is disabled — logged for manual review, not executed."));

            decimal? live = null;
            try { live = (await _manager.GetMarketPricesAsync(new[] { symbol })).GetValueOrDefault(symbol); }
            catch (Exception ex) { _logger.LogError(ex, "[PlaceOrder] Live price fetch failed for {Symbol}.", symbol); }

            if (live is not > 0)
                return ToolResult.Fail($"Could not read a live market price for {symbol} to size a no-entry-price BUY.");

            var dip = Math.Clamp(ahk.DipDiscountPercent, 0m, 100m) / 100m;
            price = Math.Round(live.Value * (1m - dip), 2, MidpointRounding.AwayFromZero);
            resolvedFromMarket = true;
            _logger.LogInformation(
                "[PlaceOrder] Resolved {Symbol} entry from live {Live} → {Price} (dip {Dip}%).",
                symbol, live, price, ahk.DipDiscountPercent);
        }

        var isMarket = orderType == "MARKET" || !price.HasValue;

        // ── Budget-based position sizing ──────────────────────────────────────
        // When no explicit quantity was supplied, derive the share count from the per-stock budget and
        // the limit price (deterministic math, never the LLM). Requires a price — a market order has no
        // price to size against, so it must carry an explicit quantity instead.
        var autoSized = false;
        if (!quantityProvided)
        {
            if (isMarket)
                return ToolResult.Fail(
                    "Cannot size an order from the per-stock budget without a limit 'price' (market order). " +
                    "Provide a 'price', or an explicit 'quantity'.");

            var sized = PositionSizer.ComputeQuantity(ahk.PerStockBudgetPkr, price!.Value, ahk.BudgetBufferPercent);
            if (sized is null)
                return ToolResult.Fail(
                    $"Per-stock budget {ahk.PerStockBudgetPkr:N0} PKR (less {ahk.BudgetBufferPercent}% buffer) " +
                    $"is too small to buy even one share of {symbol} at {price!.Value:F2} PKR.");

            quantity  = sized.Value;
            autoSized = true;

            // WARNING, not information. Auto-sizing is a legitimate feature — a tip that names a stock
            // but no share count should still be actionable — but it is also what happens when a
            // CALLER MEANT to pass a quantity and did not. Observed live on 2026-08-18: a model
            // instructed to place 10 shares omitted the argument, and the budget sized the order to 75
            // shares (48,750 PKR). That was within MaxOrderValuePkr and so entirely permitted, which
            // is exactly why it needs to be loud: the configured ceiling is PerStockBudgetPkr, not
            // whatever the requester had in mind.
            _logger.LogWarning(
                "[PlaceOrder] NO QUANTITY SUPPLIED for {Symbol} — auto-sized from the per-stock budget "
                + "to {Qty} share(s) @ {Price} ≈ {Value:N0} PKR (budget {Budget:N0} PKR, buffer {Buffer}%). "
                + "If a specific quantity was intended, it was NOT honoured.",
                symbol, quantity, price, quantity * price!.Value, ahk.PerStockBudgetPkr, ahk.BudgetBufferPercent);

            if (opts.RequireExplicitQuantity)
            {
                return ToolResult.Fail(
                    $"No 'quantity' was supplied for {symbol}, and TradingAgent.RequireExplicitQuantity "
                    + $"is enabled. Budget sizing would have placed {quantity} share(s) @ "
                    + $"{price!.Value:F2} ≈ {quantity * price!.Value:N0} PKR. Re-issue the order with an "
                    + "explicit 'quantity' — do not assume the size above was intended.");
            }
        }

        // ── 3. Order value cap ────────────────────────────────────────────────
        // A market order has no known price, so its value cannot be capped. Block it unless the
        // operator has explicitly opted in via Ahk.AllowMarketOrders — otherwise a single market
        // order could exceed MaxOrderValuePkr without ever tripping the cap.
        if (isMarket)
        {
            if (!ahk.AllowMarketOrders)
                return ToolResult.Fail(
                    "Market orders are disabled (Ahk.AllowMarketOrders=false). Provide a limit 'price' " +
                    "so the order value can be checked against MaxOrderValuePkr.");

            _logger.LogWarning(
                "[PlaceOrder] MARKET order for {Symbol} x{Qty} — value cap cannot be enforced.",
                symbol, quantity);
        }
        else
        {
            var orderValue = quantity * price!.Value;
            if (orderValue > ahk.MaxOrderValuePkr)
            {
                _logger.LogWarning(
                    "[PlaceOrder] Order value {Value:N0} PKR exceeds cap {Cap:N0} PKR — blocked.",
                    orderValue, ahk.MaxOrderValuePkr);
                return ToolResult.Fail(
                    $"Order value {orderValue:N0} PKR exceeds limit of {ahk.MaxOrderValuePkr:N0} PKR.");
            }
        }

        // ── Execute ───────────────────────────────────────────────────────────
        var signal = new TradingSignal
        {
            Action     = action,
            Symbol     = symbol,
            EntryPrice = price,
            Quantity   = quantity,
            // Normalise so the broker's market-vs-limit decision matches the gate above:
            // a "LIMIT" with no usable price is executed (and was value-checked) as a market order.
            OrderType  = isMarket ? "MARKET" : "LIMIT",
            Confidence = confidence,
            RawMessage = rawMessage
        };

        // ── Pair a take-profit SELL with the BUY ("buy at X, sell at Y") ──────
        // Only when the feature is on, this is a BUY, and a positive target was given. The take-profit
        // sells EXACTLY the shares the BUY just acquired — it is the exit of an already cap-checked
        // position, not new exposure — so it is deliberately NOT re-checked against MaxOrderValuePkr.
        // (Since target > entry, the sell value always exceeds the buy value and would otherwise be
        // wrongly blocked whenever the budget sits near the cap.) EXCEPTION: when the entry was resolved
        // from the live market at a dip discount, the buy limit rests below market and may not fill, so
        // no take-profit sell is paired (it would risk selling shares not yet held).
        TradingSignal? sellSignal = null;
        if (policy.AutoPlaceTargetSell && action == "BUY" && target is > 0 && !resolvedFromMarket)
        {
            sellSignal = new TradingSignal
            {
                Action     = "SELL",
                Symbol     = symbol,
                EntryPrice = target,
                Quantity   = quantity,
                OrderType  = "LIMIT",
                Confidence = "HIGH"
            };
        }

        // Place the BUY and (when paired) the SELL in ONE browser session. stopOnFailure means the
        // SELL is not attempted if the BUY fails — never a naked exit order.
        var signals = sellSignal is null ? new[] { signal } : new[] { signal, sellSignal };

        IReadOnlyList<OrderResult> results;
        try
        {
            IReadOnlyList<IReadOnlyList<TradingSignal>> groups = new[] { (IReadOnlyList<TradingSignal>)signals };

            // Bind this validated request to an immutable, one-time, expiring intent. TradingManager
            // recomputes the hash before submission, so any drift after this point is rejected.
            var intent = ApprovalIntent.Create(groups, rawMessage, policy.Version,
                TimeSpan.FromSeconds(Math.Max(10, opts.ApprovalIntentTtlSeconds)));
            _intentRegistry.Register(intent);

            var execution = await _manager.ExecuteGroupsAsync(
                groups, rawMessage,
                ExecutionAuthorization.HostToolGate(intent: intent));
            if (!execution.Executed)
                return ToolResult.Ok(Skipped(execution.Reason));
            results = execution.Groups.FirstOrDefault() ?? Array.Empty<OrderResult>();
            if (results.Count == 0)
                return ToolResult.Fail("Trading manager returned no order result.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlaceOrder] Broker error for {Action} {Symbol}.", action, symbol);
            return ToolResult.Fail($"Broker error: {ex.Message}");
        }

        var result = results[0];
        _logger.LogInformation(
            "[PlaceOrder] {Action} {Symbol} x{Qty} @ {Price} — Success={Success}",
            action, symbol, quantity, price, result.Success);

        // Report on the paired sell: its outcome if it ran, why it was skipped (cap), or that the buy
        // failed so it was not attempted.
        object? followUpSell = null;
        if (sellSignal is not null)
        {
            if (results.Count > 1)
            {
                var sell = results[1];
                _logger.LogInformation(
                    "[PlaceOrder] Follow-up SELL {Symbol} x{Qty} @ {Price} — Success={Success}",
                    symbol, quantity, target, sell.Success);

                // If the sell failed transiently (the buy limit hasn't filled yet → "insufficient
                // exposure"), queue it for background retry instead of just reporting failure.
                var retryScheduled = !sell.Success
                    && policy.RetryFailedTakeProfit
                    && PendingTakeProfitStore.IsRetryable(sell.Message)
                    && _pendingSells.Schedule(symbol, quantity, target!.Value,
                                              policy.TakeProfitRetryIntervalMinutes, rawMessage);

                followUpSell = new
                {
                    placed            = true,
                    success           = sell.Success,
                    order_id          = sell.OrderId,
                    action            = sell.Action,
                    symbol            = sell.Symbol,
                    price             = sell.SubmittedPrice ?? target,
                    requested_price   = target,
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

        return ToolResult.Ok(JsonSerializer.Serialize(new
        {
            success           = result.Success,
            order_id          = result.OrderId,
            action            = result.Action,
            symbol            = result.Symbol,
            quantity                   = quantity,
            price                      = result.SubmittedPrice ?? price,
            requested_price            = price,
            price_adjustment           = result.PriceAdjustment,
            auto_sized                 = autoSized,
            // Surfaced in the RESULT, not just the log. A caller that meant to specify a size needs
            // to see, in the answer it acts on, that the size it got was chosen by the budget.
            auto_sized_warning         = autoSized
                ? $"No quantity was supplied, so this order was sized from the per-stock budget: "
                  + $"{quantity} share(s). Verify this is the intended size."
                : null,
            entry_resolved_from_market = resolvedFromMarket,
            message           = result.Message,
            screenshot_before = result.ScreenshotBefore,
            screenshot_after  = result.ScreenshotAfter,
            follow_up_sell    = followUpSell
        }));
    }

    private static bool MeetsConfidence(string actual, string minimum) =>
        _confidenceRank.GetValueOrDefault(actual, 0) >=
        _confidenceRank.GetValueOrDefault(minimum, 3);

    private static string Skipped(string reason) =>
        JsonSerializer.Serialize(new { skipped = true, reason });
}
