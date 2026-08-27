using System.Text.Json;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;
using TradingAgent.Broker;
using TradingAgent.Feed;
using TradingAgent.Watchlist;

namespace TradingAgent.Tools;

/// <summary>
/// Cancels a resting order on the broker account.
///
/// <para>
/// <b>Verification is not this tool's job any more — it is <see cref="IBrokerOrderCanceller"/>'s.</b>
/// This used to poll the outstanding book itself (over the concrete <c>AhkPortalClient</c>, unusable
/// for a premium account on the AHL SOAP integration) after an HTTP 200 that means nothing on its own.
/// <see cref="IBrokerOrderCanceller.CancelOrderAsync"/> already does exactly that verified-cancel work,
/// broker-neutral, once per adapter — over the outstanding book for AHK, over the order socket's
/// confirmed <c>CXL</c> echo for AHL — so this tool only needs to interpret its
/// <see cref="BrokerCancellationResult"/>, not re-derive it.
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

    private readonly IBrokerOutstandingOrdersReader _reader;
    private readonly IBrokerOrderCanceller _canceller;
    private readonly ILogger<CancelOrderTool> _logger;

    public CancelOrderTool(
        IBrokerOutstandingOrdersReader reader,
        IBrokerOrderCanceller canceller,
        ILogger<CancelOrderTool> logger)
    {
        _reader = reader;
        _canceller = canceller;
        _logger = logger;
    }

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
            var book = await _reader.GetOutstandingOrdersAsync(symbol.Length > 0 ? symbol : null);
            var target = CancelTargetResolver.Resolve(book.Select(ToAhkShape).ToList(), orderNo, symbol, side);
            if (target.Error is { } error) return ToolResult.Fail(error);

            var order = target.Order!;
            _logger.LogInformation("[CancelOrder] Cancelling {Order}.", order.Describe());

            var result = await _canceller.CancelOrderAsync(order.OrderNo!);

            if (result.Verified)
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

            if (!result.RequestAccepted)
            {
                return ToolResult.Fail(
                    $"The broker rejected the cancel request for order #{order.OrderNo} " +
                    $"({order.Describe()}): {result.Message} The order is most likely still " +
                    "working — verify with list_outstanding_orders before trying again.");
            }

            // Accepted but still resting/unconfirmed. This is genuinely ambiguous — the exchange may
            // still be processing it, or the cancel may have been dropped — and saying "cancelled"
            // here is the one answer that is definitely wrong.
            _logger.LogWarning(
                "[CancelOrder] Order {OrderNo} accepted but not verified gone: {Message}",
                order.OrderNo, result.Message);

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                cancelled = false,
                verified = false,
                order_no = order.OrderNo,
                side = order.Type,
                symbol = order.Scrip,
                message =
                    $"The cancel request for order #{order.OrderNo} was accepted, but it could not be " +
                    $"confirmed gone from the outstanding book: {result.Message} Tell the user it is " +
                    "unconfirmed and re-check with list_outstanding_orders — do not report it as " +
                    "cancelled, and do not blindly retry."
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
    /// Adapts the broker-neutral <see cref="RestingOrder"/> onto <see cref="CancelTargetResolver"/>'s
    /// pure, already-tested resolution logic rather than rewriting it — that logic never needed to know
    /// which broker filled it in.
    /// </summary>
    private static AhkOutstandingOrder ToAhkShape(RestingOrder o) => new()
    {
        OrderNo = o.OrderNo,
        Scrip = o.Symbol,
        Type = o.Side,
        Price = o.Price,
        Remaining = o.Quantity
    };
}
