namespace TradingAgent.Trading;

/// <summary>
/// Budget-based position sizing. When a signal arrives without an explicit share count, the quantity
/// to buy is derived from a per-stock budget: spend at most (budget × (1 − buffer)) so that a fill at
/// the limit price plus fees/slippage still lands under budget, then floor to whole shares.
///
/// Pure arithmetic on purpose — sizing must be deterministic and is never delegated to the LLM, which
/// is unreliable at arithmetic.
/// </summary>
public static class PositionSizer
{
    /// <summary>
    /// Returns the whole-share quantity buyable for <paramref name="budgetPkr"/> at
    /// <paramref name="pricePkr"/>, holding back <paramref name="bufferPercent"/>% as headroom for fees
    /// and price drift. Returns null when no positive whole-share quantity fits (non-positive budget or
    /// price, or the price exceeds the buffered budget).
    /// </summary>
    public static int? ComputeQuantity(decimal budgetPkr, decimal pricePkr, decimal bufferPercent)
    {
        if (budgetPkr <= 0m || pricePkr <= 0m) return null;

        var buffer = Math.Clamp(bufferPercent, 0m, 100m) / 100m;
        var usable = budgetPkr * (1m - buffer);

        var qty = (int)Math.Floor(usable / pricePkr);
        return qty > 0 ? qty : null;
    }
}
