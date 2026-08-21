namespace TradingAgent.Trading;

/// <summary>
/// Human-facing ways to place an order from the dashboard.
///
/// <para>
/// The broker vocabulary (LIMIT, MARKET, STOPLOSS) is deliberately kept behind this registry. The
/// dashboard asks what the person wants to achieve, while the registry supplies the side, broker
/// order type, trigger kind, defaults, and fields that intent needs. Adding a broker order type or a
/// new conditional trigger therefore starts here instead of growing another hard-coded dialog.
/// </para>
/// </summary>
public static class OrderIntentRegistry
{
    public static IReadOnlyList<OrderIntentDefinition> All { get; } =
    [
        new("limit-buy", "Buy at my price",
            "Place a buy limit now; it fills only at your price or lower.",
            "Buy & sell", "immediate", "BUY", "LIMIT", PriceField: "limit"),
        new("limit-sell", "Sell at my price",
            "Place a sell limit now; it fills only at your price or higher.",
            "Buy & sell", "immediate", "SELL", "LIMIT", PriceField: "limit"),
        new("market-buy", "Buy now",
            "Buy at the best available price; the final cost can move.",
            "Buy & sell", "immediate", "BUY", "MARKET", PriceField: "none"),
        new("market-sell", "Sell now",
            "Sell at the best available price; the final proceeds can move.",
            "Buy & sell", "immediate", "SELL", "MARKET", PriceField: "none"),

        new("profit-book", "Book profit at a target",
            "Place a sell limit at your target price for shares you already own.",
            "Protect & exit", "immediate", "SELL", "LIMIT", PriceField: "target"),
        new("stop-loss", "Sell if it drops to a price",
            "A native broker stop: trigger a sell when the price falls to your level.",
            "Protect & exit", "immediate", "SELL", "STOPLOSS", PriceField: "stop"),
        new("trailing-stop", "Trailing stop",
            "Follow gains upward, then sell if price falls by your chosen percentage.",
            "Protect & exit", "conditional", "SELL", "MARKET", "PercentDrop",
            PriceField: "none", DefaultPercent: 3m, Trailing: true),
        new("sell-after-drop", "Sell if it drops by a %",
            "Watch from the current price and sell after the chosen percentage fall.",
            "Protect & exit", "conditional", "SELL", "MARKET", "PercentDrop",
            PriceField: "none", DefaultPercent: 3m),

        new("buy-on-rise", "Buy if it rises to a price",
            "A native broker stop: buy only after price reaches your breakout level.",
            "React to a move", "immediate", "BUY", "STOPLOSS", PriceField: "stop"),
        new("buy-after-rise", "Buy if it rises by a %",
            "Watch from the current price and buy after the chosen percentage rise.",
            "React to a move", "conditional", "BUY", "MARKET", "PercentRise",
            PriceField: "none", DefaultPercent: 3m),
        new("buy-after-drop", "Buy if it drops by a %",
            "Watch from the current price and place a limit buy after the chosen fall.",
            "React to a move", "conditional", "BUY", "LIMIT", "PercentDrop",
            PriceField: "limit-at-trigger", DefaultPercent: 3m),
        new("sell-after-rise", "Sell if it rises by a %",
            "Watch from the current price and place a limit sell after the chosen rise.",
            "React to a move", "conditional", "SELL", "LIMIT", "PercentRise",
            PriceField: "limit-at-trigger", DefaultPercent: 3m)
    ];

    public static OrderIntentDefinition? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(item => item.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed record OrderIntentDefinition(
    string Id,
    string Label,
    string Description,
    string Category,
    string Submission,
    string Action,
    string OrderType,
    string? TriggerKind = null,
    string PriceField = "none",
    decimal? DefaultPercent = null,
    bool Trailing = false);
