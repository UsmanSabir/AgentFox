using TradingAgent.Research;

namespace TradingAgent.Analysis;

/// <summary>
/// Turns a daily candle series into support/resistance levels, standard indicators, and a
/// buy-at-support / sell-at-resistance classification.
///
/// Deliberately pure and deterministic: same candles in, same verdict out, no network and no model
/// call. That matters for two reasons — the specialist agent may quote these numbers without
/// violating its "never invent a price, quantity, or target" rule, and the classification can be
/// unit-tested against hand-built series instead of hoping a prompt behaves.
///
/// The load-bearing distinction is between price being LOW and price being SUPPORTED. A stock
/// printing fresh range lows on consecutive red sessions is classified
/// <see cref="TradeSetup.AvoidBreakdown"/>, never <see cref="TradeSetup.BuyAtSupport"/>: buying it
/// is catching a falling knife, which is exactly the trade a naive "price near the low" screen
/// would hand you.
/// </summary>
public static class TechnicalAnalyzer
{
    /// <summary>Bars needed before levels and indicators are meaningful enough to classify.</summary>
    public const int MinimumBars = 12;

    /// <summary>
    /// Analyzes <paramref name="candles"/> (OLDEST first; the last bar may be the live forming one).
    /// The 52-week extremes are optional extra levels — they come from the portal's long EOD series,
    /// which reaches further back than any practical candle window.
    /// </summary>
    public static TechnicalSnapshot Analyze(
        string symbol,
        IReadOnlyList<PsxCandle> candles,
        TechnicalOptions options,
        decimal? high52Week = null,
        decimal? low52Week = null)
    {
        symbol = symbol.Trim().ToUpperInvariant();

        if (candles is null || candles.Count == 0)
            return new TechnicalSnapshot
            {
                Symbol   = symbol,
                Setup    = TradeSetup.InsufficientData,
                Zone     = PriceZone.Unknown,
                Warnings = ["No candles were available for this symbol."]
            };

        // SortKeyUtc, not Date: an intraday series has many bars per session date.
        var bars = candles.OrderBy(c => c.SortKeyUtc).ToList();
        var last = bars[^1];
        var warnings = new List<string>();
        var reasons = new List<string>();

        // Every measure below is in BARS, so the prose has to follow the series: on a 15m series
        // "3 consecutive down days" would be a plain lie about what was measured, and so would a
        // "20-day range" on a weekly one.
        var intraday = last.IsIntraday;
        var unit = CandleInterval.Unit(last.IntervalMinutes);
        var period = CandleInterval.Period(last.IntervalMinutes);

        var closes = bars.Select(b => b.Close).ToList();
        var rangeWindow = Math.Min(options.RangeWindow, bars.Count);
        var window = bars.Skip(bars.Count - rangeWindow).ToList();
        var rangeHigh = window.Max(b => b.High);
        var rangeLow  = window.Min(b => b.Low);

        var atr = Atr(bars, options.AtrPeriod);
        var rsi = Rsi(closes, options.RsiPeriod);
        var sma20 = Sma(closes, 20);
        var sma50 = Sma(closes, 50);
        var avgVolume = AverageVolume(bars, 30);

        // Prior window excludes the current bar, so "new range low" means the CURRENT bar broke
        // the level rather than merely being part of the window that defines it.
        var prior = bars.Take(bars.Count - 1).TakeLast(Math.Max(1, rangeWindow - 1)).ToList();
        var priorLow  = prior.Count > 0 ? prior.Min(b => b.Low)  : (decimal?)null;
        var priorHigh = prior.Count > 0 ? prior.Max(b => b.High) : (decimal?)null;
        var makesNewLow  = priorLow  is { } pl && last.Close <= pl;
        var makesNewHigh = priorHigh is { } ph && last.Close >= ph;

        var (down, up) = ConsecutiveRuns(bars);

        // ── Levels ────────────────────────────────────────────────────────────
        var raw = new List<(decimal Price, string Origin)>();
        foreach (var pivot in SwingPivots(bars, options.PivotWindow))
            raw.Add(pivot);
        raw.Add((rangeLow, $"{rangeWindow}-{unit} low"));
        raw.Add((rangeHigh, $"{rangeWindow}-{unit} high"));
        if (low52Week is > 0) raw.Add((low52Week.Value, "52-week low"));
        if (high52Week is > 0) raw.Add((high52Week.Value, "52-week high"));

        var levels = Cluster(raw, options.LevelClusterPercent);

        // A level is support or resistance by where it sits relative to price NOW — which is why a
        // broken support correctly reappears as overhead resistance after a breakdown.
        var supports = levels.Where(l => l.Price <= last.Close)
            .OrderByDescending(l => l.Price).ToList();
        var resistances = levels.Where(l => l.Price > last.Close)
            .OrderBy(l => l.Price).ToList();

        var nearestSupport = supports.FirstOrDefault();
        var nearestResistance = resistances.FirstOrDefault();

        decimal? pctAboveSupport = nearestSupport is not null && nearestSupport.Price > 0
            ? Round((last.Close - nearestSupport.Price) / nearestSupport.Price * 100m)
            : null;
        decimal? pctBelowResistance = nearestResistance is not null && last.Close > 0
            ? Round((nearestResistance.Price - last.Close) / last.Close * 100m)
            : null;

        decimal? rangePosition = rangeHigh > rangeLow
            ? Round((last.Close - rangeLow) / (rangeHigh - rangeLow), 4)
            : null;

        // ── Zone ──────────────────────────────────────────────────────────────
        var atSupport = pctAboveSupport is { } ps
            && ps <= options.SupportProximityPercent
            && rangePosition is null or <= 0.40m;
        var atResistance = pctBelowResistance is { } pr
            && pr <= options.ResistanceProximityPercent
            && rangePosition is null or >= 0.60m;

        // In a tight range both can qualify; the closer level wins.
        if (atSupport && atResistance)
        {
            if (pctAboveSupport <= pctBelowResistance) atResistance = false;
            else atSupport = false;
        }

        var zone = atSupport ? PriceZone.AtSupport
            : atResistance ? PriceZone.AtResistance
            : rangePosition switch
            {
                null => PriceZone.Unknown,
                <= 0.25m => PriceZone.LowerRange,
                >= 0.75m => PriceZone.UpperRange,
                _ => PriceZone.MidRange
            };

        // ── Breakdown guard ───────────────────────────────────────────────────
        var brokeByAtr = makesNewLow && atr is > 0 && priorLow is { } pLow && pLow - last.Close >= atr;
        var breakdown = makesNewLow && (down >= options.BreakdownDownDays || brokeByAtr);

        // ── Setup ─────────────────────────────────────────────────────────────
        TradeSetup setup;
        if (bars.Count < MinimumBars)
        {
            setup = TradeSetup.InsufficientData;
            warnings.Add($"Only {bars.Count} {period}s of candle history were available; " +
                         $"{MinimumBars} are needed to classify a setup.");
        }
        else if (breakdown)
        {
            setup = TradeSetup.AvoidBreakdown;
        }
        else
        {
            setup = zone switch
            {
                PriceZone.AtSupport    => TradeSetup.BuyAtSupport,
                PriceZone.AtResistance => TradeSetup.SellAtResistance,
                _                      => TradeSetup.Wait
            };
        }

        // ── Level-anchored trade math (buy-side semantics) ─────────────────────
        decimal? entry = null, stop = null, target = null, rewardRisk = null;
        if (nearestSupport is not null)
        {
            // Rest the buy at the level when price is still above it; use the last price once price
            // has already reached or passed it.
            entry = Round(Math.Min(last.Close, nearestSupport.Price));

            if (atr is > 0)
                stop = Round(Math.Min(entry.Value, nearestSupport.Price) - options.StopAtrMultiple * atr.Value);

            target = nearestResistance is not null ? Round(nearestResistance.Price) : null;

            if (entry > 0 && stop is > 0 && target is not null && entry > stop && target > entry)
                rewardRisk = Round((target.Value - entry.Value) / (entry.Value - stop.Value));
        }

        // ── Reasons ───────────────────────────────────────────────────────────
        if (nearestSupport is not null && pctAboveSupport is not null)
            reasons.Add($"Last {Round(last.Close)} is {pctAboveSupport}% above nearest support " +
                        $"{Round(nearestSupport.Price)} ({nearestSupport.Touches} touch(es), {nearestSupport.Origin}).");
        else
            reasons.Add($"No support level below {Round(last.Close)} in the analyzed window.");

        if (nearestResistance is not null && pctBelowResistance is not null)
            reasons.Add($"Nearest resistance {Round(nearestResistance.Price)} is {pctBelowResistance}% above " +
                        $"the last price ({nearestResistance.Touches} touch(es), {nearestResistance.Origin}).");

        if (rangePosition is not null)
            reasons.Add($"Range position {Round(rangePosition.Value * 100m)}% of the {rangeWindow}-{unit} " +
                        $"{Round(rangeLow)}–{Round(rangeHigh)} range.");

        if (rsi is not null)
        {
            var label = rsi <= options.RsiOversold ? " (oversold)"
                : rsi >= options.RsiOverbought ? " (overbought)"
                : "";
            reasons.Add($"RSI(14) {rsi}{label}.");
        }

        if (atr is not null)
            reasons.Add($"ATR(14) {atr} ({AtrPercent(atr, last.Close)}% of price).");

        decimal? volumeRatio = avgVolume is > 0 ? Round((decimal)last.Volume / avgVolume.Value) : null;
        if (volumeRatio is not null)
            reasons.Add($"Volume {last.Volume:N0} is {volumeRatio}× the 30-{unit} average {avgVolume:N0}.");

        if (down > 0) reasons.Add($"{down} consecutive down {period}(s).");
        else if (up > 0) reasons.Add($"{up} consecutive up {period}(s).");

        // Gated on the verdict, not on the raw condition: with too little history the setup is
        // InsufficientData, and a reasons list still shouting BREAKDOWN would contradict it.
        if (setup == TradeSetup.AvoidBreakdown)
            reasons.Add($"BREAKDOWN: a fresh {rangeWindow}-{unit} low with " +
                        (brokeByAtr
                            ? $"the close more than one ATR below the prior low {Round(priorLow!.Value)}"
                            : $"{down} consecutive down {period}(s)") +
                        " — price is falling through support, not testing it.");

        if (rewardRisk is not null)
            reasons.Add($"Reward:risk {rewardRisk}:1 for entry {entry} / stop {stop} / target {target}.");

        // Spell the sell level out. The trade math above is buy-side (entry at support), so on a sell
        // setup the actionable number is the resistance itself, and leaving the reader to infer that
        // from an "entry" figure below the current price is how a take-profit gets misread as a buy.
        if (setup == TradeSetup.SellAtResistance && nearestResistance is not null)
            reasons.Add($"SELL setup: sell into resistance at {Round(nearestResistance.Price)} " +
                        $"(price is {pctBelowResistance}% below it), do not open a new long here.");

        if (last.IsLive)
            reasons.Add(
                $"Last bar is the forming {CandleInterval.Label(last.IntervalMinutes)} " +
                $"{period}, not a closed one.");

        return new TechnicalSnapshot
        {
            Symbol                 = symbol,
            AsOf                   = last.Date,
            AsOfUtc                = last.BucketStartUtc,
            Interval               = CandleInterval.Label(last.IntervalMinutes),
            UsesLiveBar            = last.IsLive,
            Bars                   = bars.Count,
            Close                  = Round(last.Close),
            Open                   = Round(last.Open),
            High                   = Round(last.High),
            Low                    = Round(last.Low),
            Volume                 = last.Volume,
            PreviousClose          = last.PreviousClose is not null ? Round(last.PreviousClose.Value) : null,
            DayChangePercent       = last.PreviousClose is > 0
                ? Round((last.Close - last.PreviousClose.Value) / last.PreviousClose.Value * 100m)
                : null,
            Sma20                  = sma20,
            Sma50                  = sma50,
            Trend                  = TrendLabel(last.Close, sma20, sma50),
            Rsi14                  = rsi,
            Atr14                  = atr,
            AtrPercent             = AtrPercent(atr, last.Close),
            AverageVolume          = avgVolume,
            VolumeRatio            = volumeRatio,
            RangeHigh              = Round(rangeHigh),
            RangeLow               = Round(rangeLow),
            RangePosition          = rangePosition,
            NearestSupport         = nearestSupport is not null ? Round(nearestSupport.Price) : null,
            PercentAboveSupport    = pctAboveSupport,
            NearestResistance      = nearestResistance is not null ? Round(nearestResistance.Price) : null,
            PercentBelowResistance = pctBelowResistance,
            Supports               = supports.Take(4).Select(RoundLevel).ToList(),
            Resistances            = resistances.Take(4).Select(RoundLevel).ToList(),
            ConsecutiveDownDays    = down,
            ConsecutiveUpDays      = up,
            MakesNewRangeLow       = makesNewLow,
            MakesNewRangeHigh      = makesNewHigh,
            Zone                   = zone,
            Setup                  = setup,
            Reasons                = reasons,
            SuggestedEntry         = entry,
            SuggestedStop          = stop,
            SuggestedTarget        = target,
            RewardRiskRatio        = rewardRisk,
            Warnings               = warnings
        };
    }

