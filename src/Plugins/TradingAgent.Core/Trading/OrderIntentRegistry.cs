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
///
/// <para>
/// <b>WHO HOLDS THE ORDER is part of the label, not just the description.</b> A native broker stop
/// rests at the exchange and fires whether or not this process is running; an AgentFox trigger fires
/// only while the app is up and the market is open. That is the most consequential difference on the
/// whole board, and until 2026-09-22 it was carried by "Buy IF it rises to a price" against "Buy WHEN
/// it rises to a price" — two cards a reader could not tell apart, distinguished by a conjunction.
/// The broker-held ones now say so in their titles, and every label on the board is unique in more
/// than one word.
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

        new("scheduled-buy", "Buy on a date at my price",
            "On or after your PSX date, submit a limit buy at your price or lower.",
            "Schedule for a date", "conditional", "BUY", "LIMIT", "Scheduled", PriceField: "limit",
            RequiresActivationDate: true),
        new("scheduled-sell", "Sell on a date at my price",
            "On or after your PSX date, submit a limit sell at your price or higher.",
            "Schedule for a date", "conditional", "SELL", "LIMIT", "Scheduled", PriceField: "limit",
            RequiresActivationDate: true),
        new("scheduled-market-sell", "Sell on a date at market",
            "On or after your PSX date, sell at the best available price; proceeds can move.",
            "Schedule for a date", "conditional", "SELL", "MARKET", "Scheduled", PriceField: "none",
            RequiresActivationDate: true),
        new("scheduled-market-sell-above", "Sell after a date if price is high enough",
            "Starting on your PSX date, wait for your minimum price, then sell at market. The fill can be lower.",
            "Schedule for a date", "conditional", "SELL", "MARKET", "PriceAbove", PriceField: "trigger",
            RequiresActivationDate: true),

        new("profit-book", "Book profit at a target",
            "A sell limit at your target price for shares you already own — the same order as \"Sell at my price\", worded for taking a profit.",
            "Protect & exit", "immediate", "SELL", "LIMIT", PriceField: "target"),
        new("stop-loss", "Stop loss held by the broker",
            "Rests at the exchange and sells when the price falls to your level — it works even when AgentFox is not running.",
            "Protect & exit", "immediate", "SELL", "STOPLOSS", PriceField: "stop"),
        new("trailing-stop", "Trailing stop",
            "The sell level follows the highest price since arming and never drops. Sells the whole quantity at once; AgentFox must be running.",
            "Protect & exit", "conditional", "SELL", "MARKET", "PercentDrop",
            PriceField: "none", DefaultPercent: 3m, Trailing: true),
        // Measured from the recent HIGH, not from the price at arming: "it has fallen 3% from where
        // it just was". A fixed arm-time reference let a stock rise 5% and then need an ~8% fall to
        // fire; the trailing stop above covers "the highest since I armed it". Owner, 2026-09-22.
        new("sell-after-drop", "Sell if it drops by a %",
            "Sell at market once the price falls your chosen % below its highest price of the recent window.",
            "Protect & exit", "conditional", "SELL", "MARKET", "PercentDrop",
            PriceField: "none", DefaultPercent: 3m, DefaultWindowMinutes: 15),

        new("wait-buy-drops", "Buy when it drops to a price",
            "AgentFox watches the price and submits a limit buy when it drops to your trigger. It only fires while AgentFox is running.",
            "Wait for a price", "conditional", "BUY", "LIMIT", "PriceBelow",
            PriceField: "trigger-and-limit"),
        new("wait-buy-rises", "Buy when it rises to a price",
            "AgentFox watches the price and submits a limit buy when it rises to your trigger. It only fires while AgentFox is running.",
            "Wait for a price", "conditional", "BUY", "LIMIT", "PriceAbove",
            PriceField: "trigger-and-limit"),
        new("wait-sell-drops", "Sell when it drops to a price",
            "AgentFox watches the price and submits a limit sell when it drops to your trigger. It only fires while AgentFox is running.",
            "Wait for a price", "conditional", "SELL", "LIMIT", "PriceBelow",
            PriceField: "trigger-and-limit"),
        new("wait-sell-rises", "Sell when it rises to a price",
            "AgentFox watches the price and submits a limit sell when it rises to your trigger. It only fires while AgentFox is running.",
            "Wait for a price", "conditional", "SELL", "LIMIT", "PriceAbove",
            PriceField: "trigger-and-limit"),

        new("buy-on-rise", "Breakout buy held by the broker",
            "Rests at the exchange and buys once the price reaches your breakout level — it works even when AgentFox is not running.",
            "React to a move", "immediate", "BUY", "STOPLOSS", PriceField: "stop"),
        // The three below measure from the recent extreme too, for the same reason as sell-after-drop
        // (owner, 2026-09-22). A LIMIT one is priced at the level it FIRES at, not the level quoted at
        // arming — the level moves with the window. See ArmedOrder.PriceAtFire.
        new("buy-after-rise", "Buy if it rises by a %",
            "Buy at market once the price rises your chosen % above its lowest price of the recent window.",
            "React to a move", "conditional", "BUY", "MARKET", "PercentRise",
            PriceField: "none", DefaultPercent: 3m, DefaultWindowMinutes: 15),
        new("buy-after-drop", "Buy if it drops by a %",
            "Once the price falls your chosen % below its highest price of the recent window, place a limit buy at that level.",
            "React to a move", "conditional", "BUY", "LIMIT", "PercentDrop",
            PriceField: "limit-at-trigger", DefaultPercent: 3m, DefaultWindowMinutes: 15),
        new("sell-after-rise", "Sell if it rises by a %",
            "Once the price rises your chosen % above its lowest price of the recent window, place a limit sell at that level.",
            "React to a move", "conditional", "SELL", "LIMIT", "PercentRise",
            PriceField: "limit-at-trigger", DefaultPercent: 3m, DefaultWindowMinutes: 15)
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
    bool Trailing = false,
    bool RequiresActivationDate = false,
    int? DefaultWindowMinutes = null);
