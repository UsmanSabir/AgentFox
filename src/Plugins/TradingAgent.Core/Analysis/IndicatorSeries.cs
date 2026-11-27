using TradingAgent.Research;

namespace TradingAgent.Analysis;

/// <summary>
/// Full-length indicator series for plotting, aligned bar-for-bar with the input.
///
/// <para>
/// <see cref="TechnicalAnalyzer"/> computes only the LATEST value of each indicator, because that is
/// all a setup classification needs. A chart needs the whole line. These functions therefore repeat
/// the same formulas — Wilder's smoothing for RSI, a simple mean for SMA — and
/// <c>IndicatorSeriesTests</c> asserts that the last element of each series equals the corresponding
/// snapshot value. If the two ever disagree the chart would draw one story while the snapshot text
/// tells another, which is worse than having no chart.
/// </para>
///
/// <para>
/// The chart's RSI is the one exception, and on purpose: <c>/candles</c> computes it with <see cref="RsiWithHistory"/> over the
/// interval's whole archived series, because Wilder's smoothing remembers where it started and the analysis window (60 sessions)
/// leaves it about a point off a value computed with history, and over TRADED bars only (<see cref="RsiTraded"/>), as a rule order
/// counts sessions. The response's <c>snapshot.rsi14</c> is the line's last value, so the chart shows one number.
/// </para>
///
/// <para>
/// Entries are null until enough bars exist to compute the value, so index <c>i</c> of a returned
/// series always belongs to bar <c>i</c> and a caller can zip them without offset arithmetic.
/// </para>
/// </summary>
public static class IndicatorSeries
{
    /// <summary>Simple moving average, one pass with a running sum.</summary>
    public static decimal?[] Sma(IReadOnlyList<decimal> closes, int period)
    {
        var result = new decimal?[closes.Count];
        if (period <= 0) return result;

        decimal sum = 0;
        for (var i = 0; i < closes.Count; i++)
        {
            sum += closes[i];
            if (i >= period) sum -= closes[i - period];
            if (i >= period - 1) result[i] = Round(sum / period);
        }
        return result;
    }

    /// <summary>
    /// Wilder's RSI. The first value lands at index <paramref name="period"/> (it needs
    /// <c>period + 1</c> closes to have <c>period</c> deltas), matching
    /// <see cref="TechnicalAnalyzer"/>'s scalar implementation.
    /// </summary>
    public static decimal?[] Rsi(IReadOnlyList<decimal> closes, int period)
    {
        var result = new decimal?[closes.Count];
        if (period <= 0 || closes.Count <= period) return result;

        decimal gain = 0, loss = 0;
        for (var i = 1; i <= period; i++)
        {
            var delta = closes[i] - closes[i - 1];
            if (delta >= 0) gain += delta; else loss -= delta;
        }

        var avgGain = gain / period;
        var avgLoss = loss / period;
        result[period] = Rsi(avgGain, avgLoss);

        for (var i = period + 1; i < closes.Count; i++)
        {
            var delta = closes[i] - closes[i - 1];
            var up = delta > 0 ? delta : 0m;
            var dn = delta < 0 ? -delta : 0m;
            avgGain = (avgGain * (period - 1) + up) / period;
            avgLoss = (avgLoss * (period - 1) + dn) / period;
            result[i] = Rsi(avgGain, avgLoss);
        }

        return result;
    }

    /// <summary>
    /// Wilder's RSI over the TRADED bars of <paramref name="history"/>, returning only its last <paramref name="shown"/> values: the
    /// bars a chart shows, warmed up by everything before them (<see cref="RsiTraded"/>).
    /// </summary>
    public static decimal?[] RsiWithHistory(IReadOnlyList<PsxCandle> history, int shown, int period)
    {
        var full = RsiTraded(history, period);
        var count = Math.Clamp(shown, 0, full.Length);
        return full[(full.Length - count)..];
    }

    /// <summary>
    /// Wilder's RSI over the bars that TRADED, aligned to <paramref name="bars"/>: a bar with no volume is not a session, so it takes
    /// no step and repeats the value before it (null before the first). PSX publishes no candle for a day a stock did not trade, so
    /// this only matters for a source that carries one forward (a broker's series, a live bar before the first trade), and it is how
    /// premium's rule orders count sessions, so the chart, the alerts and a rule show one value. A series in which NO bar reports
    /// volume has no volume information at all (a feed that dropped the column reads 0 everywhere), so every bar is used instead
    /// of none.
    /// </summary>
    public static decimal?[] RsiTraded(IReadOnlyList<PsxCandle> bars, int period)
    {
        var result = new decimal?[bars.Count];
        var reportsVolume = bars.Any(b => b.Volume > 0);
        var traded = new List<int>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
            if (!reportsVolume || bars[i].Volume > 0) traded.Add(i);

        var rsi = Rsi(traded.Select(i => bars[i].Close).ToList(), period);
        decimal? last = null;
        var next = 0;
        for (var i = 0; i < bars.Count; i++)
        {
            if (next < traded.Count && traded[next] == i) last = rsi[next++];
            result[i] = last;
        }
        return result;
    }

    /// <summary>Closes of the bars that traded, by <see cref="RsiTraded"/>'s rule: every close when no bar reports volume.</summary>
    public static List<decimal> TradedCloses(IReadOnlyList<PsxCandle> bars) =>
        bars.Any(b => b.Volume > 0) ? bars.Where(b => b.Volume > 0).Select(b => b.Close).ToList() : Closes(bars);

    /// <summary>Closes of <paramref name="bars"/>, in the order given.</summary>
    public static List<decimal> Closes(IEnumerable<PsxCandle> bars) =>
        bars.Select(b => b.Close).ToList();

    private static decimal Rsi(decimal avgGain, decimal avgLoss) =>
        // A window with no losses is RSI 100 by definition; dividing by zero is the alternative.
        avgLoss == 0 ? 100m : Round(100m - 100m / (1m + avgGain / avgLoss));

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
