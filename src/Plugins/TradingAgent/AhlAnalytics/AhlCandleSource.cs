using System.Globalization;
using Microsoft.Extensions.Logging;
using TradingAgent.Research;

namespace TradingAgent.AhlAnalytics;

/// <summary>
/// Serves daily candle history from the AHL analytics portal as <see cref="PsxCandle"/> values, so it
/// can stand in for the PSX portal path wherever deep history is needed.
///
/// <para>
/// <b>Why prefer it.</b> One request returns about five years of bars for a symbol. The PSX path
/// reaches the exchange one DATE at a time and parses HTML, so the same history costs roughly 1235
/// requests. Beyond cost, AHL's series is corporate-action ADJUSTED, and that is the correct input
/// for technical analysis: a raw series carries an artificial cliff on every split or bonus date, and
/// RSI, MACD, ATR and swing pivots computed across that cliff are meaningless. LUCK's 5:1 action shows
/// as a −80% single-day "move" in the raw series.
/// </para>
///
/// <para>
/// <b>Why it cannot simply replace PSX.</b> Adjusted prices are not the prices anything traded at.
/// LUCK's 2024-08-19 close is 166.29 here against 853.02 as traded, and the volume is scaled by the
/// same factor. So these bars must never reach reconciliation, realised P&amp;L, or any comparison
/// against a fill — PSX stays the source of record for those. Two consequences are enforced by
/// callers rather than here, and both matter:
/// </para>
/// <list type="bullet">
/// <item>An AHL series is never CONCATENATED with archived PSX bars. A series is wholly one source or
/// wholly the other, or the join sits at an arbitrary date with a scale change across it.</item>
/// <item>AHL bars are never written into the <c>daily_bars</c> archive, which holds raw exchange data
/// and is what reconciliation reads.</item>
/// </list>
///
/// <para>
/// Appending the live forming bar to an AHL series IS safe: the adjustment factor for the current
/// session is 1.0, verified by both sources returning identical values for today's bar.
/// </para>
/// </summary>
public sealed class AhlCandleSource
{
    private readonly AhlAnalyticsClient _client;
    private readonly ILogger<AhlCandleSource> _logger;

    public AhlCandleSource(AhlAnalyticsClient client, ILogger<AhlCandleSource> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>Configured on, per <c>Plugins:AhlAnalytics:Enabled</c>.</summary>
    public bool Enabled => _client.Enabled;

    /// <summary>
    /// Whether the portal is reachable RIGHT NOW without performing the SSO handshake.
    ///
    /// <para>
    /// This distinction is the whole point of the flag. The handshake's first hop runs against the
    /// broker session, and restoring a dead one can launch a browser and log in. A candle read is a
    /// routine, frequently repeated operation, so it must never be the thing that triggers a login —
    /// it falls back to PSX instead. The handshake happens on agent- or user-initiated calls
    /// (<c>market_movers</c>, <c>stock_dossier</c>), and once a token is held this returns true and
    /// candle reads start using it.
    /// </para>
    /// </summary>
    public bool ReadyWithoutHandshake => _client.Enabled && _client.HasToken;

    /// <summary>
    /// Daily bars for one symbol, oldest first, capped to the most recent <paramref name="sessions"/>.
    /// Returns an empty list when the portal is disabled, unreachable, or has no data for the symbol —
    /// never throws, because every caller has a PSX path to fall back to.
    /// </summary>
    public async Task<IReadOnlyList<PsxCandle>> GetDailyAsync(
        string symbol, int sessions, CancellationToken ct = default)
    {
        if (!_client.Enabled) return [];

        try
        {
            // Already oldest-first: the client reverses the portal's newest-first ordering once, at
            // its own boundary.
            var bars = await _client.GetDailyCandlesAsync(symbol, ct);
            if (bars.Count == 0) return [];

            var mapped = new List<PsxCandle>(Math.Min(bars.Count, sessions));
            foreach (var bar in bars)
            {
                if (ParseSessionDate(bar.Date) is not { } date) continue;

                // A bar missing a price is dropped rather than zero-filled, matching how the PSX
                // parser treats an incomplete row — a zero low would invent a support level.
                if (bar.Open <= 0 || bar.High <= 0 || bar.Low <= 0 || bar.Close <= 0) continue;

                mapped.Add(new PsxCandle
                {
                    Symbol = symbol,
                    Date = date,
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    // The portal publishes no LDCP on this endpoint. Left null rather than derived
                    // from the previous bar, because on an adjustment boundary a derived value would
                    // disagree with the exchange's own figure.
                    PreviousClose = null,
                    Volume = bar.Volume,
                    IsLive = false
                });
            }

            return mapped.Count <= sessions
                ? mapped
                : mapped.GetRange(mapped.Count - sessions, sessions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail soft: the caller falls back to PSX, which is the whole reason both exist.
            _logger.LogWarning(ex, "[AhlCandles] Daily history failed for {Symbol}.", symbol);
            return [];
        }
    }

    /// <summary>
    /// The portal's timestamps are <c>"yyyy-MM-dd HH:mm:ss"</c> with a synthetic time component
    /// (16:00:00 on daily bars, and occasionally an odd one like 17:14:39 on the oldest row). Only the
    /// date identifies the session, so the time is deliberately discarded rather than parsed into a
    /// bucket.
    /// </summary>
    private static DateOnly? ParseSessionDate(string? timestamp)
    {
        if (timestamp is not { Length: >= 10 }) return null;
        return DateOnly.TryParseExact(
            timestamp[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;
    }
}
