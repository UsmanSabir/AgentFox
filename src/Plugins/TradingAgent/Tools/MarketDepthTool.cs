using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;
using TradingAgent.Feed;

namespace TradingAgent.Tools;

/// <summary>
/// Subscribes one symbol's market depth on the broker feed and returns what has arrived.
///
/// <para>
/// This is the ONLY source of order-book depth available to the plugin. The AHL analytics portal has
/// none — every order-book endpoint there returns 500 and its websocket carries best bid/ask only — so
/// anything needing resting quantity behind the touch has to come through here, which means it needs a
/// live broker session.
/// </para>
///
/// <para>
/// The ladder is returned in decision-ready form: best bid and ask with the quantity at the touch,
/// the spread, total resting volume each side, and the book imbalance. Two publishing quirks are
/// handled before the caller sees anything — the portal pads its arrays with zero rows, and it
/// republishes only when the book changes — so an empty poll never reads as an empty book and a
/// padding row never reads as a price of zero.
/// </para>
/// </summary>
public sealed class MarketDepthTool : BaseTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AhkFeedWorker _feed;
    private readonly AhkDepthBook _depth;
    private readonly ILogger<MarketDepthTool> _logger;

    public MarketDepthTool(AhkFeedWorker feed, AhkDepthBook depth, ILogger<MarketDepthTool> logger)
    {
        _feed = feed;
        _depth = depth;
        _logger = logger;
    }

    public override string Name => "get_market_depth";

    public override string Description =>
        "Read the order book (market depth) for ONE PSX symbol from the broker feed — the only source " +
        "of depth available, since the AHL analytics portal provides none. Returns MBP (market by " +
        "price: resting quantity aggregated per price level, the ladder a trader reads) and MBO " +
        "(market by order: individual orders). Subscribing replaces whichever symbol depth was " +
        "following, because the portal supports one depth symbol at a time. Depth needs a live broker " +
        "session and only updates while the market is open. Rows are returned as the portal sends them.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["symbol"] = new()
        {
            Type = "string",
            Description = "PSX ticker to follow, e.g. PPL. Omit to read whatever depth is already " +
                          "subscribed without changing the subscription.",
            Required = false
        },
        ["wait_seconds"] = new()
        {
            Type = "integer",
            Description = "After subscribing, how long to wait for the first rows to arrive (0-30). " +
                          "Defaults to 6. Depth arrives on the feed's normal poll, so a new " +
                          "subscription needs a poll or two before it has anything.",
            Required = false
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(
        Dictionary<string, object?> arguments)
    {
        var symbol = ToolArgs.Text(arguments, "symbol")?.Trim().ToUpperInvariant();
        var wait = Math.Clamp(ToolArgs.Int(arguments, "wait_seconds") ?? 6, 0, 30);

        if (symbol is not null)
        {
            var refusal = await _feed.FocusDepthAsync(symbol);
            if (refusal is not null) return ToolResult.Fail(refusal);

            // Depth arrives on the feed's own poll rather than in response to the subscription, so an
            // immediate read would almost always be empty and look like an absent book.
            var deadline = DateTime.UtcNow.AddSeconds(wait);
            while (DateTime.UtcNow < deadline && _depth.Get("REG", symbol) is null)
                await Task.Delay(500);
        }

        var target = symbol ?? _depth.SubscribedSymbol;
        if (target is null)
        {
            return ToolResult.Fail(
                "No symbol is subscribed for depth. Pass a symbol to start following one.");
        }

        var entry = _depth.Get("REG", target);
        var payload = new
        {
            symbol = target,
            subscribed_symbol = _depth.SubscribedSymbol,
            market_status = _feed.MarketStatus,
            level_count = entry?.Levels.Count ?? 0,
            order_count = entry?.Orders.Count ?? 0,
            // The scalars a decision actually turns on, ahead of the ladder itself.
            best_bid = entry?.BestBid,
            best_ask = entry?.BestAsk,
            bid_volume_at_touch = entry?.BidVolumeAtTouch,
            ask_volume_at_touch = entry?.AskVolumeAtTouch,
            spread = entry?.Spread,
            total_bid_volume = entry?.TotalBidVolume,
            total_ask_volume = entry?.TotalAskVolume,
            // -1 = all offered, +1 = all bid. The most useful single number from depth.
            imbalance = entry?.Imbalance,
            levels_at_utc = entry?.LevelsAtUtc,
            orders_at_utc = entry?.OrdersAtUtc,
            // Each row pairs the two ladders by index: bid_* is the bid side, ask_* the ask side.
            levels = entry?.Levels.Select(l => new
            {
                bid_orders = l.BidOrders, bid_volume = l.BidVolume, bid_price = l.BidPrice,
                ask_price = l.AskPrice, ask_volume = l.AskVolume, ask_orders = l.AskOrders
            }),
            orders = entry?.Orders.Select(o => new
            {
                bid_price = o.BidPrice, bid_volume = o.BidVolume, bid_flag = o.BidFlag,
                ask_price = o.AskPrice, ask_volume = o.AskVolume, ask_flag = o.AskFlag
            }),
            total_rows_ever_seen = _depth.RowsSeen,
            note = entry is null
                ? "Nothing has arrived yet. Depth only streams while the market is open and needs a " +
                  "live broker session. The portal republishes the book only when it CHANGES, so a " +
                  "fresh subscription can wait several polls on a quiet symbol."
                : null
        };

        _logger.LogDebug("[MarketDepth] {Symbol}: {Levels} levels / {Orders} orders, spread {Spread}.",
            target, payload.level_count, payload.order_count, payload.spread);

        return ToolResult.Ok(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
