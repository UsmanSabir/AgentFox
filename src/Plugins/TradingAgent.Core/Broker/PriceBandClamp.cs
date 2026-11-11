namespace TradingAgent.Broker;

/// <summary>
/// Moves a limit price into the day's price band, but only ever in the operator's favour. Pure.
///
/// <para>
/// <b>The rule: an order never sells below the price the operator set, and never buys above it.</b>
/// A BUY above the Upper Cap is lowered to the cap (it pays less), and a SELL below the Lower Lock is
/// raised to the lock (it receives more). The other two moves are refused: lowering a SELL to the
/// Upper Cap would sell below the operator's price, and raising a BUY to the Lower Lock would buy above
/// it. Those prices are sent unchanged and the exchange refuses them out loud, which is the honest
/// answer to an order that cannot trade at its own price today.
/// </para>
///
/// <para>
/// Until 2026-11-10 the clamp moved both ways, so a take-profit resting above the day's cap was sold
/// at the cap, and a keep-working sell whose stock had fallen far enough was re-placed below the
/// minimum it was armed with.
/// </para>
/// </summary>
public static class PriceBandClamp
{
    public static (decimal Price, string? Adjustment) Apply(
        bool isBuy, decimal requested, decimal? lowerLock, decimal? upperCap)
    {
        if (isBuy && upperCap is > 0m && requested > upperCap.Value)
            return (upperCap.Value,
                $"Limit clamped down from {requested:F2} to the day's Upper Cap {upperCap.Value:F2}.");

        if (!isBuy && lowerLock is > 0m && requested < lowerLock.Value)
            return (lowerLock.Value,
                $"Limit clamped up from {requested:F2} to the day's Lower Lock {lowerLock.Value:F2}.");

        return (requested, null);
    }
}
