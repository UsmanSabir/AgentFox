namespace TradingAgent.Chart;

/// <summary>
/// Extra marks an edition wants drawn on the chart the dashboard already renders — projections,
/// predicted points, a next target, a confidence band.
///
/// <para>
/// This exists so a premium edition can put features on the EXISTING chart in the EXISTING dashboard
/// rather than shipping a second page. The mechanism is deliberately data, not markup: the public
/// ChartPane already draws every primitive these features need (horizontal price lines, line series,
/// per-bar markers), and it draws them from the <c>/trading/candles</c> response. An overlay set is
/// merged into that response, so an edition supplies values and the shared renderer draws them.
/// </para>
///
/// <para>
/// EVERYTHING HERE IS PUBLIC. It is serialized to the browser, where it is readable in the network
/// tab — a weaker boundary than a shipped assembly, which at least has to be decompiled. Emit the
/// CONCLUSION (a line, a target, a band) and never the features, weights, thresholds, scores, or
/// model identity behind it. A premium projection is defensible because reproducing it requires the
/// model, not because the drawn line is hidden.
/// </para>
///
/// <para>
/// Overlays are PRESENTATION ONLY and must never become an execution input. Position sizing, the
/// intent builder, and the risk engine read the server-side model directly; nothing may size or
/// trigger a trade from a value that made a round trip through the chart response.
/// </para>
/// </summary>
public sealed record ChartOverlaySet(
    IReadOnlyList<ChartOverlayLevel> Levels,
    IReadOnlyList<ChartOverlaySeries> Series,
    IReadOnlyList<ChartOverlayMarker> Markers,
    IReadOnlyList<ChartOverlayBand> Bands)
{
    public static ChartOverlaySet Empty { get; } = new([], [], [], []);

    public bool IsEmpty => Levels.Count == 0 && Series.Count == 0
                        && Markers.Count == 0 && Bands.Count == 0;

    /// <summary>Combines what several providers contributed, preserving provider order.</summary>
    public static ChartOverlaySet Merge(IEnumerable<ChartOverlaySet> sets)
    {
        var levels = new List<ChartOverlayLevel>();
        var series = new List<ChartOverlaySeries>();
        var markers = new List<ChartOverlayMarker>();
        var bands = new List<ChartOverlayBand>();
        foreach (var set in sets)
        {
            levels.AddRange(set.Levels);
            series.AddRange(set.Series);
            markers.AddRange(set.Markers);
            bands.AddRange(set.Bands);
        }
        return new ChartOverlaySet(levels, series, markers, bands);
    }
}

/// <summary>A horizontal price line — a projected target, a predicted level.</summary>
/// <param name="Kind">See <see cref="ChartOverlayKind"/>. A semantic token, never a color.</param>
/// <param name="Weight">Relative emphasis, 1-3; the client maps it to line width.</param>
public sealed record ChartOverlayLevel(
    string Id,
    string Label,
    decimal Price,
    string Kind,
    int Weight = 1,
    bool Confirmed = false);

/// <summary>
/// A line across time. <see cref="ChartOverlayPoint.Time"/> may extend PAST the last candle — that
/// is what makes a projection a projection — but those timestamps must come from
/// <see cref="ChartOverlayRequest.NextSessionTimes"/> rather than be computed by the provider.
/// </summary>
public sealed record ChartOverlaySeries(
    string Id,
    string Label,
    string Kind,
    bool Dashed,
    IReadOnlyList<ChartOverlayPoint> Points);

/// <param name="Time">Seconds since the epoch, matching the candle series.</param>
public sealed record ChartOverlayPoint(long Time, decimal Value);

