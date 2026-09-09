namespace TradingAgent.Watchlist;

/// <summary>
/// The outcome of validating a protective stop the operator asked to attach to a BUY entry: either a
/// refusal with a stable code, or the exact trigger and limit the stop should be created with.
/// </summary>
public readonly record struct AttachedStopPlan(
    string? ErrorCode,
    string? Message,
    decimal StopTrigger,
    decimal StopLimit)
{
    public bool Ok => ErrorCode is null;
}

/// <summary>
/// Whether a protective stop may be attached to an entry, and at what levels.
///
/// <para>
/// <b>Shared by both entry paths on purpose.</b> "Protect this BUY with a stop" is offered on a
/// WAITING entry (armed, fires on a trigger) and on an IMMEDIATE one (an ordinary dashboard buy). The
/// rules are identical and there is nothing about them either path gets to decide, so they live here
/// rather than in two endpoints — the second copy is exactly where a rule quietly stops being applied.
/// </para>
///
/// <para>
/// Pure: takes prices and a side, reads no clock, performs no I/O. The endpoints do the reads and the
/// writes; this decides.
/// </para>
/// </summary>
public static class AttachedStopRule
{
    /// <summary>
    /// Validates a requested attachment.
    ///
    /// <para>
    /// A stop may only be attached to a BUY. It protects a position the entry creates, and attaching
    /// one to a SELL would arm a second sell of stock the first is already disposing of.
    /// </para>
    ///
    /// <para>
    /// <paramref name="entryPrice"/> is the entry's own limit or trigger, and is optional because a
    /// MARKET buy has neither. When it is known, a stop at or above it is refused: it would fire the
    /// moment it activated rather than protect anything. When it is NOT known the check is skipped
    /// rather than guessed at — a market buy's fill price is unknowable in advance, and refusing on a
    /// number nobody has would block the one order type most likely to need protecting.
    /// </para>
    /// </summary>
    public static AttachedStopPlan Validate(
        string? action, decimal? stopTrigger, decimal? stopLimit, decimal? entryPrice)
    {
        if (!string.Equals(action?.Trim(), "BUY", StringComparison.OrdinalIgnoreCase))
            return new("stop_requires_buy",
                "A protective stop can only be attached to a BUY entry.", 0m, 0m);

        if (stopTrigger is not > 0)
            return new("invalid_stop_trigger",
                "A protective stop needs a positive trigger price.", 0m, 0m);

        var trigger = stopTrigger.Value;

        if (entryPrice is { } entry && trigger >= entry)
            return new("stop_above_entry",
                $"A protective stop at {trigger} sits at or above the entry ({entry}), so it would "
                + "trigger immediately rather than protect anything.", 0m, 0m);

        // Default the limit just below the trigger. A stop limit set exactly AT its trigger routinely
        // misses the move that triggered it, which is protection in name only.
        var limit = stopLimit ?? Math.Round(trigger * 0.99m, 2, MidpointRounding.AwayFromZero);

        if (limit <= 0)
            return new("invalid_stop_limit",
                "A protective stop needs a positive worst-acceptable price.", 0m, 0m);

        if (limit > trigger)
            return new("invalid_stop_limit",
                $"A SELL stop's limit ({limit}) must be at or below its trigger ({trigger}), or it "
                + "cannot fill once triggered.", 0m, 0m);

        return new(null, null, trigger, limit);
    }
}