    // ── Levels ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Swing pivots: a bar whose high is the highest (or low the lowest) within
    /// <paramref name="width"/> bars either side. The last <paramref name="width"/> bars are skipped
    /// because a pivot is not confirmed until enough bars have printed after it.
    /// </summary>
    private static IEnumerable<(decimal Price, string Origin)> SwingPivots(
        IReadOnlyList<PsxCandle> bars, int width)
    {
        for (var i = width; i < bars.Count - width; i++)
        {
            var isHigh = true;
            var isLow = true;
            for (var j = i - width; j <= i + width && (isHigh || isLow); j++)
            {
                if (j == i) continue;
                if (bars[j].High > bars[i].High) isHigh = false;
                if (bars[j].Low < bars[i].Low) isLow = false;
            }

            if (isHigh) yield return (bars[i].High, "swing high");
            if (isLow) yield return (bars[i].Low, "swing low");
        }
    }

    /// <summary>
    /// Merges nearby levels into one, carrying the touch count. Two pivots a fraction of a percent
    /// apart are the same level to a trader, and reporting them separately would both clutter the
    /// output and understate how many times the level has actually held.
    /// </summary>
    private static List<PriceLevel> Cluster(
        List<(decimal Price, string Origin)> raw, decimal tolerancePercent)
    {
        var clusters = new List<PriceLevel>();
        if (raw.Count == 0) return clusters;

        var sorted = raw.Where(r => r.Price > 0).OrderBy(r => r.Price).ToList();
        var groupPrices = new List<decimal>();
        var groupOrigins = new List<string>();

        void Flush()
        {
            if (groupPrices.Count == 0) return;
            clusters.Add(new PriceLevel(
                groupPrices.Average(),
                groupPrices.Count,
                string.Join(", ", groupOrigins.Distinct())));
            groupPrices.Clear();
            groupOrigins.Clear();
        }

        foreach (var (price, origin) in sorted)
        {
            if (groupPrices.Count > 0
                && (price - groupPrices[0]) / groupPrices[0] * 100m > tolerancePercent)
            {
                Flush();
            }

            groupPrices.Add(price);
            groupOrigins.Add(origin);
        }

        Flush();
        return clusters;
    }

