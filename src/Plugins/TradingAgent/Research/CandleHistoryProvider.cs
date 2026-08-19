using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.AhlAnalytics;
using TradingAgent.Config;
using TradingAgent.Observability;
using TradingAgent.Market;
using TradingAgent.Persistence;
using TradingAgent.Watchlist;

namespace TradingAgent.Research;

/// <summary>
/// Serves daily and weekly candle history, preferring the local archive over the portal.
///
/// This is the layer that makes deep history practical. <see cref="PsxDataClient"/> can only reach the
/// exchange one date at a time, so reading two years from it costs ~500 requests every process start.
/// Reading the same history from <c>daily_bars</c> costs one query, leaving the portal to supply only
/// the dates the archive is missing — in steady state, today's session and nothing else.
///
/// The archive covers <see cref="MonitoredUniverse.ForArchiveAsync"/> — the watchlist plus the
/// tradable list — so a symbol outside it falls back entirely to the portal path and is therefore
/// limited to the configured lookback with no weekly structure. That fallback is reported in the
/// returned warnings rather than left to be inferred from a short series.
/// </summary>
public sealed class CandleHistoryProvider
{
    private readonly PsxDataClient _dataClient;
    private readonly CompositeLiveQuoteSource _quotes;
    private readonly ITradingRepository _repository;
    private readonly MonitoredUniverse _universe;
    private readonly AhlCandleSource _ahl;
    private readonly TradingActivityLog _activity;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<CandleHistoryProvider> _logger;

    /// <summary>
    /// Last source mix announced to the activity log. The log collapses identical consecutive lines,
    /// but a scan runs every few minutes and would still repost the same line indefinitely, so the
    /// announcement is suppressed until the mix actually changes — which is the only moment an
    /// operator needs to see it.
    /// </summary>
    private string? _lastAnnouncedSourceMix;

