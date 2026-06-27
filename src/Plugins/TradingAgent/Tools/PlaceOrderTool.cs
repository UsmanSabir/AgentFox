using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TradingAgent.Broker;
using TradingAgent.Config;
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

    private readonly AhkBroker _broker;
    private readonly IOptions<TradingAgentOptions> _agentOptions;
    private readonly IOptions<AhkConfig> _ahkConfig;
    private readonly DuplicateSignalFilter _dedup;
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
        AhkBroker broker,
        IOptions<TradingAgentOptions> agentOptions,
        IOptions<AhkConfig> ahkConfig,
        DuplicateSignalFilter dedup,
        ILogger<PlaceOrderTool> logger)
    {
        _broker       = broker;
        _agentOptions = agentOptions;
        _ahkConfig    = ahkConfig;
        _dedup        = dedup;
        _logger       = logger;
    }

    protected override async Task<ToolResult> ExecuteInternalAsync(
        Dictionary<string, object?> arguments)
    {
        var opts = _agentOptions.Value;
        var ahk  = _ahkConfig.Value;

        // ── 1. AutoExecute gate ───────────────────────────────────────────────
        if (!opts.AutoExecute)
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

        var isMarket = orderType == "MARKET" || !price.HasValue;

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
        if (!MeetsConfidence(confidence, opts.MinConfidence))
        {
            _logger.LogInformation(
                "[PlaceOrder] Confidence '{Conf}' below threshold '{Min}' — skipped.",
                confidence, opts.MinConfidence);
            return ToolResult.Ok(Skipped(
                $"Confidence '{confidence}' is below minimum '{opts.MinConfidence}'."));
        }

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
            _logger.LogInformation(
                "[PlaceOrder] Auto-sized {Symbol}: {Qty} share(s) @ {Price} ≈ {Value:N0} PKR " +
                "from budget {Budget:N0} PKR (buffer {Buffer}%).",
                symbol, quantity, price, quantity * price!.Value, ahk.PerStockBudgetPkr, ahk.BudgetBufferPercent);
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

        // ── 4. Duplicate filter ───────────────────────────────────────────────
        if (!string.IsNullOrEmpty(rawMessage) && _dedup.IsDuplicate(rawMessage))
        {
            _logger.LogInformation("[PlaceOrder] Duplicate signal — skipped.");
            return ToolResult.Ok(Skipped("Identical signal already processed within the last hour."));
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
        // wrongly blocked whenever the budget sits near the cap.)
        TradingSignal? sellSignal = null;
        if (opts.AutoPlaceTargetSell && action == "BUY" && target is > 0)
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
            results = await _broker.PlaceOrdersAsync(signals, stopOnFailure: true);
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

                followUpSell = new
                {
                    placed            = true,
                    success           = sell.Success,
                    order_id          = sell.OrderId,
                    action            = sell.Action,
                    symbol            = sell.Symbol,
                    price             = target,
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
            quantity          = quantity,
            price             = price,
            auto_sized        = autoSized,
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
