using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using TradingAgent.Analysis;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Persistence;
using TradingAgent.Research;
using TradingAgent.Watchlist;

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

/// <summary>
/// One symbol that cannot yet produce weekly levels, and how far off it is. Reported per symbol because
/// the archive can be complete market-wide while an individual symbol is starved: coverage is per
/// (date, symbol), and a symbol added to the universe after a date was fetched was never requested for
/// it. <c>MissingSessions</c> is what a backfill targeting this symbol would have to fetch.
/// </summary>
public sealed record SymbolArchiveGap(string Symbol, int ArchivedBars, int MissingSessions);

/// <summary>Archive coverage plus whatever the backfill is currently doing.</summary>
public sealed record CandleArchiveStatus(
    bool BackfillEnabled,
    int BackfillYears,
    int ConfiguredSymbols,
    DailyArchiveStatus Archive,
    int MissingTradingDays,
    int TargetTradingDays,
    int DailyBarsForWeekly,
    IReadOnlyList<SymbolArchiveGap> SymbolsShortOfWeekly,
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
    private readonly MonitoredUniverse _universe;
    private readonly IMarketCalendar _calendar;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<CandleBackfillRunner> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _progressLock = new();
    private CandleBackfillProgress _progress = new() { Message = "No backfill pass has run yet." };

    public CandleBackfillRunner(
        PsxDataClient dataClient,
        ITradingRepository repository,
        MonitoredUniverse universe,
        IMarketCalendar calendar,
        IOptions<TradingAgentOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<CandleBackfillRunner> logger)
    {
        _dataClient = dataClient;
        _repository = repository;
        _universe = universe;
        _calendar = calendar;
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
    ///
    /// <para>
    /// <paramref name="symbols"/> narrows which dates the pass considers missing — the dates those
    /// symbols have never been requested for — rather than which symbols get stored. A session fetch
    /// returns the whole market for one request, so every archived symbol is written for each date
    /// visited either way; scoping only avoids revisiting dates that are already complete for everyone
    /// else. That is the difference between filling one starved symbol in a few hundred requests and not
    /// being able to fill it at all.
    /// </para>
    /// </summary>
    public bool TryStart(int? years = null, IReadOnlyCollection<string>? symbols = null)
    {
        if (!_gate.Wait(0)) return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await ExecutePassAsync(years, symbols, _lifetime.ApplicationStopping);
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
    public async Task RunOnceAsync(
        int? years, CancellationToken ct, IReadOnlyCollection<string>? symbols = null)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            await ExecutePassAsync(years, symbols, ct);
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
        var symbols = await _universe.ForArchiveAsync(ct);

        var archive = await _repository.GetDailyArchiveStatusAsync(ct);
        var barCounts = await _repository.GetDailyBarCountsAsync(symbols, ct);

        var target = 0;
        var missing = 0;
        var gaps = new List<SymbolArchiveGap>();

        if (years > 0)
        {
            // Measured against the last SETTLED session, not today: counting a session still in
            // progress as missing would leave the archive permanently reported as incomplete.
            var settledThrough = LastSettledSession();
            var from = settledThrough.AddYears(-Math.Clamp(years, 1, 15));
            var weekdays = Weekdays(from, settledThrough);
            target = weekdays.Count;

            // Symbol-aware: a date counts as covered only once every archived symbol has been requested
            // for it. Measured per date alone, an archive could report itself complete while a symbol
            // added after those dates were fetched held almost no history — which is exactly the state
            // that made a starved symbol unfixable.
            var covered = await _repository.GetCoveredDailyDatesAsync(from, settledThrough, symbols, ct);
            missing = weekdays.Count(d => !covered.Contains(d));

            var perSymbol = await _repository.GetCoveredDailyDateCountsAsync(
                from, settledThrough, symbols, ct);

            foreach (var symbol in symbols)
            {
                var bars = barCounts.GetValueOrDefault(symbol);
                if (bars >= MultiTimeframeAnalyzer.MinimumDailyBarsForWeekly) continue;

                gaps.Add(new SymbolArchiveGap(
                    Symbol: symbol,
                    ArchivedBars: bars,
                    MissingSessions: Math.Max(0, target - perSymbol.GetValueOrDefault(symbol))));
            }

            gaps.Sort((a, b) => a.ArchivedBars.CompareTo(b.ArchivedBars));
        }

        return new CandleArchiveStatus(
            BackfillEnabled: years > 0,
            BackfillYears: years,
            ConfiguredSymbols: symbols.Count,
            Archive: archive,
            MissingTradingDays: missing,
            TargetTradingDays: target,
            DailyBarsForWeekly: MultiTimeframeAnalyzer.MinimumDailyBarsForWeekly,
            SymbolsShortOfWeekly: gaps,
            Progress: Progress);
    }

    // ── The pass ──────────────────────────────────────────────────────────────

    private async Task ExecutePassAsync(
        int? yearsOverride, IReadOnlyCollection<string>? symbolScope, CancellationToken ct)
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

        // The archive universe — watchlist plus tradable symbols — so a watched symbol accumulates the
        // same daily history, and therefore the same weekly levels, as a tradable one.
        var allowed = (await _universe.ForArchiveAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (allowed.Count == 0)
        {
            SetProgress(_ => new CandleBackfillProgress
            {
                Message = "Nothing to archive: neither AllowedSymbols nor the watchlist has any symbols. " +
                          "Add symbols to the watchlist (or configure AllowedSymbols) first."
            });
            _logger.LogInformation("[CandleBackfill] Skipped: monitored universe is empty.");
            return;
        }

        // A scope narrows which dates count as missing, never which symbols are stored. Symbols outside
        // the archive universe are refused rather than silently dropped: their rows would be filtered out
        // of every session fetched, so they could never become covered and the pass would be asked to run
        // again forever.
        var scope = allowed;
        if (symbolScope is { Count: > 0 })
        {
            var requested = symbolScope
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var unknown = requested.Where(s => !allowed.Contains(s)).ToList();

            if (unknown.Count > 0)
            {
                var message =
                    $"Cannot backfill {string.Join(", ", unknown)}: not in the archive universe. Add the " +
                    "symbol to the watchlist (or to AllowedSymbols) first — a session fetch is filtered " +
                    "to that universe, so nothing would be stored for it.";
                _logger.LogWarning("[CandleBackfill] {Message}", message);
                SetProgress(_ => new CandleBackfillProgress { Message = message });
                return;
            }

            scope = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // Only settled sessions are archived. Fetching the day still in progress stores a partial bar
        // that the coverage marker then prevents from ever being corrected — or an empty table
        // indistinguishable from a holiday, which becomes a permanent hole. Clearing coverage past the
        // settlement point also repairs any date recorded prematurely by an earlier build or by an
        // opportunistic archive write during the session.
        var settledThrough = LastSettledSession();
        await _repository.ClearDailyCoverageAfterAsync(settledThrough, ct);

        var from = settledThrough.AddYears(-Math.Clamp(years, 1, 15));
        var weekdays = Weekdays(from, settledThrough);
        var covered = await _repository.GetCoveredDailyDatesAsync(from, settledThrough, scope, ct);

        // Newest first: the sessions a scan needs today land before the deep history.
        var missing = weekdays.Where(d => !covered.Contains(d)).OrderByDescending(d => d).ToList();

        // Named when scoped, because which symbols a targeted pass is for is the whole point of it;
        // capped so a caller passing the full universe by hand cannot turn the status into a wall of
        // tickers.
        var scopeLabel = ReferenceEquals(scope, allowed)
            ? $"all {allowed.Count} archived symbols"
            : string.Join(", ", scope.Order(StringComparer.Ordinal).Take(6))
              + (scope.Count > 6 ? $" and {scope.Count - 6} more" : "");

        if (missing.Count == 0)
        {
            var complete = await _repository.GetDailyArchiveStatusAsync(ct);
            SetProgress(_ => new CandleBackfillProgress
            {
                CompletedUtc = DateTime.UtcNow,
                DatesTargeted = 0,
                Message = $"Archive already complete for {scopeLabel}: {complete.Bars:N0} bars for " +
                          $"{complete.Symbols} symbols, {complete.EarliestSession} to " +
                          $"{complete.LatestSession}."
            });
            _logger.LogInformation(
                "[CandleBackfill] Archive complete for {Scope} ({Bars} bars, {Symbols} symbols).",
                scopeLabel, complete.Bars, complete.Symbols);
            return;
        }

        SetProgress(_ => new CandleBackfillProgress
        {
            IsRunning = true,
            StartedUtc = DateTime.UtcNow,
            DatesTargeted = missing.Count,
            Message = $"Archiving {missing.Count} trading days back to {from:yyyy-MM-dd}, targeting " +
                      $"{scopeLabel}. Every archived symbol is stored for each day fetched."
        });

        _logger.LogInformation(
            "[CandleBackfill] {Missing} of {Total} weekdays missing back to {From} for {Scope}. "
            + "Archiving {Symbols} symbols…",
            missing.Count, weekdays.Count, from, scopeLabel, allowed.Count);

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

                await _repository.SaveNonTradingDayAsync(date, ct);
                empty++;
                completed++;
                SetProgress(p => p with { EmptyDates = empty, DatesCompleted = completed });
                await Task.Delay(AfterEmpty, ct);
                continue;
            }

            streak = 0;
            // Filtered to (and recorded as covering) the whole archive universe, not just the scope: the
            // fetch returned the entire market for this date, so claiming less would leave the other
            // symbols looking unrequested and drag them into the next pass for no extra data.
            var bars = rows.Values.Where(b => allowed.Contains(b.Symbol)).ToList();
            await _repository.SaveDailySessionAsync(date, bars, allowed, ct);
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

    /// <summary>
    /// The most recent trading day whose candles are final.
    ///
    /// <para>
    /// Today counts only once the market is closed AND the exchange has had time to publish the
    /// settled table (<see cref="TradingScanOptions.ArchiveSettleAfterPkt"/>, default 17:30 PKT — after
    /// the latest scheduled close of 16:30 on Friday). Otherwise the cutoff steps back a day. Weekend
    /// dates are left in place because <see cref="Weekdays"/> filters them out anyway.
    /// </para>
    /// </summary>
    public DateOnly LastSettledSession(DateTime? utcNow = null)
    {
        var pktNow = PsxTime.Now(utcNow);
        var today = DateOnly.FromDateTime(pktNow);
        var settleAfter = ParseSettleTime(_options.Value.Scan.ArchiveSettleAfterPkt);
        var status = _calendar.GetStatus(utcNow);

        var settledToday = !status.IsOpen && TimeOnly.FromDateTime(pktNow) >= settleAfter;
        return settledToday ? today : today.AddDays(-1);
    }

    private static TimeOnly ParseSettleTime(string? configured) =>
        TimeOnly.TryParse(configured, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : new TimeOnly(17, 30);

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
