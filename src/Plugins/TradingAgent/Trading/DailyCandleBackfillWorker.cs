using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Persistence;
using TradingAgent.Research;

namespace TradingAgent.Trading;

/// <summary>
/// One-time (then incremental) backfill of daily OHLC history into the trading ledger.
///
/// The exchange serves settled candles one DATE at a time — each request returning every symbol — so
/// two years of history is ~500 requests. Doing that on demand would put a multi-minute stall in front
/// of a user's question and repeat it after every restart; doing it once into <c>daily_bars</c> makes
/// weekly levels possible and reduces steady-state cost to one request per new trading day.
///
/// Three properties make it safe to leave running unattended:
///
///   Resumable — <c>daily_bar_coverage</c> records every date already retrieved, including
///   non-trading days, so a restart continues instead of starting over.
///
///   Gentle — dates are fetched one at a time with a pause between them. The portal answers bursts
///   with HTTP 200 and an EMPTY table, which is indistinguishable from a holiday, so pacing is what
///   protects the archive's correctness, not just the portal's load.
///
///   Suspicious of silence — an empty response is retried once, and a run that hits a long streak of
///   empty weekdays assumes it is being throttled and stops rather than recording that stretch of
///   history as if the market had been closed for it.
/// </summary>
public sealed class DailyCandleBackfillWorker : BackgroundService
{
    /// <summary>Empty weekdays in a row before a run assumes throttling rather than holidays.</summary>
    private const int SuspiciousEmptyStreak = 4;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan BetweenDates = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan AfterEmpty = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BetweenPasses = TimeSpan.FromHours(6);

    private readonly PsxDataClient _dataClient;
    private readonly ITradingRepository _repository;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<DailyCandleBackfillWorker> _logger;

    public DailyCandleBackfillWorker(
        PsxDataClient dataClient,
        ITradingRepository repository,
        IOptions<TradingAgentOptions> options,
        ILogger<DailyCandleBackfillWorker> logger)
    {
        _dataClient = dataClient;
        _repository = repository;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scan = _options.Value.Scan;
        if (scan.BackfillYears <= 0)
        {
            _logger.LogInformation("[CandleBackfill] Disabled (Scan.BackfillYears = 0).");
            return;
        }

        // Let the app finish starting; history is never urgent.
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CandleBackfill] Pass failed; retrying later.");
            }

            // A completed archive still wakes periodically to pick up each new session, and an
            // interrupted one gets another attempt.
            try { await Task.Delay(BetweenPasses, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        var options = _options.Value;
        var scan = options.Scan;

        var allowed = options.AllowedSymbols
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (allowed.Count == 0)
        {
            _logger.LogInformation(
                "[CandleBackfill] No AllowedSymbols configured — nothing to archive. " +
                "Configure the universe first; history is stored for those symbols only.");
            return;
        }

        var today = PsxTime.Today();
        var from = today.AddYears(-Math.Clamp(scan.BackfillYears, 1, 15));

        var covered = await _repository.GetCoveredDailyDatesAsync(from, today, ct);
        var missing = Weekdays(from, today).Where(d => !covered.Contains(d)).ToList();

        if (missing.Count == 0)
        {
            var status = await _repository.GetDailyArchiveStatusAsync(ct);
            _logger.LogInformation(
                "[CandleBackfill] Archive complete: {Bars} bars for {Symbols} symbols, {Earliest} to {Latest}.",
                status.Bars, status.Symbols, status.EarliestSession, status.LatestSession);
            return;
        }

        _logger.LogInformation(
            "[CandleBackfill] {Missing} of {Total} weekdays missing back to {From}. Archiving {Symbols} symbols…",
            missing.Count, Weekdays(from, today).Count, from, allowed.Count);

        // Newest first: the sessions a scan needs today are archived before the deep history.
        missing.Sort((a, b) => b.CompareTo(a));

        var stored = 0;
        var emptyStreak = 0;

        foreach (var date in missing)
        {
            ct.ThrowIfCancellationRequested();

            var rows = await FetchWithRetryAsync(date, ct);

            if (rows.Count == 0)
            {
                // Could be a holiday, could be throttling. Recording a throttled date as covered would
                // permanently punch a hole in the archive, so a streak aborts the pass instead.
                if (++emptyStreak >= SuspiciousEmptyStreak)
                {
                    _logger.LogWarning(
                        "[CandleBackfill] {Streak} consecutive weekdays returned no rows (ending {Date}). " +
                        "Assuming the portal is throttling; stopping this pass with {Stored} sessions stored. " +
                        "It will resume from here.",
                        emptyStreak, date, stored);
                    return;
                }

                await _repository.SaveDailySessionAsync(date, [], ct);
                await Task.Delay(AfterEmpty, ct);
                continue;
            }

            emptyStreak = 0;
            var bars = rows.Values.Where(b => allowed.Contains(b.Symbol)).ToList();
            await _repository.SaveDailySessionAsync(date, bars, ct);
            stored++;

            if (stored % 25 == 0)
                _logger.LogInformation("[CandleBackfill] {Stored}/{Missing} sessions archived (at {Date}).",
                    stored, missing.Count, date);

            await Task.Delay(BetweenDates, ct);
        }

        var final = await _repository.GetDailyArchiveStatusAsync(ct);
        _logger.LogInformation(
            "[CandleBackfill] Pass done: {Stored} sessions stored this pass; archive now holds {Bars} bars " +
            "for {Symbols} symbols, {Earliest} to {Latest}.",
            stored, final.Bars, final.Symbols, final.EarliestSession, final.LatestSession);
    }

    private async Task<IReadOnlyDictionary<string, PsxCandle>> FetchWithRetryAsync(
        DateOnly date, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var rows = await _dataClient.FetchMarketDayUncachedAsync(date, ct);
                if (rows.Count > 0 || attempt == 2) return rows;

                // One retry before believing an empty answer.
                await Task.Delay(AfterEmpty, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "[CandleBackfill] Fetch attempt {Attempt} failed for {Date}.", attempt, date);
                if (attempt == 2) return new Dictionary<string, PsxCandle>();
                await Task.Delay(AfterEmpty, ct);
            }
        }

        return new Dictionary<string, PsxCandle>();
    }

    private static List<DateOnly> Weekdays(DateOnly from, DateOnly to)
    {
        var dates = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                dates.Add(d);
        }
        return dates;
    }
}
