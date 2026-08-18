using System.Text.Json;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;
using AgentFox.Plugins;
using TradingAgent.Config;
using TradingAgent.Feed;

namespace TradingAgent.Tools;

/// <summary>
/// Cancels a resting order on the broker account.
///
/// <para>
/// <b>The portal gives no confirmation, so this tool manufactures one.</b> <c>POST /Home/CancelOrder</c>
/// returns nothing meaningful — the portal's own UI fires it and closes the dialog without reading
/// the response at all. Trusting HTTP 200 would therefore report success for a cancel that never
/// happened. Instead every cancel is verified by re-reading the outstanding book and confirming the
/// order has actually left it. This mirrors the reasoning already recorded in
/// <c>AhkConfig.VerifyOrderInBook</c> for placements: on this portal the account's own order book is
/// the only evidence that means anything.
/// </para>
///
/// <para>
/// <b>Selection is deliberately strict.</b> A cancel is irreversible in the sense that matters —
/// a queue position, once given up, is gone, and the resting order might be a protective stop. So the
/// tool cancels by explicit order number, or by a symbol that resolves to exactly ONE working order.
/// A symbol matching several orders is refused with the candidates listed, rather than the tool
/// picking one; guessing which of three orders the user meant is not a recoverable mistake.
/// </para>
/// </summary>
public sealed class CancelOrderTool : BaseTool
{
    private static readonly JsonSerializerOptions SnakeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Gap between order-book reads while verifying a cancel. 2s rather than sub-second on purpose:
    /// a cancel round-trips to the exchange and will not clear in milliseconds, so faster polling
    /// buys nothing and only multiplies requests against the broker — at 750ms a single 30s
    /// verification was 40 reads. The loop exits the moment the order is gone, so the common case is
    /// one or two.
    /// </summary>
    private static readonly TimeSpan VerifyPollInterval = TimeSpan.FromSeconds(1);

    private readonly AhkPortalClient _portal;
    private readonly IRuntimePluginOptions<AhkConfig> _config;
    private readonly ILogger<CancelOrderTool> _logger;

