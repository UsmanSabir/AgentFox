using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Persistence;
using TradingAgent.Research;

namespace TradingAgent.Trading;

/// <summary>Live progress of a backfill pass, or the outcome of the last one.</summary>
public sealed record CandleBackfillProgress
{
    public bool IsRunning { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }

    /// <summary>Trading days this pass set out to retrieve.</summary>
    public int DatesTargeted { get; init; }

    public int DatesCompleted { get; init; }
    public int SessionsStored { get; init; }
    public int EmptyDates { get; init; }
    public DateOnly? CurrentDate { get; init; }

    /// <summary>True when the pass stopped early because the portal looked like it was throttling.</summary>
    public bool AbortedForThrottling { get; init; }

    public string? Message { get; init; }

    public decimal? PercentComplete => DatesTargeted > 0
        ? Math.Round((decimal)DatesCompleted / DatesTargeted * 100m, 1)
        : null;
}

/// <summary>Archive coverage plus whatever the backfill is currently doing.</summary>
public sealed record CandleArchiveStatus(
    bool BackfillEnabled,
    int BackfillYears,
    int ConfiguredSymbols,
    DailyArchiveStatus Archive,
    int MissingTradingDays,
    int TargetTradingDays,
    CandleBackfillProgress Progress);

/// <summary>
/// Owns the daily-candle backfill: the pass logic, its progress, and the single-flight guard.
///
/// Separate from <see cref="DailyCandleBackfillWorker"/> so the same pass can be started three ways —
/// by the worker's timer, by the web UI, or by the agent's own tool — without any of them duplicating
/// the pacing and throttle-detection rules, and without two of them ever running at once. A manual
/// trigger returns as soon as the pass has STARTED: a two-year backfill takes ~18 minutes, far longer
/// than any HTTP request should be held open, so progress is polled rather than awaited.
/// </summary>
public sealed class CandleBackfillRunner
{
    /// <summary>Empty weekdays in a row before a pass assumes throttling rather than holidays.</summary>
    private const int SuspiciousEmptyStreak = 4;

    private static readonly TimeSpan BetweenDates = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan AfterEmpty = TimeSpan.FromSeconds(2);

    private readonly PsxDataClient _dataClient;
    private readonly ITradingRepository _repository;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<CandleBackfillRunner> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _progressLock = new();
    private CandleBackfillProgress _progress = new() { Message = "No backfill pass has run yet." };

    public CandleBackfillRunner(
        PsxDataClient dataClient,
        ITradingRepository repository,
        IOptions<TradingAgentOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<CandleBackfillRunner> logger)
    {
        _dataClient = dataClient;
        _repository = repository;
        _options = options;
        _lifetime = lifetime;
        _logger = logger;
    }

    public CandleBackfillProgress Progress
    {
        get { lock (_progressLock) return _progress; }
    }

