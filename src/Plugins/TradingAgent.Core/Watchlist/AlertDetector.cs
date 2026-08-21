using TradingAgent.Analysis;

namespace TradingAgent.Watchlist;

/// <summary>
/// Decides what is worth telling a human about, given the previous pass's state and the current
/// deterministic snapshot.
///
/// <para>
/// Pure and side-effect free on purpose: same inputs, same alerts. Every threshold that could produce
/// a false positive is explicit, and the whole thing is table-testable against hand-built snapshots
/// rather than by watching a live market and hoping.
/// </para>
///
/// <para>Three guards do the real work, and none of them is optional polish:</para>
/// <list type="number">
///   <item><b>Transitions, not conditions.</b> "Price is at support" is true for days. "Price has
///   turned up off support" happens once. Only the second is an alert.</item>
///   <item><b>Confirmation streaks.</b> A sustained condition must hold for N consecutive passes
///   before it fires, so a price oscillating either side of a level does not fire on every pass.</item>
///   <item><b>A break buffer.</b> A close must clear a level by a margin to count as breaking it,
///   because a wick through a level is noise and reporting it as a break is how a monitor loses the
///   reader's trust.</item>
/// </list>
///
/// <para>
/// <b>Two kinds of signal, and they cannot share one confirmation rule.</b> A SUSTAINED condition
/// (price is bouncing off support, price is making fresh lows on volume) is a property of the current
/// snapshot and stays true across passes, so a streak can confirm it. An EDGE (the setup
/// classification changed, SMA20 crossed SMA50, RSI entered oversold) is visible for exactly one pass:
/// the state we compare against is rewritten at the end of every pass, so by the time a streak of two
/// matured the difference would be gone and the alert could never fire. Edges therefore fire
/// immediately, and their protection against flicker is the durable cooldown the worker applies — not
/// a streak that would silence them permanently.
/// </para>
///
/// <para>
/// Break detection reads levels from the CURRENT snapshot rather than remembering the old one, because
/// <see cref="TechnicalAnalyzer"/> already reclassifies a broken support as overhead resistance. The
/// level price to report is therefore the nearest resistance (for a breakdown) or the nearest support
/// (for a breakout) — stable across passes, where a remembered level would drift on the very next one.
/// </para>
///
/// <para>
/// Cooldown and de-duplication are deliberately NOT here — they need the alert history, which is
/// durable state the worker owns. This decides what happened; the worker decides whether it has
/// already been said.
/// </para>
/// </summary>
public static class AlertDetector
{
    public static AlertDetection Detect(
        SymbolMonitorState previous,
        TechnicalSnapshot snapshot,
        MultiTimeframeView? multi,
        MonitorThresholds thresholds)
    {
        var symbol = snapshot.Symbol;
        var candidates = new Dictionary<AlertKind, Candidate>();

        // ── Observations: what is true right now ──────────────────────────────
        var close = snapshot.Close;
        var support = snapshot.NearestSupport;
        var resistance = snapshot.NearestResistance;
        var volumeConfirmed = snapshot.VolumeRatio is null
            || snapshot.VolumeRatio >= thresholds.VolumeConfirmRatio;

        var smaRelation = snapshot.Sma20 is { } fast && snapshot.Sma50 is { } slow
            ? fast >= slow ? "above" : "below"
            : null;

        var rsiBand = snapshot.Rsi14 switch
        {
            null => null,
            var r when r <= thresholds.RsiOversold => "oversold",
            var r when r >= thresholds.RsiOverbought => "overbought",
            _ => "neutral"
        };

        // ── Sustained conditions: confirmed by a streak ───────────────────────
        // All are properties of the CURRENT snapshot, so they stay true while the situation lasts and
        // a streak can meaningfully confirm them.

        // Requiring an up run rather than mere proximity is what separates "cheap" from "supported" —
        // the same distinction TechnicalAnalyzer draws between BuyAtSupport and AvoidBreakdown.
        if (snapshot.Setup == TradeSetup.BuyAtSupport
            && snapshot.ConsecutiveUpDays >= 1
            && support is { } bounceLevel)
        {
            candidates[AlertKind.SupportBounce] = new Candidate(
                AlertSeverity.High,
                bounceLevel,
                $"{symbol} is turning up off support {bounceLevel} (last {close}, "
                + $"{snapshot.PercentAboveSupport}% above it) after {snapshot.ConsecutiveUpDays} up bar(s)"
                + (snapshot.Rsi14 is { } r1 ? $", RSI {r1}" : "") + ".");
        }

        if (snapshot.Setup == TradeSetup.SellAtResistance
            && snapshot.ConsecutiveDownDays >= 1
            && resistance is { } rejectLevel)
        {
            candidates[AlertKind.ResistanceRejection] = new Candidate(
                AlertSeverity.High,
                rejectLevel,
                $"{symbol} is turning down at resistance {rejectLevel} (last {close}, "
                + $"{snapshot.PercentBelowResistance}% below it) after "
                + $"{snapshot.ConsecutiveDownDays} down bar(s).");
        }

        // A break is price making a fresh range extreme while still moving that way, on volume. The
        // broken level is the one now on the OTHER side of price — the analyzer has already
        // reclassified it — and the buffer keeps a wick from counting as a break.
        if (snapshot.MakesNewRangeLow
            && snapshot.ConsecutiveDownDays >= 1
            && volumeConfirmed
            && ClearedBy(resistance, close, thresholds.BreakBufferPercent, below: true))
        {
            candidates[AlertKind.SupportBreak] = new Candidate(
                AlertSeverity.High,
                resistance,
                $"{symbol} broke down through {resistance} to {close} — a fresh "
                + $"{snapshot.Bars}-bar range low after {snapshot.ConsecutiveDownDays} down bar(s)"
                + (snapshot.VolumeRatio is { } v1 ? $" on {v1}× average volume" : "") + ".");
        }

        if (snapshot.MakesNewRangeHigh
            && snapshot.ConsecutiveUpDays >= 1
            && volumeConfirmed
            && ClearedBy(support, close, thresholds.BreakBufferPercent, below: false))
        {
            candidates[AlertKind.ResistanceBreakout] = new Candidate(
                AlertSeverity.High,
                support,
                $"{symbol} broke out above {support} to {close} — a fresh "
                + $"{snapshot.Bars}-bar range high after {snapshot.ConsecutiveUpDays} up bar(s)"
                + (snapshot.VolumeRatio is { } v2 ? $" on {v2}× average volume" : "") + ".");
        }

        // ── Edges: fire on the pass they are visible ──────────────────────────
        // Each is a change against state that gets rewritten below, so it exists for exactly one pass.
        // Streak-gating these would silence them forever, not merely delay them.
        var edges = new Dictionary<AlertKind, Candidate>();

        if (!previous.IsNew && previous.Setup != snapshot.Setup
            && snapshot.Setup != TradeSetup.InsufficientData)
        {
            edges[AlertKind.SetupChanged] = new Candidate(
                // A move INTO a breakdown is the one setup change that is genuinely urgent.
                snapshot.Setup == TradeSetup.AvoidBreakdown ? AlertSeverity.High : AlertSeverity.Medium,
                null,
                $"{symbol} setup changed from {previous.Setup} to {snapshot.Setup} (last {close}).");
        }

        if (!previous.IsNew && smaRelation is not null
            && previous.SmaRelation is not null && previous.SmaRelation != smaRelation)
        {
            edges[AlertKind.TrendFlip] = new Candidate(
                AlertSeverity.Medium,
                null,
                $"{symbol} SMA20 crossed {smaRelation} SMA50 "
                + $"(SMA20 {snapshot.Sma20}, SMA50 {snapshot.Sma50}, last {close}).");
        }

        if (!previous.IsNew && multi?.WeeklyBreakdown == true && !previous.WeeklyBreakdown)
        {
            edges[AlertKind.WeeklyBreakdown] = new Candidate(
                AlertSeverity.Critical,
                multi.Weekly?.NearestSupport,
                $"{symbol} has entered a WEEKLY breakdown: the weekly chart is making fresh lows while "
                + "still falling. A daily support test inside this is a falling knife.");
        }

        if (!previous.IsNew && rsiBand is "oversold" && previous.RsiBand is not null and not "oversold")
        {
            edges[AlertKind.RsiOversold] = new Candidate(
                AlertSeverity.Low, null,
                $"{symbol} RSI(14) fell to {snapshot.Rsi14}, at or below the {thresholds.RsiOversold} "
                + "oversold threshold.");
        }

        if (!previous.IsNew && rsiBand is "overbought" && previous.RsiBand is not null and not "overbought")
        {
            edges[AlertKind.RsiOverbought] = new Candidate(
                AlertSeverity.Low, null,
                $"{symbol} RSI(14) rose to {snapshot.Rsi14}, at or above the {thresholds.RsiOverbought} "
                + "overbought threshold.");
        }

        // ── Fire ──────────────────────────────────────────────────────────────
        var streaks = new Dictionary<AlertKind, int>();
        var fired = new List<DetectedAlert>();
        var weeklyConfirmedSupports = multi?.ConfirmedSupports ?? [];
        var weeklyConfirmedResistances = multi?.ConfirmedResistances ?? [];

        DetectedAlert Build(AlertKind kind, Candidate candidate)
        {
            var confirmed = candidate.Level is { } level && (
                weeklyConfirmedSupports.Any(c => Near(c.Price, level))
                || weeklyConfirmedResistances.Any(c => Near(c.Price, level)));

            return new DetectedAlert
            {
                Symbol          = symbol,
                Kind            = kind,
                Severity        = candidate.Severity,
                LevelPrice      = candidate.Level,
                Price           = close,
                Interval        = snapshot.Interval,
                Summary         = candidate.Summary,
                Reasons         = snapshot.Reasons,
                WeeklyConfirmed = confirmed,
                FromLiveBar     = snapshot.UsesLiveBar
            };
        }

        foreach (var (kind, candidate) in candidates)
        {
            var streak = previous.Streaks.GetValueOrDefault(kind) + 1;
            streaks[kind] = streak;

            // Fires exactly ON reaching the threshold, not above it: while a condition keeps holding,
            // the streak keeps climbing but the alert is not repeated. Re-arming happens when the
            // condition lapses and the streak resets to zero.
            if (streak == thresholds.ConfirmPasses) fired.Add(Build(kind, candidate));
        }

        foreach (var (kind, candidate) in edges)
            fired.Add(Build(kind, candidate));

        var next = new SymbolMonitorState
        {
            Symbol          = symbol,
            Zone            = snapshot.Zone,
            Setup           = snapshot.Setup,
            Support         = support,
            Resistance      = resistance,
            SmaRelation     = smaRelation,
            RsiBand         = rsiBand,
            WeeklyBreakdown = multi?.WeeklyBreakdown ?? previous.WeeklyBreakdown,
            // Only surviving candidates keep their streak; anything that lapsed resets to zero, which
            // is what re-arms it for next time.
            Streaks         = streaks,
            UpdatedUtc      = DateTime.UtcNow,
            IsNew           = false
        };

        return new AlertDetection(fired, next);
    }

    /// <summary>
    /// The first pass for a symbol records state without firing anything. Every kind here is a
    /// transition, and on a cold start there is nothing to have transitioned FROM — firing then would
    /// mean a restart alerts on every standing condition at once.
    /// </summary>
    public static SymbolMonitorState Seed(string symbol) => new()
    {
        Symbol = symbol,
        IsNew = true,
        UpdatedUtc = DateTime.UtcNow
    };

    /// <summary>
    /// True when <paramref name="close"/> has cleared <paramref name="level"/> by more than the
    /// buffer. A null level means there is nothing above (or below) price to have broken — which
    /// happens at an all-time extreme — and that is not a break of anything nameable.
    /// </summary>
    private static bool ClearedBy(decimal? level, decimal close, decimal bufferPercent, bool below) =>
        level is { } value
        && value > 0
        && (below
            ? close < value * (1m - bufferPercent / 100m)
            : close > value * (1m + bufferPercent / 100m));

    private static bool Near(decimal a, decimal b) =>
        b != 0 && Math.Abs(a - b) / Math.Abs(b) * 100m <= 2.0m;

    private sealed record Candidate(AlertSeverity Severity, decimal? Level, string Summary);
}
