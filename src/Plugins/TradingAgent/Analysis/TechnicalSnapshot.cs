using TradingAgent.Config;

namespace TradingAgent.Analysis;

/// <summary>Where the last price sits inside its recent trading range.</summary>
public enum PriceZone
{
    Unknown,
    AtSupport,
    LowerRange,
    MidRange,
    UpperRange,
    AtResistance
}

/// <summary>
/// The deterministic read on what the candles support doing. This is a setup classification, not
/// advice: execution still goes through the risk engine and the configured execution mode.
/// </summary>
public enum TradeSetup
{
    /// <summary>Not enough candle history to classify.</summary>
    InsufficientData,

    /// <summary>Price is testing support and is not breaking down — the buy-low case.</summary>
    BuyAtSupport,

    /// <summary>Price is pressing resistance — the sell-high / take-profit case.</summary>
    SellAtResistance,

    /// <summary>Mid-range or unclear: no level to trade against.</summary>
    Wait,

    /// <summary>
    /// Price is at the bottom of its range because it is still falling — support has given way.
    /// Cheap is not the same as supported, and this is the case that must never be reported as a buy.
    /// </summary>
    AvoidBreakdown
}

/// <summary>
/// One horizontal price level derived from the candles. <see cref="Touches"/> counts how many
/// pivots merged into it: a level tested repeatedly is stronger than one drawn from a single bar.
/// </summary>
public sealed record PriceLevel(decimal Price, int Touches, string Origin);

/// <summary>
/// Deterministic technical read for one symbol over a candle window. Every number here is computed
/// from the supplied candles — nothing is modelled, estimated, or inferred by an LLM, which is what
/// lets the specialist quote these figures without breaking its "never invent a price" rule.
/// Nullable fields mean the window was too short to compute the measure.
/// </summary>
public sealed record TechnicalSnapshot
{
    public string Symbol { get; init; } = "";

    /// <summary>Trading date of the last bar analyzed.</summary>
    public DateOnly AsOf { get; init; }

    /// <summary>True when the last bar is the live forming session rather than a settled close.</summary>
    public bool UsesLiveBar { get; init; }

    public int Bars { get; init; }

    public decimal Close { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public long Volume { get; init; }
    public decimal? PreviousClose { get; init; }
    public decimal? DayChangePercent { get; init; }

    public decimal? Sma20 { get; init; }
    public decimal? Sma50 { get; init; }
    public string? Trend { get; init; }
    public decimal? Rsi14 { get; init; }
    public decimal? Atr14 { get; init; }
    public decimal? AtrPercent { get; init; }

    /// <summary>Mean volume over the last 30 sessions (name kept plain so it serializes readably).</summary>
    public long? AverageVolume { get; init; }

    /// <summary>Last session's volume as a multiple of <see cref="AverageVolume"/>.</summary>
    public decimal? VolumeRatio { get; init; }

    public decimal RangeHigh { get; init; }
    public decimal RangeLow { get; init; }

    /// <summary>0 = sitting on the range low, 1 = at the range high. Null for a flat range.</summary>
    public decimal? RangePosition { get; init; }

    public decimal? NearestSupport { get; init; }
    public decimal? PercentAboveSupport { get; init; }
    public decimal? NearestResistance { get; init; }
    public decimal? PercentBelowResistance { get; init; }

    public IReadOnlyList<PriceLevel> Supports { get; init; } = [];
    public IReadOnlyList<PriceLevel> Resistances { get; init; } = [];

    public int ConsecutiveDownDays { get; init; }
    public int ConsecutiveUpDays { get; init; }
    public bool MakesNewRangeLow { get; init; }
    public bool MakesNewRangeHigh { get; init; }

    public PriceZone Zone { get; init; }
    public TradeSetup Setup { get; init; }

    /// <summary>Plain-language evidence for the zone/setup call, each citing its own numbers.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>Level-anchored trade math. Null when no tradable level was found on the relevant side.</summary>
    public decimal? SuggestedEntry { get; init; }
    public decimal? SuggestedStop { get; init; }
    public decimal? SuggestedTarget { get; init; }
    public decimal? RewardRiskRatio { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Thresholds for <see cref="TechnicalAnalyzer"/>, normally projected from configuration.</summary>
public sealed record TechnicalOptions
{
    public int RangeWindow { get; init; } = 20;
    public int PivotWindow { get; init; } = 3;
    public decimal LevelClusterPercent { get; init; } = 1.5m;
    public decimal SupportProximityPercent { get; init; } = 2.5m;
    public decimal ResistanceProximityPercent { get; init; } = 2.5m;
    public decimal StopAtrMultiple { get; init; } = 1.0m;
    public decimal RsiOversold { get; init; } = 35m;
    public decimal RsiOverbought { get; init; } = 70m;
    public int BreakdownDownDays { get; init; } = 3;
    public int RsiPeriod { get; init; } = 14;
    public int AtrPeriod { get; init; } = 14;

    public static TechnicalOptions From(TradingScanOptions scan) => new()
    {
        RangeWindow                = Math.Clamp(scan.RangeWindow, 5, 120),
        PivotWindow                = Math.Clamp(scan.PivotWindow, 1, 10),
        LevelClusterPercent        = Math.Clamp(scan.LevelClusterPercent, 0.1m, 10m),
        SupportProximityPercent    = Math.Clamp(scan.SupportProximityPercent, 0.1m, 25m),
        ResistanceProximityPercent = Math.Clamp(scan.ResistanceProximityPercent, 0.1m, 25m),
        StopAtrMultiple            = Math.Clamp(scan.StopAtrMultiple, 0.1m, 5m),
        RsiOversold                = Math.Clamp(scan.RsiOversold, 1m, 50m),
        RsiOverbought              = Math.Clamp(scan.RsiOverbought, 50m, 99m),
        BreakdownDownDays          = Math.Clamp(scan.BreakdownDownDays, 1, 10)
    };
}
