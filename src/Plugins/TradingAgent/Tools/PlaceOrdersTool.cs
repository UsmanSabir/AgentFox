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

    private readonly AhkBroker _broker;
    private readonly IOptions<TradingAgentOptions> _agentOptions;
    private readonly IOptions<AhkConfig> _ahkConfig;
    private readonly DuplicateSignalFilter _dedup;
    private readonly ILogger<PlaceOrdersTool> _logger;

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
            Description = "The orders to place, in order. Each: action (BUY/SELL), symbol, " +
                          "price (limit; omit for market), optional quantity (OMIT to auto-size from the per-stock budget), " +
                          "optional target (take-profit sell price for a BUY), order_type.",
            JsonSchema  = """
                {
                  "type": "array",
                  "minItems": 1,
                  "items": {
                    "type": "object",
                    "properties": {
                      "action":     { "type": "string", "enum": ["BUY", "SELL"] },
                      "symbol":     { "type": "string", "description": "PSX ticker e.g. LUCK" },
                      "quantity":   { "type": "number", "description": "Number of shares. Omit to auto-size from the per-stock budget using the limit price." },
                      "price":      { "type": "number", "description": "Limit price in PKR. Omit for market order." },
                      "target":     { "type": "number", "description": "Take-profit/sell price. Pairs a SELL with a BUY." },
                      "order_type": { "type": "string", "enum": ["LIMIT", "MARKET"] }
                    },
                    "required": ["action", "symbol"]
                  }
                }
                """
        },
        ["confidence"]  = new() { Type = "string", Description = "Overall signal confidence: HIGH, MEDIUM, or LOW.", Required = true  },
        ["raw_message"] = new() { Type = "string", Description = "Original message text (for duplicate check).",     Required = false },
    };

    public PlaceOrdersTool(
        AhkBroker broker,
        IOptions<TradingAgentOptions> agentOptions,
        IOptions<AhkConfig> ahkConfig,
        DuplicateSignalFilter dedup,
        ILogger<PlaceOrdersTool> logger)
    {
        _broker       = broker;
        _agentOptions = agentOptions;
        _ahkConfig    = ahkConfig;
        _dedup        = dedup;
        _logger       = logger;
    }

    /// <summary>One order as supplied by the model (snake_case JSON).</summary>
    private sealed record OrderInput(
        string? Action,
        string? Symbol,
        int? Quantity,
        decimal? Price,
        decimal? Target,
        string? OrderType);

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var opts = _agentOptions.Value;
        var ahk  = _ahkConfig.Value;

        // ── Batch-wide gates (run once) ───────────────────────────────────────
        if (!opts.AutoExecute)
            return ToolResult.Ok(Skipped("AutoExecute is disabled. Signals logged but not executed."));

        var confidence = arguments.GetValueOrDefault("confidence")?.ToString()?.ToUpperInvariant() ?? "NONE";
        var rawMessage = arguments.GetValueOrDefault("raw_message")?.ToString() ?? "";

        if (!MeetsConfidence(confidence, opts.MinConfidence))
            return ToolResult.Ok(Skipped($"Confidence '{confidence}' is below minimum '{opts.MinConfidence}'."));

        if (!string.IsNullOrEmpty(rawMessage) && _dedup.IsDuplicate(rawMessage))
            return ToolResult.Ok(Skipped("Identical signal already processed within the last hour."));

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

        // ── Validate each order into a group (or a skip reason) ────────────────
        var validated = orders.Select(o => (order: o, plan: BuildGroup(o, opts, ahk))).ToList();
        var groups    = validated.Where(v => v.plan.group is not null)
                                 .Select(v => v.plan.group!)
                                 .ToList();

        // ── Execute the runnable groups in one session ────────────────────────
        IReadOnlyList<IReadOnlyList<OrderResult>> grouped;
        try
        {
            grouped = groups.Count > 0
                ? await _broker.PlaceOrderGroupsAsync(groups)
                : Array.Empty<IReadOnlyList<OrderResult>>();
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
            if (plan.group is null)
            {
                report.Add(new { action = order.Action, symbol = order.Symbol, placed = false, reason = plan.skip });
                continue;
            }

            report.Add(BuildOrderReport(order, plan, grouped[gi++]));
        }

        var placed   = report.Count(r => r.GetType().GetProperty("success")?.GetValue(r) is true);
        _logger.LogInformation("[PlaceOrders] Batch complete: {Total} order(s), {Placed} succeeded.", report.Count, placed);

        return ToolResult.Ok(JsonSerializer.Serialize(new { orders = report }));
    }

    /// <summary>
    /// Validates one input order and builds its execution group: null group + a skip reason when the
    /// order is invalid or breaches a gate; otherwise [primary] or [BUY, take-profit SELL]. The bool
    /// says whether a take-profit sell was paired (needed to report "buy failed → sell skipped").
    /// </summary>
    private (IReadOnlyList<TradingSignal>? group, string? skip, bool pairedSell) BuildGroup(
        OrderInput o, TradingAgentOptions opts, AhkConfig ahk)
    {
        var action = o.Action?.ToUpperInvariant() ?? "";
        var symbol = o.Symbol?.ToUpperInvariant() ?? "";

        if (action is not ("BUY" or "SELL") || string.IsNullOrEmpty(symbol))
            return (null, $"Invalid order — action must be BUY/SELL and symbol is required (got action='{o.Action}', symbol='{o.Symbol}').", false);

        var orderType = o.OrderType?.ToUpperInvariant() ?? "LIMIT";
        var isMarket  = orderType == "MARKET" || !o.Price.HasValue;

        // Quantity is optional: an explicit positive value is honoured; a present-but-non-positive
        // value is rejected; when omitted the position is sized from the per-stock budget and the limit
        // price (deterministic math), which needs a price — a market order must carry an explicit qty.
        int qty;
        if (o.Quantity.HasValue)
        {
            if (o.Quantity.Value <= 0)
                return (null, $"Invalid quantity '{o.Quantity}' — must be a positive integer.", false);
            qty = o.Quantity.Value;
        }
        else
        {
            if (isMarket)
                return (null, "Cannot size from the per-stock budget without a limit price (market order). Provide a price or an explicit quantity.", false);

            var sized = PositionSizer.ComputeQuantity(ahk.PerStockBudgetPkr, o.Price!.Value, ahk.BudgetBufferPercent);
            if (sized is null)
                return (null, $"Per-stock budget {ahk.PerStockBudgetPkr:N0} PKR (less {ahk.BudgetBufferPercent}% buffer) is too small to buy one share at {o.Price!.Value:F2} PKR.", false);
            qty = sized.Value;
        }

        if (isMarket)
        {
            if (!ahk.AllowMarketOrders)
                return (null, "Market orders are disabled (Ahk.AllowMarketOrders=false). Provide a limit price.", false);
        }
        else
        {
            var value = qty * o.Price!.Value;
            if (value > ahk.MaxOrderValuePkr)
                return (null, $"Order value {value:N0} PKR exceeds limit of {ahk.MaxOrderValuePkr:N0} PKR.", false);
        }

        var group = new List<TradingSignal>
        {
            new()
            {
                Action     = action,
                Symbol     = symbol,
                EntryPrice = o.Price,
                Quantity   = qty,
                OrderType  = isMarket ? "MARKET" : "LIMIT",
                Confidence = "HIGH"
            }
        };

        // Pair a take-profit SELL with a BUY that carries a target. The sell exits exactly the shares
        // the BUY (already cap-checked) acquired — it is not new exposure — so it is deliberately NOT
        // re-checked against MaxOrderValuePkr. (target > entry, so its value always exceeds the buy's
        // and would otherwise be wrongly blocked whenever the budget sits near the cap.)
        var pairedSell = false;
        if (opts.AutoPlaceTargetSell && action == "BUY" && o.Target is > 0)
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

        return (group, null, pairedSell);
    }

    private static object BuildOrderReport(
        OrderInput order,
        (IReadOnlyList<TradingSignal>? group, string? skip, bool pairedSell) plan,
        IReadOnlyList<OrderResult> results)
    {
        var primary = results[0];

        object? followUpSell = null;
        if (plan.pairedSell)
        {
            if (results.Count > 1)
            {
                var sell = results[1];
                followUpSell = new
                {
                    placed            = true,
                    success           = sell.Success,
                    order_id          = sell.OrderId,
                    price             = order.Target,
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

        return new
        {
            action            = primary.Action,
            symbol            = primary.Symbol,
            quantity          = plan.group![0].Quantity,
            auto_sized        = order.Quantity is null,
            success           = primary.Success,
            order_id          = primary.OrderId,
            message           = primary.Message,
            screenshot_before = primary.ScreenshotBefore,
            screenshot_after  = primary.ScreenshotAfter,
            follow_up_sell    = followUpSell
        };
    }

    private static bool MeetsConfidence(string actual, string minimum) =>
        _confidenceRank.GetValueOrDefault(actual, 0) >=
        _confidenceRank.GetValueOrDefault(minimum, 3);

    private static string Skipped(string reason) =>
        JsonSerializer.Serialize(new { skipped = true, reason });
}
