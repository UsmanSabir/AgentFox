using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Persistence;

namespace TradingAgent.Research;

/// <summary>
/// Serves daily and weekly candle history, preferring the local archive over the portal.
///
/// This is the layer that makes deep history practical. <see cref="PsxDataClient"/> can only reach the
/// exchange one date at a time, so reading two years from it costs ~500 requests every process start.
/// Reading the same history from <c>daily_bars</c> costs one query, leaving the portal to supply only
/// the dates the archive is missing — in steady state, today's session and nothing else.
///
/// The archive covers <see cref="TradingAgentOptions.AllowedSymbols"/> only (a deliberate storage
/// choice), so a symbol outside that list falls back entirely to the portal path and is therefore
/// limited to the configured lookback with no weekly structure. That fallback is reported in the
/// returned warnings rather than left to be inferred from a short series.
/// </summary>
public sealed class CandleHistoryProvider
{
    private readonly PsxDataClient _dataClient;
    private readonly ITradingRepository _repository;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<CandleHistoryProvider> _logger;

    public CandleHistoryProvider(
        PsxDataClient dataClient,
        ITradingRepository repository,
        IOptions<TradingAgentOptions> options,
        ILogger<CandleHistoryProvider> logger)
    {
        _dataClient = dataClient;
        _repository = repository;
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

        var allowed = _options.Value.AllowedSymbols
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fromArchive = new List<string>();
        var fromPortal = new List<string>();
        foreach (var symbol in symbols)
        {
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
        var live = includeLive
            ? portal?.Live ?? await GetLiveSafeAsync(warnings, ct)
            : new Dictionary<string, PsxLiveQuote>();

        var today = PsxTime.Today();
        var series = new Dictionary<string, IReadOnlyList<PsxCandle>>(StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in symbols.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Prefer whichever source gave the deeper history; the portal path is capped at the
            // configured lookback, the archive is not.
            var bars = new List<PsxCandle>();
            var fromDb = archived.GetValueOrDefault(symbol) ?? [];
            var fromWeb = portal?.Series.GetValueOrDefault(symbol) ?? [];
            bars.AddRange(fromDb.Count >= fromWeb.Count ? fromDb : fromWeb);

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

        return new CandleHistory
        {
            Series         = series,
            Live           = live,
            Sessions       = covered,
            RetrievedAtUtc = DateTime.UtcNow,
            Warnings       = warnings
        };
    }

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
                await _repository.SaveDailySessionAsync(session.Key, session.ToList(), ct);
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
            return await _dataClient.GetMarketWatchAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[CandleHistory] Live market watch unavailable.");
            warnings.Add($"Live market watch unavailable ({ex.Message}); analysis uses settled closes only.");
            return new Dictionary<string, PsxLiveQuote>();
        }
    }
}
