namespace TradingAgent.Risk;

/// <summary>
/// The one rule relating a stop order's trigger to its limit price, shared by the arm-time check and
/// the pre-trade risk engine so the two can never disagree.
///
/// <para>
/// <b>What actually governs this is the trigger's DIRECTION, not the order's side.</b> A stop fires
/// into a market that is already moving, and the limit has to sit on the side that move is heading
/// toward, or the order rests where price has just left and never fills:
/// </para>
///
/// <list type="bullet">
///   <item>Trigger fires on a RISING price (a breakout entry, or a sell into strength) — the limit
///         must be at or ABOVE the trigger, because price is climbing away from it.</item>
///   <item>Trigger fires on a FALLING price (a protective exit, or a buy-the-dip entry) — the limit
///         must be at or BELOW the trigger, because price is falling away from it.</item>
/// </list>
///
/// <para>
/// The earlier version of this rule inferred direction from the side alone — BUY meant rising, SELL
/// meant falling. That holds for the two most common shapes and is wrong for the others. It refused a
/// real armed order live on 2026-08-18: a BUY on FFC set to fire when price fell to 550.06 with a
/// limit of 544.56. Price is moving DOWN when that trigger fires, so a limit below it is exactly
/// right, but the side-based rule read "BUY" as "breakout" and rejected it as unfillable. The order
/// sat armed for hours and was refused at the only moment it mattered.
/// </para>
/// </summary>
public static class StopLimitRule
{
    /// <summary>
    /// Validates one stop order, returning null when it is acceptable and the reason when it is not.
    ///
    /// <para>
    /// <paramref name="firesOnRisingPrice"/> is the trigger's direction where it is known: true for a
    /// trigger that fires as price rises, false for one that fires as price falls, and null when the
    /// caller genuinely has no direction — a stop order submitted directly rather than through an
    /// armed trigger. Null falls back to the side convention (BUY rises, SELL falls), which is the
    /// right default for a bare stop order and preserves the previous behaviour for that path.
    /// </para>
    /// </summary>
    public static string? Validate(
        string? action,
        string? orderType,
        bool? firesOnRisingPrice,
        decimal? trigger,
        decimal? limit)
    {
        if (!string.Equals(orderType?.Trim(), "STOPLOSS", StringComparison.OrdinalIgnoreCase))
            return null;

        if (limit is not { } stopLimit) return null;
        if (stopLimit <= 0) return "stop-loss limit price must be positive.";
        if (trigger is not { } level || level <= 0) return null;

        var side = action?.Trim().ToUpperInvariant();
        var rising = firesOnRisingPrice ?? side == "BUY";

        if (rising && stopLimit < level)
        {
            return $"this stop fires as the price RISES through {level}, so its limit ({stopLimit}) "
                 + $"must be at or above the trigger, or price will have moved past it before it can fill.";
        }

        if (!rising && stopLimit > level)
        {
            return $"this stop fires as the price FALLS through {level}, so its limit ({stopLimit}) "
                 + $"must be at or below the trigger, or price will have moved past it before it can fill.";
        }

        return null;
    }
}
