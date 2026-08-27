using System.Text.Json;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;
using TradingAgent.Broker;

namespace TradingAgent.Tools;

/// <summary>
/// Lists the account's resting (unfilled) orders straight from the broker's order book.
///
/// <para>
/// This is the read half of the cancel workflow: <c>cancel_order</c> needs an order number, and the
/// order number only exists here. It is also the honest answer to "what orders do I have working",
/// which the plugin previously could only approximate by scraping the Outstanding Log tab in a
/// browser.
/// </para>
///
/// <para>
/// Backed by <see cref="IBrokerOutstandingOrdersReader"/> rather than a concrete client, so which
/// broker answers depends on which adapter is active. Previously this took <c>AhkPortalClient</c>
/// directly, which meant a premium account running the AHL SOAP integration would still need the
/// AHK browser-cookie session for this one read.
/// </para>
/// </summary>
public sealed class ListOutstandingOrdersTool : BaseTool
{
    private static readonly JsonSerializerOptions SnakeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly IBrokerOutstandingOrdersReader _reader;
    private readonly ILogger<ListOutstandingOrdersTool> _logger;

    public ListOutstandingOrdersTool(
        IBrokerOutstandingOrdersReader reader, ILogger<ListOutstandingOrdersTool> logger)
    {
        _reader = reader;
        _logger = logger;
    }

    public override string Name => "list_outstanding_orders";

    public override string Description =>
        "List the REAL resting (working, unfilled) orders on the broker account, with their order " +
        "numbers, side, symbol, price and remaining quantity. Use this whenever the user asks what " +
        "orders are pending, and ALWAYS before cancelling an order — cancel_order needs an order " +
        "number that only this tool can supply. Never invent or guess an order number.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["symbol"] = new()
        {
            Type = "string",
            Description = "Optional PSX ticker to filter by, e.g. OGDC. Omit for all symbols.",
            Required = false
        },
        ["side"] = new()
        {
            Type = "string",
            Description = "Optional side filter: ALL (default), BUY, or SELL.",
            Required = false,
            EnumValues = ["ALL", "BUY", "SELL"]
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var symbol = ToolArgs.Text(arguments, "symbol")?.Trim().ToUpperInvariant() ?? "";
        var side = AhkOrderSide.Normalize(ToolArgs.Text(arguments, "side"));

        try
        {
            var book = await _reader.GetOutstandingOrdersAsync(symbol.Length == 0 ? null : symbol);
            var orders = book.Where(o => AhkOrderSide.Matches(o.Side, side)).ToList();

            return ToolResult.Ok(JsonSerializer.Serialize(new
            {
                filter = new { symbol = symbol.Length == 0 ? null : symbol, side },
                count = orders.Count,
                orders = orders.Select(o => new
                {
                    order_no      = o.OrderNo,
                    side          = o.Side,
                    order_type    = o.OrderType,
                    symbol        = o.Symbol,
                    price         = o.Price,
                    remaining_qty = o.Quantity
                }),
                note = orders.Count == 0
                    ? "The account has no resting orders matching this filter."
                    : "Use the order_no value with cancel_order to cancel one of these."
            }, SnakeOptions));
        }
        catch (Exception ex)
        {
            // Reporting "0 orders" here would be a lie with consequences: an operator told the
            // account is flat may leave a live order running, or place a duplicate.
            _logger.LogError(ex, "[ListOutstandingOrders] Read failed.");
            return ToolResult.Fail(
                $"Could not read the outstanding order book from the broker: {ex.Message}. " +
                "Do NOT tell the user they have no working orders — the book could not be read. " +
                "Check the broker portal directly.");
        }
    }
}

/// <summary>
/// Maps between the plain-English side an operator uses and the portal's own three-letter
/// vocabulary. The portal says <c>SEL</c>, not <c>SELL</c>; sending the natural spelling silently
/// matches nothing rather than erroring, which is the kind of filter bug that reads as "no orders".
/// </summary>
public static class AhkOrderSide
{
    public const string All = "ALL";
    public const string Buy = "BUY";
    public const string Sell = "SEL";

    public static string Normalize(string? side) => side?.Trim().ToUpperInvariant() switch
    {
        null or "" or "ALL" or "BOTH" => All,
        "BUY" or "B" => Buy,
        "SELL" or "SEL" or "S" => Sell,
        var other => other
    };

    /// <summary>True when a portal-reported side matches a normalized filter.</summary>
    public static bool Matches(string? portalSide, string normalizedFilter) =>
        normalizedFilter == All ||
        string.Equals(Normalize(portalSide), normalizedFilter, StringComparison.OrdinalIgnoreCase);
}
