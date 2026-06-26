using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TradingAgent.Broker;
using TradingAgent.Config;
using TradingAgent.Models;
using TradingAgent.Safety;

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
        "has confirmed the market is open.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["action"]      = new() { Type = "string",  Description = "BUY or SELL",                                   Required = true  },
        ["symbol"]      = new() { Type = "string",  Description = "PSX ticker symbol e.g. OGDC",                   Required = true  },
        ["quantity"]    = new() { Type = "number",  Description = "Number of shares",                              Required = true  },
        ["price"]       = new() { Type = "number",  Description = "Limit price in PKR. Omit for market order.",    Required = false },
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

        // Reject a malformed quantity rather than silently substituting DefaultQty — a bad signal
        // should not turn into a real order at the default size.
        if (!int.TryParse(arguments.GetValueOrDefault("quantity")?.ToString(), out var quantity) || quantity <= 0)
            return ToolResult.Fail($"Invalid 'quantity' — must be a positive integer (got '{arguments.GetValueOrDefault("quantity")}').");

        decimal? price = null;
        if (arguments.TryGetValue("price", out var priceRaw) && priceRaw is not null)
        {
            try { price = Convert.ToDecimal(priceRaw); }
            catch { /* treated as a market order below */ }
        }

        var isMarket = orderType == "MARKET" || !price.HasValue;

        // ── 2. Confidence gate ────────────────────────────────────────────────
        if (!MeetsConfidence(confidence, opts.MinConfidence))
        {
            _logger.LogInformation(
                "[PlaceOrder] Confidence '{Conf}' below threshold '{Min}' — skipped.",
                confidence, opts.MinConfidence);
            return ToolResult.Ok(Skipped(
                $"Confidence '{confidence}' is below minimum '{opts.MinConfidence}'."));
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

        try
        {
            var result = await _broker.PlaceOrderAsync(signal);

            _logger.LogInformation(
                "[PlaceOrder] {Action} {Symbol} x{Qty} @ {Price} — Success={Success}",
                action, symbol, quantity, price, result.Success);

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                success           = result.Success,
                order_id          = result.OrderId,
                action            = result.Action,
                symbol            = result.Symbol,
                message           = result.Message,
                screenshot_before = result.ScreenshotBefore,
                screenshot_after  = result.ScreenshotAfter
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlaceOrder] Broker error for {Action} {Symbol}.", action, symbol);
            return ToolResult.Fail($"Broker error: {ex.Message}");
        }
    }

    private static bool MeetsConfidence(string actual, string minimum) =>
        _confidenceRank.GetValueOrDefault(actual, 0) >=
        _confidenceRank.GetValueOrDefault(minimum, 3);

    private static string Skipped(string reason) =>
        JsonSerializer.Serialize(new { skipped = true, reason });
}
