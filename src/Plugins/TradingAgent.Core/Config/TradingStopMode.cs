namespace TradingAgent.Config;

/// <summary>
/// How far the operator has stopped trading.
///
/// <para>
/// <b>Why two levels rather than one switch.</b> PSX clears every resting order at the close, so a
/// protective stop exists tomorrow only if something places it again tomorrow. A single kill switch that
/// refuses every order therefore does not merely pause trading: from the next session on, every position
/// is unprotected for as long as it stays on. <see cref="EntriesOff"/> is the stop an operator almost
/// always means — nothing new is bought, and everything that only REDUCES a position (protective stops,
/// take-profits, exits) keeps working. <see cref="FullStop"/> is the old switch, kept for the case where
/// the operator wants nothing at all sent, knowingly.
/// </para>
/// </summary>
public enum TradingStopMode
{
    /// <summary>Trading as configured.</summary>
    Off = 0,

    /// <summary>No BUY is sent. SELLs, which on the cash market can only reduce a position, still are.</summary>
    EntriesOff = 1,

    /// <summary>Nothing is sent, protective stops included. The legacy <c>killSwitch: true</c>.</summary>
    FullStop = 2
}

public static class TradingStopModes
{
    /// <summary>
    /// Reads a stored or requested mode. Accepts the enum names and the snake_case spellings an API caller
    /// is likely to send (<c>entries_off</c>, <c>full_stop</c>). Anything unrecognised is null — never Off,
    /// because reading a garbled "stop" as "trade normally" is the dangerous direction.
    /// </summary>
    public static TradingStopMode? Parse(string? value) =>
        value?.Trim().Replace("_", "").Replace("-", "").ToUpperInvariant() switch
        {
            "OFF" or "NONE" => TradingStopMode.Off,
            "ENTRIESOFF" => TradingStopMode.EntriesOff,
            "FULLSTOP" or "ALL" => TradingStopMode.FullStop,
            _ => null
        };

    /// <summary>The value written to the runtime store and returned by the API.</summary>
    public static string ToWire(this TradingStopMode mode) => mode switch
    {
        TradingStopMode.EntriesOff => "EntriesOff",
        TradingStopMode.FullStop => "FullStop",
        _ => "Off"
    };
}