    private static PriceLevel RoundLevel(PriceLevel level) =>
        level with { Price = Round(level.Price) };

    // ── Indicators ────────────────────────────────────────────────────────────

    private static decimal? Sma(IReadOnlyList<decimal> closes, int period) =>
        closes.Count < period ? null : Round(closes.TakeLast(period).Average());

    /// <summary>Wilder's RSI. Needs <paramref name="period"/> + 1 closes.</summary>
    private static decimal? Rsi(IReadOnlyList<decimal> closes, int period)
    {
        if (closes.Count <= period) return null;

        decimal gain = 0, loss = 0;
        for (var i = 1; i <= period; i++)
        {
            var delta = closes[i] - closes[i - 1];
            if (delta >= 0) gain += delta; else loss -= delta;
        }

        var avgGain = gain / period;
        var avgLoss = loss / period;

        for (var i = period + 1; i < closes.Count; i++)
        {
            var delta = closes[i] - closes[i - 1];
            var up = delta > 0 ? delta : 0m;
            var dn = delta < 0 ? -delta : 0m;
            avgGain = (avgGain * (period - 1) + up) / period;
            avgLoss = (avgLoss * (period - 1) + dn) / period;
        }

        if (avgLoss == 0) return avgGain == 0 ? 50m : 100m;
        var rs = avgGain / avgLoss;
        return Round(100m - 100m / (1m + rs));
    }