    /// <summary>
    /// Starts a pass in the background and returns whether it started (false = one is already running).
    /// Bound to the application lifetime, not to the caller's request, so closing the page or letting a
    /// tool call return does not abandon the pass halfway through.
    /// </summary>
    public bool TryStart(int? years = null)
    {
        if (!_gate.Wait(0)) return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await ExecutePassAsync(years, _lifetime.ApplicationStopping);
            }
            catch (OperationCanceledException)
            {
                SetProgress(p => p with
                {
                    IsRunning = false,
                    CompletedUtc = DateTime.UtcNow,
                    Message = "Backfill stopped because the application is shutting down. " +
                              "It resumes from where it left off on the next start."
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CandleBackfill] Pass failed.");
                SetProgress(p => p with
                {
                    IsRunning = false,
                    CompletedUtc = DateTime.UtcNow,
                    Message = $"Backfill failed: {ex.Message}"
                });
            }
            finally
            {
                _gate.Release();
            }
        });

        return true;
    }

    /// <summary>Runs a pass and awaits it. Used by the scheduled worker; no-ops if one is running.</summary>
    public async Task RunOnceAsync(int? years, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            await ExecutePassAsync(years, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Coverage of the archive against the configured target, plus current progress.</summary>
    public async Task<CandleArchiveStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var options = _options.Value;
        var years = options.Scan.BackfillYears;
        var symbols = options.AllowedSymbols.Count(s => !string.IsNullOrWhiteSpace(s));

        var archive = await _repository.GetDailyArchiveStatusAsync(ct);

        var target = 0;
        var missing = 0;
        if (years > 0)
        {
            var today = PsxTime.Today();
            var from = today.AddYears(-Math.Clamp(years, 1, 15));
            var weekdays = Weekdays(from, today);
            var covered = await _repository.GetCoveredDailyDatesAsync(from, today, ct);
            target = weekdays.Count;
            missing = weekdays.Count(d => !covered.Contains(d));
        }

        return new CandleArchiveStatus(
            BackfillEnabled: years > 0,
            BackfillYears: years,
            ConfiguredSymbols: symbols,
            Archive: archive,
            MissingTradingDays: missing,
            TargetTradingDays: target,
            Progress: Progress);
    }

    // ── The pass ──────────────────────────────────────────────────────────────

    private async Task ExecutePassAsync(int? yearsOverride, CancellationToken ct)
    {
        var options = _options.Value;
        var years = yearsOverride ?? options.Scan.BackfillYears;

        if (years <= 0)
        {
            SetProgress(_ => new CandleBackfillProgress
            {
                Message = "Backfill is disabled (Scan.BackfillYears = 0)."
            });
            return;
        }

        var allowed = options.AllowedSymbols
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (allowed.Count == 0)
        {
            SetProgress(_ => new CandleBackfillProgress
            {
                Message = "No AllowedSymbols are configured, so there is nothing to archive. " +
                          "Set the trading universe first — history is stored for those symbols only."
            });
            _logger.LogInformation("[CandleBackfill] Skipped: no AllowedSymbols configured.");
            return;
        }

        var today = PsxTime.Today();
        var from = today.AddYears(-Math.Clamp(years, 1, 15));
        var weekdays = Weekdays(from, today);
        var covered = await _repository.GetCoveredDailyDatesAsync(from, today, ct);

        // Newest first: the sessions a scan needs today land before the deep history.
        var missing = weekdays.Where(d => !covered.Contains(d)).OrderByDescending(d => d).ToList();

        if (missing.Count == 0)
        {
            var complete = await _repository.GetDailyArchiveStatusAsync(ct);
            SetProgress(_ => new CandleBackfillProgress
            {
                CompletedUtc = DateTime.UtcNow,
                DatesTargeted = 0,
                Message = $"Archive already complete: {complete.Bars:N0} bars for {complete.Symbols} " +
                          $"symbols, {complete.EarliestSession} to {complete.LatestSession}."
            });
            _logger.LogInformation("[CandleBackfill] Archive complete ({Bars} bars, {Symbols} symbols).",
                complete.Bars, complete.Symbols);
            return;
        }

        SetProgress(_ => new CandleBackfillProgress
        {
            IsRunning = true,
            StartedUtc = DateTime.UtcNow,
            DatesTargeted = missing.Count,
            Message = $"Archiving {missing.Count} trading days back to {from:yyyy-MM-dd} " +
                      $"for {allowed.Count} symbols."
        });

        _logger.LogInformation(
            "[CandleBackfill] {Missing} of {Total} weekdays missing back to {From}. Archiving {Symbols} symbols…",
            missing.Count, weekdays.Count, from, allowed.Count);

        var stored = 0;
        var empty = 0;
        var completed = 0;
        var streak = 0;

        foreach (var date in missing)
        {
            ct.ThrowIfCancellationRequested();
            SetProgress(p => p with { CurrentDate = date });

            var rows = await FetchWithRetryAsync(date, ct);

            if (rows.Count == 0)
            {
                // Could be a holiday, could be throttling — the portal answers both with an empty
                // table. Recording a throttled date as covered would punch a permanent hole in the
                // archive, so a streak aborts the pass instead of writing that history off.
                if (++streak >= SuspiciousEmptyStreak)
                {
                    var message =
                        $"Stopped after {streak} consecutive empty weekdays (ending {date:yyyy-MM-dd}) — " +
                        $"the portal appears to be throttling. {stored} sessions stored; the next pass " +
                        "resumes from here.";
                    _logger.LogWarning("[CandleBackfill] {Message}", message);
                    SetProgress(p => p with
                    {
                        IsRunning = false,
                        CompletedUtc = DateTime.UtcNow,
                        CurrentDate = null,
                        AbortedForThrottling = true,
                        Message = message
                    });
                    return;
                }

                await _repository.SaveDailySessionAsync(date, [], ct);
                empty++;
                completed++;
                SetProgress(p => p with { EmptyDates = empty, DatesCompleted = completed });
                await Task.Delay(AfterEmpty, ct);
                continue;
            }

            streak = 0;
            var bars = rows.Values.Where(b => allowed.Contains(b.Symbol)).ToList();
            await _repository.SaveDailySessionAsync(date, bars, ct);
            stored++;
            completed++;
            SetProgress(p => p with { SessionsStored = stored, DatesCompleted = completed });

            if (stored % 25 == 0)
                _logger.LogInformation("[CandleBackfill] {Stored}/{Missing} sessions archived (at {Date}).",
                    stored, missing.Count, date);

            await Task.Delay(BetweenDates, ct);
        }

        var final = await _repository.GetDailyArchiveStatusAsync(ct);
        var done =
            $"Stored {stored} sessions ({empty} non-trading days). Archive now holds {final.Bars:N0} bars " +
            $"for {final.Symbols} symbols, {final.EarliestSession} to {final.LatestSession}.";

        _logger.LogInformation("[CandleBackfill] Pass done. {Message}", done);
        SetProgress(p => p with
        {
            IsRunning = false,
            CompletedUtc = DateTime.UtcNow,
            CurrentDate = null,
            Message = done
        });
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

    private void SetProgress(Func<CandleBackfillProgress, CandleBackfillProgress> update)
    {
        lock (_progressLock) _progress = update(_progress);
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