    public CancelOrderTool(
        AhkPortalClient portal,
        IRuntimePluginOptions<AhkConfig> config,
        ILogger<CancelOrderTool> logger)
    {
        _portal = portal;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// How long to wait for a cancelled order to leave the book. The portal processes a cancel
    /// asynchronously against the exchange, so an immediate re-read routinely still shows it — and in
    /// the pre-open state it can take appreciably longer. See <see cref="AhkConfig.CancelVerifyTimeoutMs"/>.
    /// </summary>
    private TimeSpan VerifyTimeout =>
        TimeSpan.FromMilliseconds(Math.Max(2_000, _config.Current.CancelVerifyTimeoutMs));

    public override string Name => "cancel_order";

    public override string Description =>
        "Cancel a REAL resting order on the broker account. Identify the order either by 'order_no' " +
        "(preferred — get it from list_outstanding_orders) or by 'symbol' when the account has " +
        "exactly one working order for that symbol. The cancellation is verified against the " +
        "broker's own order book before reporting success, so trust this tool's verdict and never " +
        "assume a cancel worked. If several orders match a symbol, the tool refuses and lists them; " +
        "ask the user which order number they mean.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["order_no"] = new()
        {
            Type = "string",
            Description = "The exact order number to cancel, as reported by list_outstanding_orders.",
            Required = false
        },
        ["symbol"] = new()
        {
            Type = "string",
            Description =
                "PSX ticker whose single working order should be cancelled, e.g. OGDC. Used only " +
                "when order_no is not supplied, and only if exactly one order matches.",
            Required = false
        },
        ["side"] = new()
        {
            Type = "string",
            Description = "Optional side filter to disambiguate a symbol: BUY or SELL.",
            Required = false,
            EnumValues = ["ALL", "BUY", "SELL"]
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var orderNo = ToolArgs.Text(arguments, "order_no")?.Trim();
        var symbol = ToolArgs.Text(arguments, "symbol")?.Trim().ToUpperInvariant() ?? "";
        var side = AhkOrderSide.Normalize(ToolArgs.Text(arguments, "side"));

        if (string.IsNullOrWhiteSpace(orderNo) && symbol.Length == 0)
        {
            return ToolResult.Fail(
                "Specify either 'order_no' or 'symbol'. Call list_outstanding_orders first to see " +
                "the working orders and their order numbers.");
        }

        // Note there is NO kill-switch gate here, unlike order placement. The kill switch exists to
        // stop the agent taking on risk; cancelling a resting order removes risk, and an emergency
        // stop that also blocked cancels would trap the account in exactly the exposure the switch
        // was flipped to escape.

        try
        {
            var read = await _portal.GetOutstandingAsync(symbol, symbol.Length > 0 ? side : AhkOrderSide.All);
            if (!read.Ok)
            {
                // Refuse rather than proceed. Without a readable book there is no way to identify the
                // right order, and no way to verify the outcome afterwards.
                return ToolResult.Fail(
                    $"{read.Error} Nothing was cancelled. Do not assume anything about the account's " +
                    "orders until the book can be read again.");
            }

            var target = CancelTargetResolver.Resolve(read.Orders, orderNo, symbol, side);
            if (target.Error is { } error) return ToolResult.Fail(error);

            var order = target.Order!;
            _logger.LogInformation("[CancelOrder] Cancelling {Order}.", order.Describe());

            var accepted = await _portal.CancelOrderAsync(order.OrderNo!);
            if (!accepted)
            {
                return ToolResult.Fail(
                    $"The broker rejected the cancel request for order #{order.OrderNo} " +
                    $"({order.Describe()}). The order is most likely still working — verify with " +
                    "list_outstanding_orders before trying again.");
            }

            var gone = await WaitUntilGoneAsync(order.OrderNo!);

            if (gone)
            {
                _logger.LogInformation("[CancelOrder] Confirmed: order {OrderNo} left the book.", order.OrderNo);
                return ToolResult.Ok(JsonSerializer.Serialize(new
                {
                    cancelled = true,
                    verified = true,
                    order_no = order.OrderNo,
                    side = order.Type,
                    symbol = order.Scrip,
                    price = order.Price,
                    remaining_qty = order.Remaining,
                    message = $"Order #{order.OrderNo} ({order.Describe()}) was cancelled and is no " +
                              "longer in the broker's outstanding order book."
                }, SnakeOptions));
            }

            // Accepted but still resting. This is genuinely ambiguous — the exchange may still be
            // processing it, or the cancel may have been dropped — and saying "cancelled" here is the
            // one answer that is definitely wrong.
            _logger.LogWarning(
                "[CancelOrder] Order {OrderNo} was still in the book {Seconds}s after the cancel request.",
                order.OrderNo, VerifyTimeout.TotalSeconds);

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                cancelled = false,
                verified = false,
                order_no = order.OrderNo,
                side = order.Type,
                symbol = order.Scrip,
                message =
                    $"The cancel request for order #{order.OrderNo} was accepted, but the order was " +
                    $"STILL in the outstanding book {VerifyTimeout.TotalSeconds:F0}s later. It may be " +
                    "processing at the exchange, or the cancel may not have taken effect. Tell the " +
                    "user it is unconfirmed and re-check with list_outstanding_orders — do not " +
                    "report it as cancelled, and do not blindly retry."
            }, SnakeOptions));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CancelOrder] Cancel failed.");
            return ToolResult.Fail(
                $"The cancel could not be completed: {ex.Message}. The order's status is UNKNOWN — " +
                "check list_outstanding_orders before assuming anything about it.");
        }
    }

    /// <summary>
    /// Polls the outstanding book until the order disappears or the timeout expires. Bounded polling
    /// rather than a fixed sleep, because the cancel round-trips to the exchange and its latency is
    /// not something this code gets to assume.
    /// </summary>
    /// <summary>
    /// Polls the outstanding book until the order demonstrably disappears, or the timeout expires.
    ///
    /// <para>
    /// A read that FAILS is never treated as "gone". That distinction is the whole point: when the
    /// broker blocked account access mid-test on 2026-08-18, the book came back empty, and an earlier
    /// version of this method read that as confirmation and reported the cancel verified-complete
    /// while the order was still live. "I cannot see the book" and "the order is not in the book" are
    /// opposite conclusions, and only one of them justifies telling a user their order is gone.
    /// </para>
    /// </summary>
    private async Task<bool> WaitUntilGoneAsync(string orderNo)
    {
        var deadline = DateTime.UtcNow + VerifyTimeout;

        do
        {
            await Task.Delay(VerifyPollInterval);

            var read = await _portal.GetOutstandingAsync();
            if (!read.Ok)
            {
                _logger.LogWarning(
                    "[CancelOrder] Could not read the order book while verifying {OrderNo}: {Error}",
                    orderNo, read.Error);
                continue; // keep trying; never conclude "gone" from a failed read
            }

            if (!read.Orders.Any(o =>
                    string.Equals(o.OrderNo?.Trim(), orderNo, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }
}