    public CandleHistoryProvider(
        PsxDataClient dataClient,
        CompositeLiveQuoteSource quotes,
        ITradingRepository repository,
        MonitoredUniverse universe,
        AhlCandleSource ahl,
        TradingActivityLog activity,
        IOptions<TradingAgentOptions> options,
        ILogger<CandleHistoryProvider> logger)
    {
        _dataClient = dataClient;
        _quotes = quotes;
        _repository = repository;
        _universe = universe;
        _ahl = ahl;
        _activity = activity;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Daily candles for <paramref name="symbols"/> over the most recent <paramref name="sessions"/>
    /// trading days, oldest first, topped up with the live forming bar when requested.
    /// </summary>
    public async Task<CandleHistory> GetDailyAsync(
        IReadOnlyList<string> symbols,
        int sessions,
        bool includeLive = true,
        CancellationToken ct = default)
    {
        sessions = Math.Clamp(sessions, 5, 5000);
        var warnings = new List<string>();
        var archived = new Dictionary<string, IReadOnlyList<PsxCandle>>(StringComparer.OrdinalIgnoreCase);

        // The archived universe, NOT the tradable one: a watchlist symbol needs the same deep history
        // as a tradable one or its weekly levels cannot be computed.
        var allowed = (await _universe.ForArchiveAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Which source actually served each symbol, reported back on CandleHistory and summarised to
        // the activity log. Not cosmetic: AHL bars are corporate-action ADJUSTED and PSX bars are raw,
        // so a consumer comparing a level against a fill has to know which it is holding.
        var sources = new Dictionary<string, CandleSource>(StringComparer.OrdinalIgnoreCase);

        // ── AHL analytics first, when it is already usable ────────────────────
        // Gated on ReadyWithoutHandshake, NOT merely on Enabled: the SSO handshake runs against the
        // broker session and restoring a dead one can launch a browser and log in. A candle read
        // happens on every scan, so it must never be what triggers a login — it quietly uses PSX
        // instead until an agent- or user-initiated call has obtained a token.
        var fromAhl = new Dictionary<string, IReadOnlyList<PsxCandle>>(StringComparer.OrdinalIgnoreCase);
        if (_ahl.ReadyWithoutHandshake)
        {
            foreach (var symbol in symbols.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var bars = await _ahl.GetDailyAsync(symbol, sessions, ct);
                // Require a usable depth before displacing the PSX path, so a symbol the portal
                // barely covers does not end up with a shorter series than PSX would have given.
                if (bars.Count >= Math.Min(sessions, 20))
                {
                    fromAhl[symbol] = bars;
                    sources[symbol] = CandleSource.AhlAnalytics;
                }
            }
        }

        var fromArchive = new List<string>();
        var fromPortal = new List<string>();
        foreach (var symbol in symbols)
        {
            // Symbols AHL already covered need neither the archive read nor a portal fetch.
            if (fromAhl.ContainsKey(symbol)) continue;
            (allowed.Contains(symbol) ? fromArchive : fromPortal).Add(symbol);
        }

        // ── Archive-backed symbols ────────────────────────────────────────────
        foreach (var symbol in fromArchive)
        {
            try
            {
                var bars = await _repository.GetDailyBarsAsync(symbol, sessions, ct);
                if (bars.Count > 0) archived[symbol] = bars;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CandleHistory] Archive read failed for {Symbol}.", symbol);
                warnings.Add($"{symbol}: archived history could not be read ({ex.Message}); using the portal.");
            }
        }

        // Any archive-backed symbol still short of the requested window (or absent entirely, e.g. the
        // backfill has not run) has to come from the portal like everything else.
        foreach (var symbol in fromArchive)
        {
            if (!archived.TryGetValue(symbol, out var bars) || bars.Count < Math.Min(sessions, 20))
                fromPortal.Add(symbol);
        }

        // ── Portal top-up ─────────────────────────────────────────────────────
        CandleHistory? portal = null;
        if (fromPortal.Count > 0)
        {
            portal = await _dataClient.GetCandleHistoryAsync(
                fromPortal.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Math.Min(sessions, _options.Value.Scan.LookbackDays),
                includeLive,
                ct);
            warnings.AddRange(portal.Warnings);

            // Everything just fetched is settled market data, so archive whatever belongs to the
            // configured universe — that is how the first scan after a fresh install starts filling
            // the archive without waiting for the backfill to reach that date.
            await ArchiveFetchedAsync(portal, allowed, ct);
        }

        // ── Live bar ──────────────────────────────────────────────────────────
        // Deliberately NOT portal?.Live: that field only ever holds the PSX market watch, so
        // preferring it whenever a portal fetch happened would silently bypass the broker feed on
        // the common path. The composite is asked every time — its PSX source reads the same cached
        // snapshot the portal fetch already populated, so this costs no additional request.
        var live = includeLive
            ? await GetLiveSafeAsync(warnings, ct)
            : new Dictionary<string, PsxLiveQuote>();

        var today = PsxTime.Today();
        var series = new Dictionary<string, IReadOnlyList<PsxCandle>>(StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in symbols.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bars = new List<PsxCandle>();

            if (fromAhl.TryGetValue(symbol, out var ahlBars))
            {
                // Used WHOLE and never combined with archived PSX bars. The two are on different price
                // scales either side of any corporate action, so a merged series would carry a
                // silent scale change at whatever date the join happened to fall on.
                bars.AddRange(ahlBars);
            }
            else
            {
                // Prefer whichever source gave the deeper history; the portal path is capped at the
                // configured lookback, the archive is not.
                var fromDb = archived.GetValueOrDefault(symbol) ?? [];
                var fromWeb = portal?.Series.GetValueOrDefault(symbol) ?? [];
                bars.AddRange(fromDb.Count >= fromWeb.Count ? fromDb : fromWeb);
                if (bars.Count > 0)
                {
                    sources[symbol] = fromDb.Count >= fromWeb.Count
                        ? CandleSource.LocalArchive
                        : CandleSource.PsxPortal;
                }
            }

            if (bars.Count == 0)
            {
                if (!warnings.Any(w => w.StartsWith($"{symbol}:", StringComparison.OrdinalIgnoreCase)))
                    warnings.Add($"{symbol}: no daily candles are available from the archive or the portal.");
                continue;
            }

            if (live.TryGetValue(symbol, out var quote) && quote.ToCandle(today) is { } forming)
            {
                if (bars[^1].Date == forming.Date) bars[^1] = forming;
                else if (bars[^1].Date < forming.Date) bars.Add(forming);
            }

            series[symbol] = bars.OrderBy(b => b.SortKeyUtc).TakeLast(sessions).ToList();
        }

        var covered = series.Values
            .SelectMany(s => s.Select(b => b.Date))
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        AnnounceSources(sources);

        return new CandleHistory
        {
            Series         = series,
            Live           = live,
            Sessions       = covered,
            Sources        = sources,
            RetrievedAtUtc = DateTime.UtcNow,
            Warnings       = warnings
        };
    }

    /// <summary>
    /// Posts the source mix to the activity log, once per change.
    ///
    /// <para>
    /// Which source served the candles is an operational fact worth surfacing rather than inferring:
    /// AHL bars are corporate-action adjusted and PSX bars are raw, so the answer changes how a level
    /// or an indicator should be read. It also makes the fallback visible — if the analytics portal
    /// stops being reachable, the line changes to PSX and says so, instead of the system quietly
    /// serving a different kind of price.
    /// </para>
    /// </summary>
    private void AnnounceSources(Dictionary<string, CandleSource> sources)
    {
        if (sources.Count == 0) return;

        var counts = sources.Values
            .GroupBy(v => v)
            .OrderByDescending(g => g.Count())
            .ToList();

        var mix = string.Join(", ", counts.Select(g => $"{Describe(g.Key)} {g.Count()}"));
        if (mix == _lastAnnouncedSourceMix) return;
        _lastAnnouncedSourceMix = mix;

        var primary = counts[0].Key;
        var detail = primary == CandleSource.AhlAnalytics
            ? "AHL bars are corporate-action ADJUSTED — correct for indicators and levels, but they " +
              "do not reconcile against fill prices. PSX remains the source of record for money."
            : _ahl.Enabled
                ? "The AHL analytics portal is enabled but has no session yet, so candles come from " +
                  "PSX (raw, as-traded prices)."
                : "The AHL analytics portal is disabled; candles come from PSX (raw, as-traded prices).";

        _activity.Info("Candles", $"Daily candle source: {mix}", detail);
        _logger.LogInformation("[CandleHistory] Source mix: {Mix}", mix);
    }

    private static string Describe(CandleSource source) => source switch
    {
        CandleSource.AhlAnalytics => "AHL analytics",
        CandleSource.LocalArchive => "local archive (PSX)",
        CandleSource.PsxPortal    => "PSX portal",
        _                          => "unknown"
    };

    /// <summary>
    /// Weekly candles for one symbol, resampled from its daily history. Exact rather than
    /// approximated, because the daily bars carry true highs and lows.
    /// </summary>
    public async Task<IReadOnlyList<PsxCandle>> GetWeeklyAsync(
        string symbol, int weeks, CancellationToken ct = default)
    {
        // Ask for enough sessions to form the requested weeks with room for holidays.
        var sessions = Math.Clamp(weeks * 6, 30, 5000);
        var history = await GetDailyAsync([symbol], sessions, includeLive: true, ct);

        return history.Series.TryGetValue(symbol, out var daily)
            ? CandleResampler.ToWeekly(daily).TakeLast(weeks).ToList()
            : [];
    }

    private async Task ArchiveFetchedAsync(
        CandleHistory fetched, IReadOnlySet<string> allowed, CancellationToken ct)
    {
        if (allowed.Count == 0) return;

        var bySession = fetched.Series.Values
            .SelectMany(bars => bars)
            .Where(b => !b.IsLive && !b.IsIntraday && allowed.Contains(b.Symbol))
            .GroupBy(b => b.Date);

        foreach (var session in bySession)
        {
            try
            {
                // Coverage is claimed only for the symbols this session actually yielded a bar for.
                // Unlike the backfill, which fetches the whole market for a date and can honestly speak
                // for every symbol in it, this path fetched a per-symbol series: a symbol with no bar
                // here may simply not have been asked about, and claiming it would freeze a gap the
                // backfill could never revisit. Under-claiming costs at most one refetch later.
                var bars = session.ToList();
                await _repository.SaveDailySessionAsync(
                    session.Key, bars, [.. bars.Select(b => b.Symbol).Distinct()], ct);
            }
            catch (Exception ex)
            {
                // Archiving is an optimisation; failing to write must never fail an analysis.
                _logger.LogWarning(ex, "[CandleHistory] Archiving session {Date} failed.", session.Key);
                return;
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, PsxLiveQuote>> GetLiveSafeAsync(
        List<string> warnings, CancellationToken ct)
    {
        try
        {
            // Goes through the composite rather than PsxDataClient directly, so the broker's live
            // feed tops up the forming candle when it is running and the PSX market watch covers the
            // rest. The snapshot's own warnings are surfaced: a forming bar built from a degraded
            // source is still usable, but the caller has to be able to say so.
            var snapshot = await _quotes.GetQuotesAsync(ct);
            warnings.AddRange(snapshot.Warnings);
            return snapshot.Quotes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[CandleHistory] Live quotes unavailable.");
            warnings.Add($"Live quotes unavailable ({ex.Message}); analysis uses settled closes only.");
            return new Dictionary<string, PsxLiveQuote>();
        }
    }
}
