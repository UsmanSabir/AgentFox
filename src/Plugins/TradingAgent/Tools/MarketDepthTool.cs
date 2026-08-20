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
/// <b>Rows are returned raw, on purpose.</b> The portal's depth payload had never been captured when
/// this was written, so its field names are unknown. Presenting a typed ladder would mean guessing
/// them, and a wrong guess yields a book full of nulls that reads as thin liquidity rather than as a
/// parsing failure — the kind of error that changes a sizing decision. So the rows are passed through
/// as received, together with the field names observed, and a typed model can follow from real data.
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
            by_price_rows = entry?.ByPrice.Count ?? 0,
            by_order_rows = entry?.ByOrder.Count ?? 0,
            by_price_at_utc = entry?.ByPriceAtUtc,
            by_order_at_utc = entry?.ByOrderAtUtc,
            // Raw, as received. See the class remarks for why this is not a typed ladder.
            by_price = entry?.ByPrice.Select(r => r.ToString()),
            by_order = entry?.ByOrder.Select(r => r.ToString()),
            // The field names seen so far — the artefact that lets a typed model be written from real
            // data rather than guessed.
            observed_by_price_fields = _depth.ObservedMbpKeys,
            observed_by_order_fields = _depth.ObservedMboKeys,
            total_rows_ever_seen = _depth.RowsSeen,
            note = entry is null
                ? "Nothing has arrived yet. Depth only streams while the market is open and needs a " +
                  "live broker session; a fresh subscription also needs a feed poll or two."
                : null
        };

        _logger.LogDebug("[MarketDepth] {Symbol}: {ByPrice} MBP / {ByOrder} MBO rows.",
            target, payload.by_price_rows, payload.by_order_rows);

        return ToolResult.Ok(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