    /// <summary>Wilder's ATR over true ranges. Needs <paramref name="period"/> + 1 bars.</summary>
    private static decimal? Atr(IReadOnlyList<PsxCandle> bars, int period)
    {
        if (bars.Count <= period) return null;

        var trueRanges = new List<decimal>(bars.Count - 1);
        for (var i = 1; i < bars.Count; i++)
        {
            var prevClose = bars[i - 1].Close;
            trueRanges.Add(Math.Max(
                bars[i].High - bars[i].Low,
                Math.Max(Math.Abs(bars[i].High - prevClose), Math.Abs(bars[i].Low - prevClose))));
        }

        if (trueRanges.Count < period) return null;

        var atr = trueRanges.Take(period).Average();
        for (var i = period; i < trueRanges.Count; i++)
            atr = (atr * (period - 1) + trueRanges[i]) / period;

        return Round(atr);
    }

    private static long? AverageVolume(IReadOnlyList<PsxCandle> bars, int period)
    {
        var window = bars.TakeLast(period).Where(b => b.Volume > 0).ToList();
        return window.Count == 0 ? null : (long)window.Average(b => b.Volume);
    }

    private static decimal? AtrPercent(decimal? atr, decimal close) =>
        atr is > 0 && close > 0 ? Round(atr.Value / close * 100m) : null;

    private static string? TrendLabel(decimal close, decimal? sma20, decimal? sma50)
    {
        if (sma20 is null) return null;
        if (sma50 is null)
            return close >= sma20 ? "above SMA20" : "below SMA20";

        return (close >= sma20, sma20 >= sma50) switch
        {
            (true, true)   => "uptrend (price > SMA20 > SMA50)",
            (true, false)  => "recovering (price > SMA20, SMA20 < SMA50)",
            (false, true)  => "pulling back (price < SMA20, SMA20 > SMA50)",
            (false, false) => "downtrend (price < SMA20 < SMA50)"
        };
    }

    /// <summary>Length of the current run of down or up closes, counting back from the last bar.</summary>
    private static (int Down, int Up) ConsecutiveRuns(IReadOnlyList<PsxCandle> bars)
    {
        int down = 0, up = 0;
        for (var i = bars.Count - 1; i > 0; i--)
        {
            if (bars[i].Close < bars[i - 1].Close && up == 0) down++;
            else if (bars[i].Close > bars[i - 1].Close && down == 0) up++;
            else break;
        }
        return (down, up);
    }

    private static decimal Round(decimal value, int digits = 2) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero);
}