/// <param name="Position">"aboveBar" or "belowBar".</param>
/// <param name="Shape">
/// <see cref="ChartOverlayShape"/>; null draws the default circle. A shape, like a kind, is a token the
/// client maps — never a drawing instruction.
/// </param>
/// <param name="Detail">
/// One plain-text line the chart shows while the pointer is on this marker's bar, e.g. "Ex-dividend
/// 8 Oct 2026: Rs 6.00 a share". Null shows nothing. Public like everything else here.
/// </param>
public sealed record ChartOverlayMarker(
    string Id,
    long Time,
    string Text,
    string Kind,
    string Position = "aboveBar",
    decimal? Value = null,
    string? Shape = null,
    string? Detail = null);

/// <summary>The marker shapes the client can draw. An unrecognised one is drawn as a circle.</summary>
public static class ChartOverlayShape
{
    public const string Circle = "circle";
    public const string Square = "square";
    public const string ArrowUp = "arrowUp";
    public const string ArrowDown = "arrowDown";
}

/// <summary>An upper/lower envelope — a confidence or probability band around a projection.</summary>
public sealed record ChartOverlayBand(
    string Id,
    string Label,
    string Kind,
    IReadOnlyList<ChartOverlayBandPoint> Points);

public sealed record ChartOverlayBandPoint(long Time, decimal Lower, decimal Upper);

/// <summary>
/// The semantic tokens an overlay may claim. The client maps each to a theme color exactly as the
/// core levels do, so overlays stay legible in both light and dark and an edition cannot take over
/// the dashboard's palette. An unrecognized kind renders in the neutral color rather than failing.
/// </summary>
public static class ChartOverlayKind
{
    public const string Projection = "projection";
    public const string Prediction = "prediction";
    public const string Target = "target";
    public const string Entry = "entry";
    public const string Stop = "stop";
    public const string Support = "support";
    public const string Resistance = "resistance";
    /// <summary>A corporate event on a bar: the day a stock went ex-dividend.</summary>
    public const string Dividend = "dividend";
    /// <summary>A corporate event on a bar: the day financial results were announced.</summary>
    public const string Earnings = "earnings";
    public const string Neutral = "neutral";
}

/// <summary>
/// What a provider is told about the chart being drawn.
/// </summary>
/// <param name="NextSessionTimes">
/// Timestamps for the next few TRADING sessions after the last candle, in the same units as the
/// candle series. Supplied by the core because a provider computing <c>lastBar + 86400</c> itself
/// would draw a target on a Saturday or on a configured market holiday. A provider that wants to
/// project forward must take its x-values from here.
/// </param>
/// <param name="LastClose">
/// The close of the last bar in the series being drawn. Supplied because a provider annotating a
/// chart almost always needs the price context of that chart, and re-deriving it from its own source
/// risks anchoring an overlay to a slightly different price than the candles show — which renders as
/// a projection that visibly does not meet the series it projects from.
/// </param>
/// <param name="BarTimes">
/// Every bar's time, oldest first, in the candle series' units. A weekly or monthly bar is stamped
/// with the START of its period, so a provider marking a dated event finds its bar here rather than
/// guessing which bar a date falls in. Null only from a caller that predates it.
/// </param>
public sealed record ChartOverlayRequest(
    string Symbol,
    string Interval,
    long FirstBarTime,
    long LastBarTime,
    int BarCount,
    decimal LastClose,
    IReadOnlyList<long> NextSessionTimes,
    IReadOnlyList<long>? BarTimes = null);

/// <summary>
/// Supplies overlays for one chart. Register an implementation in DI and the <c>/trading/candles</c>
/// handler will consult it; the community edition registers none, so the response carries an empty
/// overlay block and the chart is exactly what it was.
///
/// <para>
/// The chart is a READ path. An implementation that throws or runs long is dropped for that request
/// and the chart still renders — a slow model must never be able to stop a user seeing prices. Same
/// failure direction as the rest of the premium design: degraded premium never takes down core
/// function.
/// </para>
/// </summary>
public interface IChartOverlayProvider
{
    /// <summary>Stable identifier, used in logs when this provider is dropped.</summary>
    string Id { get; }

    Task<ChartOverlaySet?> GetOverlaysAsync(ChartOverlayRequest request, CancellationToken ct);
}
