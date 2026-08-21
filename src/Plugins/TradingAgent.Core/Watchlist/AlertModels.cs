using TradingAgent.Analysis;
using TradingAgent.Config;

namespace TradingAgent.Watchlist;

/// <summary>
/// What the monitor noticed. Every kind is a TRANSITION — something that became true and was not true
/// before — because a condition that simply stays true is not news, and re-reporting it every pass is
/// what makes an alert feed get muted and ignored.
/// </summary>
public enum AlertKind
{
    /// <summary>Price was at/near support and has turned up off it. The bullish-from-support case.</summary>
    SupportBounce,

    /// <summary>Price is at resistance and has turned down. The take-profit / sell case.</summary>
    ResistanceRejection,

    /// <summary>Price closed below support by more than the noise buffer. The level failed.</summary>
    SupportBreak,

    /// <summary>Price closed above resistance by more than the noise buffer.</summary>
    ResistanceBreakout,

    /// <summary>The deterministic setup classification changed (e.g. into AvoidBreakdown).</summary>
    SetupChanged,

    /// <summary>SMA20 crossed SMA50 — a trend-state change, not a level event.</summary>
    TrendFlip,

    /// <summary>The WEEKLY chart entered a breakdown: fresh lows while still falling.</summary>
    WeeklyBreakdown,

    /// <summary>RSI crossed into oversold.</summary>
    RsiOversold,

    /// <summary>RSI crossed into overbought.</summary>
    RsiOverbought
}

/// <summary>
/// How much attention an alert deserves. Used for ordering, for the UI's colour, and as the gate for
/// optional auto-assessment — never as a trading instruction.
/// </summary>
public enum AlertSeverity
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>An alert the detector decided to raise, before it is persisted.</summary>
public sealed record DetectedAlert
{
    public required string Symbol { get; init; }
    public required AlertKind Kind { get; init; }
    public required AlertSeverity Severity { get; init; }

    /// <summary>The level the event is about, when it is a level event. Null for trend/RSI kinds.</summary>
    public decimal? LevelPrice { get; init; }

    /// <summary>Price at the moment of detection.</summary>
    public required decimal Price { get; init; }

    /// <summary>Bar width the detection ran on, e.g. <c>1D</c>.</summary>
    public required string Interval { get; init; }

    /// <summary>One line a human can act on, citing the numbers behind it.</summary>
    public required string Summary { get; init; }

    /// <summary>The deterministic reasons from the snapshot, kept so the alert stays explicable later.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>True when the level is confirmed on the weekly chart — structure rather than a swing.</summary>
    public bool WeeklyConfirmed { get; init; }

    /// <summary>True when the last bar was still forming, so the trigger could still un-happen.</summary>
    public bool FromLiveBar { get; init; }
}

/// <summary>
/// What the monitor remembers about a symbol between passes. Without this there are no transitions,
/// only conditions — and the feed would re-report the same standing situation forever.
/// </summary>
public sealed record SymbolMonitorState
{
    public required string Symbol { get; init; }

    public PriceZone Zone { get; init; } = PriceZone.Unknown;
    public TradeSetup Setup { get; init; } = TradeSetup.InsufficientData;

    /// <summary>Nearest support at the last pass, for detecting a break of it.</summary>
    public decimal? Support { get; init; }

    /// <summary>Nearest resistance at the last pass.</summary>
    public decimal? Resistance { get; init; }

    /// <summary><c>above</c> / <c>below</c> — SMA20 relative to SMA50, for cross detection.</summary>
    public string? SmaRelation { get; init; }

    /// <summary><c>oversold</c> / <c>overbought</c> / <c>neutral</c>.</summary>
    public string? RsiBand { get; init; }

    public bool WeeklyBreakdown { get; init; }

    /// <summary>
    /// Consecutive passes each candidate condition has held. A kind only fires once its streak reaches
    /// <see cref="MonitorThresholds.ConfirmPasses"/>, which is what stops a price sitting exactly on a
    /// level from firing on every tick.
    /// </summary>
    public IReadOnlyDictionary<AlertKind, int> Streaks { get; init; } =
        new Dictionary<AlertKind, int>();

    public DateTime UpdatedUtc { get; init; }

    /// <summary>True before the symbol has ever been seen — no transitions can be claimed yet.</summary>
    public bool IsNew { get; init; }
}

/// <summary>Detection thresholds, projected from configuration.</summary>
public sealed record MonitorThresholds
{
    /// <summary>Consecutive passes a condition must hold before it fires.</summary>
    public int ConfirmPasses { get; init; } = 2;

    /// <summary>
    /// How far beyond a level a close must be to count as a break rather than a wick through it.
    /// </summary>
    public decimal BreakBufferPercent { get; init; } = 0.5m;

    /// <summary>Volume multiple (vs the 30-bar average) required to call a break confirmed.</summary>
    public decimal VolumeConfirmRatio { get; init; } = 1.3m;

    /// <summary>RSI at or below this is oversold.</summary>
    public decimal RsiOversold { get; init; } = 35m;

    /// <summary>RSI at or above this is overbought.</summary>
    public decimal RsiOverbought { get; init; } = 70m;

    /// <summary>Within this percent of a level counts as "at" it.</summary>
    public decimal SupportProximityPercent { get; init; } = 2.5m;

    public decimal ResistanceProximityPercent { get; init; } = 2.5m;

    public static MonitorThresholds From(TradingAgentOptions options) => new()
    {
        ConfirmPasses              = Math.Clamp(options.Monitor.ConfirmPasses, 1, 10),
        BreakBufferPercent         = Math.Clamp(options.Monitor.BreakBufferPercent, 0m, 10m),
        VolumeConfirmRatio         = Math.Max(0m, options.Monitor.VolumeConfirmRatio),
        RsiOversold                = Math.Clamp(options.Scan.RsiOversold, 1m, 50m),
        RsiOverbought              = Math.Clamp(options.Scan.RsiOverbought, 50m, 99m),
        SupportProximityPercent    = Math.Clamp(options.Scan.SupportProximityPercent, 0.1m, 25m),
        ResistanceProximityPercent = Math.Clamp(options.Scan.ResistanceProximityPercent, 0.1m, 25m)
    };
}

/// <summary>Everything one detection pass produced for one symbol.</summary>
public sealed record AlertDetection(
    IReadOnlyList<DetectedAlert> Fired,
    SymbolMonitorState NextState);

/// <summary>A persisted alert, as returned to the UI and the SSE stream.</summary>
public sealed record AlertRecord
{
    public required string AlertId { get; init; }
    public required string Symbol { get; init; }
    public required string Kind { get; init; }
    public required string Severity { get; init; }
    public decimal? LevelPrice { get; init; }
    public required decimal Price { get; init; }
    public required string Interval { get; init; }
    public required string Summary { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public bool WeeklyConfirmed { get; init; }
    public bool FromLiveBar { get; init; }

    /// <summary><c>new</c> | <c>acknowledged</c> | <c>dismissed</c>.</summary>
    public required string State { get; init; }

    public required DateTime RaisedUtc { get; init; }
    public required string SessionDate { get; init; }
}
