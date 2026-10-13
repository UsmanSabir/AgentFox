using System.Collections.Concurrent;
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
    private readonly TimeProvider _clock;

    public AhlCandleSource(
        AhlAnalyticsClient client,
        ILogger<AhlCandleSource> logger,
        TimeProvider? clock = null)
    {
        _client = client;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
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
    /// it falls back to PSX instead. The handshake happens on explicit agent/user tool calls, or by
    /// the passive movers endpoint after the AHK feed already owns a broker session. Once a token is
    /// held this returns true and candle reads start using it.
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
            var bars = await _client.GetDailyCandlesAsync(symbol, minimumCandles: sessions, ct: ct);
            if (bars.Count == 0) return [];

            var currentPktSession = PktSessionDate(_clock.GetUtcNow());
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
                    // AHL includes the forming session in the daily endpoint. Calling it settled is
                    // what let an early snapshot enter durable history and remain there all day.
                    IsLive = date == currentPktSession
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

    // ── intraday ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Earlier sessions' one-minute bars, per symbol, for the current PKT date only. Settled bars do
    /// not change, so one <c>5D</c> read a day is enough; re-reading 1,800 rows on every chart request
    /// or strategy pass would spend the portal's shared 40-a-minute budget on nothing.
    ///
    /// <para>
    /// Retention: memory only. The whole set is dropped on the first write of a new PKT date, and at
    /// <see cref="PriorSessionCacheMaxSymbols"/> entries, so it holds at most one day's reads for the
    /// symbols actually asked about. An empty read (an outage or a throttle) is never stored.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, (DateOnly Day, IReadOnlyList<PsxCandle> Bars)> _priorSessions =
        new(StringComparer.OrdinalIgnoreCase);

    private const int PriorSessionCacheMaxSymbols = 300;

    /// <summary>
    /// Today's one-minute bars, oldest first, or empty when the portal is not signed in, fails, or
    /// has nothing for today yet. Never performs the SSO handshake (see <see cref="ReadyWithoutHandshake"/>).
    ///
    /// <para>
    /// <c>1D</c> means "the latest session", which before the open is YESTERDAY's. Only bars dated
    /// today are returned, so a pre-open read yields nothing and the caller falls back to PSX rather
    /// than presenting yesterday's tape as today's.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<PsxCandle>> GetTodayMinuteBarsAsync(string symbol, CancellationToken ct = default)
    {
        if (!ReadyWithoutHandshake) return [];
        var today = PktSessionDate(_clock.GetUtcNow());
        var bars = await ReadMinuteBarsAsync(symbol, "1D", ct);
        return bars.Where(b => b.Date == today).ToList();
    }

    /// <summary>
    /// One-minute bars for the sessions BEFORE today that the portal still serves (its <c>5D</c>
    /// window, so up to four), oldest first. Read at most once per symbol per PKT day. Empty when the
    /// portal is not signed in or the read fails.
    ///
    /// <para>
    /// These may be corporate-action ADJUSTED if an ex-date falls inside the window, like the daily
    /// series. Check <see cref="LooksAdjusted"/> before placing them beside raw archived bars.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<PsxCandle>> GetPriorSessionMinuteBarsAsync(string symbol, CancellationToken ct = default)
    {
        if (!ReadyWithoutHandshake) return [];
        var today = PktSessionDate(_clock.GetUtcNow());
        if (_priorSessions.TryGetValue(symbol, out var held) && held.Day == today) return held.Bars;

        var prior = (await ReadMinuteBarsAsync(symbol, "5D", ct)).Where(b => b.Date < today).ToList();
        if (prior.Count == 0) return prior;

        if (_priorSessions.Count >= PriorSessionCacheMaxSymbols || _priorSessions.Values.Any(e => e.Day != today))
            _priorSessions.Clear();
        _priorSessions[symbol] = (today, prior);
        return prior;
    }

    /// <summary>
    /// True when any price is finer than a paisa. PSX trades in whole paisa, so a finer price can only
    /// be the portal's corporate-action adjustment (its documented fingerprint, e.g. LUCK's
    /// <c>162.09221369698164</c>). Such bars sit on a different scale from raw exchange bars.
    /// </summary>
    public static bool LooksAdjusted(IEnumerable<PsxCandle> bars) =>
        bars.Any(b => !WholePaisa(b.Open) || !WholePaisa(b.High) || !WholePaisa(b.Low) || !WholePaisa(b.Close));

    private static bool WholePaisa(decimal price) => decimal.Round(price, 2) == price;

    /// <summary>
    /// Maps the portal's one-minute rows. <b>A row's stamp is the END of its minute</b>, in PKT
    /// wall-clock time: the row stamped <c>09:18:00</c> holds the trades from 09:17:00 to 09:17:59.
    /// MEASURED 2026-09-25 against the PSX tick tape for PPL and OGDC: read as the end, volume matched
    /// exactly on 77 of 77 and 76 of 76 settled minutes; read as the start, on none. Premium's planner
    /// reads the same rows the same way (<c>TradeCastPlannerMarketData</c>).
    /// </summary>
    private async Task<IReadOnlyList<PsxCandle>> ReadMinuteBarsAsync(string symbol, string range, CancellationToken ct)
    {
        try
        {
            var rows = await _client.GetIntradayCandlesAsync(symbol, range, ct);
            if (rows.Count == 0) return [];

            return MapMinuteRows(symbol, rows, _clock.GetUtcNow().UtcDateTime);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail soft: the caller falls back to the PSX tick tape and the archive.
            _logger.LogWarning(ex, "[AhlCandles] Intraday {Range} bars failed for {Symbol}.", range, symbol);
            return [];
        }
    }

    /// <summary>
    /// The portal's one-minute rows as bars, oldest first. Pure, so the stamp reading and the
    /// dropped-row rules are tested rather than trusted. See <see cref="ReadMinuteBarsAsync"/> for
    /// how the stamp is read.
    /// </summary>
    internal static IReadOnlyList<PsxCandle> MapMinuteRows(string symbol, IReadOnlyList<AhlCandle> rows, DateTime nowUtc)
    {
        var mapped = new List<PsxCandle>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Date is not { Length: >= 19 } stamp
                || !DateTime.TryParseExact(stamp[..19], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var pkt))
                continue;
            // Dropped rather than zero-filled, as for daily bars: a zero low would invent a level.
            if (row.Open <= 0 || row.High <= 0 || row.Low <= 0 || row.Close <= 0) continue;

            // The stamp closes the minute, so the bar starts one minute earlier. PKT is UTC+5 all year,
            // with no daylight saving. A session never crosses midnight, so the date is unaffected.
            var startUtc = DateTime.SpecifyKind(pkt.AddHours(-5).AddMinutes(-1), DateTimeKind.Utc);
            mapped.Add(new PsxCandle
            {
                Symbol          = symbol,
                Date            = DateOnly.FromDateTime(pkt),
                Open            = row.Open,
                High            = row.High,
                Low             = row.Low,
                Close           = row.Close,
                PreviousClose   = null,
                Volume          = row.Volume,
                IntervalMinutes = 1,
                BucketStartUtc  = startUtc,
                IsLive          = startUtc.AddMinutes(1) > nowUtc
            });
        }

        mapped.Sort((a, b) => a.SortKeyUtc.CompareTo(b.SortKeyUtc));
        return mapped;
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

    /// <summary>Pakistan has no daylight-saving transition; PSX session dates are UTC+05:00.</summary>
    internal static DateOnly PktSessionDate(DateTimeOffset utcNow) =>
        DateOnly.FromDateTime(utcNow.UtcDateTime.AddHours(5));
}
