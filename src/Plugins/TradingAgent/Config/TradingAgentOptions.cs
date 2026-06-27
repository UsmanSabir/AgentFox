namespace TradingAgent.Config;

public class TradingAgentOptions
{
    public const string SectionName = "TradingAgent";

    public bool AutoExecute { get; set; } = false;

    // HIGH, MEDIUM, or LOW
    public string MinConfidence { get; set; } = "HIGH";

    // Reserved: will resolve via IModelClientFactory once added to AgentFox.Plugins.
    // Currently the default IChatClient (from DI) is used for signal parsing.
    public string ParserModelKey { get; set; } = "CheapModel";

    public int DuplicateWindowMinutes { get; set; } = 60;

    /// <summary>
    /// When a BUY tip also specifies a target ("buy at 50, sell at 55"), automatically place a
    /// take-profit SELL limit order at the target after the BUY succeeds (default true). The follow-up
    /// sell is only attempted when the BUY actually succeeded. Set false to place only the BUY.
    /// </summary>
    public bool AutoPlaceTargetSell { get; set; } = true;

    /// <summary>
    /// How to handle a BUY tip that names a stock with a clear buy intent but gives NO entry price
    /// (e.g. "accumulate on dips"). When true, the entry is resolved from the live market price less
    /// <c>Ahk.DipDiscountPercent</c> and the order is placed (budget-sized). When false (default) the tip
    /// is recognized and logged but NOT executed, so a human can place it manually. Because a
    /// dip-discounted limit rests below market and may not fill, no take-profit SELL is auto-paired for
    /// these orders even when a target is given.
    /// </summary>
    public bool AutoBuyWithoutEntryPrice { get; set; } = false;
}
