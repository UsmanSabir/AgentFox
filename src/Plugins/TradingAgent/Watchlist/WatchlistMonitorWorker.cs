using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Analysis;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Persistence;
using TradingAgent.Research;

namespace TradingAgent.Watchlist;

/// <summary>
/// Watches the whole watchlist for level and trend transitions, and raises alerts.
///
/// <para>
/// <b>The cost model is what makes this viable.</b> A pass costs ONE market-wide request — PSX serves
/// candles by DATE, covering every symbol, and the live market watch is a single snapshot — plus local
/// archive reads. So 100 watched symbols cost the same as 5, and the pass rate is limited by the
/// portal's patience rather than by universe size. Nothing in this loop may fetch per symbol; doing so
/// would turn a 2-minute cadence into a rate-limit incident.
/// </para>
///
/// <para>
/// It raises alerts and nothing else. Execution stays behind the execution mode, the risk engine, and
/// the kill switch — a monitor that could place orders would be a different, far more dangerous
/// component.
/// </para>
/// </summary>
public sealed class WatchlistMonitorWorker : BackgroundService
{
    /// <summary>Let the app finish starting before the first pass; monitoring is never urgent at t=0.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    /// <summary>Poll interval while the market is closed, purely to notice that it has opened.</summary>
    private static readonly TimeSpan ClosedPoll = TimeSpan.FromMinutes(5);

    private readonly MonitoredUniverse _universe;
    private readonly CandleHistoryProvider _history;
    private readonly ITradingRepository _repository;
    private readonly IMarketCalendar _calendar;
    private readonly AlertBroadcaster _broadcaster;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<WatchlistMonitorWorker> _logger;

    private readonly object _statusLock = new();
    private MonitorStatus _status;

    public WatchlistMonitorWorker(
        MonitoredUniverse universe,
        CandleHistoryProvider history,
        ITradingRepository repository,
        IMarketCalendar calendar,
        AlertBroadcaster broadcaster,
        IOptions<TradingAgentOptions> options,
        ILogger<WatchlistMonitorWorker> logger)
    {
        _universe = universe;
        _history = history;
        _repository = repository;
        _calendar = calendar;
        _broadcaster = broadcaster;
        _options = options;
        _logger = logger;

        // Seeded from configuration rather than left at zero until the first pass. These values exist
        // so a reader can answer "why did it not alert"; reporting "every 0s, 0 confirming passes"
        // before the first pass would answer it wrongly, which is worse than not answering.
        var monitor = options.Value.Monitor;
        _status = new MonitorStatus
        {
            Enabled         = monitor.Enabled,
            IntervalSeconds = Math.Clamp(monitor.IntervalSeconds, 30, 3600),
            ConfirmPasses   = Math.Clamp(monitor.ConfirmPasses, 1, 10),
            Message = monitor.Enabled
                ? "No monitoring pass has run yet."
                : "Monitoring is disabled (Monitor.Enabled = false)."
        };
    }

    public MonitorStatus Status
    {
        get { lock (_statusLock) return _status; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var monitor = _options.Value.Monitor;
        if (!monitor.Enabled)
        {
            SetStatus(s => s with { Enabled = false, Message = "Monitoring is disabled (Monitor.Enabled = false)." });
            _logger.LogInformation("[WatchlistMonitor] Disabled by configuration.");
            return;
        }

        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        // Tracks whether the post-close settle pass has already run for a given session, so it happens
        // once per day rather than on every closed-market poll.
        DateOnly? settledFor = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.Value.Monitor;
            var market = _calendar.GetStatus();
            var today = PsxTime.Today();
            var interval = TimeSpan.FromSeconds(Math.Clamp(options.IntervalSeconds, 30, 3600));

            try
            {
                if (market.IsOpen)
                {
                    settledFor = null;
                    await RunPassAsync("open", stoppingToken);
                }
                else if (options.RunAfterClose && settledFor != today)
                {
                    // One pass after the close, on the day's settled bars — the in-session passes all
                    // ran against a forming candle.
                    settledFor = today;
                    await RunPassAsync("post-close", stoppingToken);
                    await PruneAsync(stoppingToken);
                }
                else
                {
                    SetStatus(s => s with
                    {
                        MarketOpen = false,
                        Message = $"Market closed — {market.Reason}. Monitoring resumes at the next open."
                    });
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WatchlistMonitor] Pass failed; retrying next cycle.");
                SetStatus(s => s with { Message = $"Last pass failed: {ex.Message}" });
            }

            try { await Task.Delay(market.IsOpen ? interval : ClosedPoll, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Runs one detection pass. Public so the API can trigger it on demand.</summary>
    public async Task<MonitorStatus> RunPassAsync(string trigger, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var options = _options.Value.Monitor;
        var thresholds = MonitorThresholds.From(_options.Value);
        var technicalOptions = TechnicalOptions.From(_options.Value.Scan);

        var watchlist = await _repository.GetWatchlistAsync(ct);
        // A muted symbol is still analyzed — its state must stay current, or unmuting would produce a
        // burst of stale transitions — but nothing it produces is raised.
        var muted = watchlist.Entries
            .Where(e => !e.AlertsEnabled)
            .Select(e => e.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var symbols = await _universe.ForMonitoringAsync(ct);
        if (symbols.Count == 0)
        {
            SetStatus(s => s with
            {
                LastPassUtc = started, SymbolsCovered = 0, AlertsRaised = 0,
                Message = "Nothing to monitor: the watchlist and AllowedSymbols are both empty."
            });
            return Status;
        }

        // ONE market-wide load for every symbol. Deep enough for weekly structure, served from the
        // archive with only missing dates reaching the portal.
        var sessions = Math.Max(
            _options.Value.Scan.LookbackDays,
            Math.Clamp(_options.Value.Scan.WeeklyLookbackWeeks, 12, 600) * 6);
        var history = await _history.GetDailyAsync(symbols, sessions, includeLive: true, ct);

        var states = await _repository.GetMonitorStatesAsync(ct);
        var cooldownStart = options.CooldownMinutes > 0
            ? DateTime.UtcNow.AddMinutes(-options.CooldownMinutes)
            // 0 means "the rest of the session": anchor the window at today's PKT midnight.
            : PsxTime.Today().ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var raised = new List<AlertRecord>();
        var suppressed = 0;
        var analyzed = 0;
        var today = PsxTime.Today();

        foreach (var symbol in symbols)
        {
            ct.ThrowIfCancellationRequested();
            if (!history.Series.TryGetValue(symbol, out var candles)
                || candles.Count < TechnicalAnalyzer.MinimumBars)
                continue;

            analyzed++;
            var lookback = candles.TakeLast(_options.Value.Scan.LookbackDays).ToList();
            var snapshot = TechnicalAnalyzer.Analyze(symbol, lookback, technicalOptions);
            var multi = MultiTimeframeAnalyzer.Analyze(
                symbol, candles, technicalOptions, _options.Value.Scan.ConfluenceTolerancePercent);

            var previous = states.GetValueOrDefault(symbol) ?? AlertDetector.Seed(symbol);
            var detection = AlertDetector.Detect(previous, snapshot, multi, thresholds);
            await _repository.SaveMonitorStateAsync(detection.NextState, ct);

            if (muted.Contains(symbol)) continue;

            foreach (var alert in detection.Fired)
            {
                if (raised.Count >= Math.Max(1, options.MaxAlertsPerPass))
                {
                    suppressed++;
                    continue;
                }

                // Durable cooldown, so a restart cannot re-announce what was already said.
                if (await _repository.HasRecentAlertAsync(
                        alert.Symbol, alert.Kind, alert.LevelPrice, cooldownStart, ct))
                    continue;

                var id = await _repository.SaveAlertAsync(alert, today, ct);
                var record = new AlertRecord
                {
                    AlertId         = id,
                    Symbol          = alert.Symbol,
                    Kind            = alert.Kind.ToString(),
                    Severity        = alert.Severity.ToString(),
                    LevelPrice      = alert.LevelPrice,
                    Price           = alert.Price,
                    Interval        = alert.Interval,
                    Summary         = alert.Summary,
                    Reasons         = alert.Reasons,
                    WeeklyConfirmed = alert.WeeklyConfirmed,
                    FromLiveBar     = alert.FromLiveBar,
                    State           = "new",
                    RaisedUtc       = DateTime.UtcNow,
                    SessionDate     = today.ToString("yyyy-MM-dd")
                };
                raised.Add(record);
                _broadcaster.Publish(record);

                _logger.LogInformation("[WatchlistMonitor] {Severity} {Kind} {Symbol}: {Summary}",
                    alert.Severity, alert.Kind, alert.Symbol, alert.Summary);
            }
        }

        if (suppressed > 0)
            _logger.LogWarning(
                "[WatchlistMonitor] {Count} alert(s) suppressed by the per-pass cap of {Cap}. "
                + "They were NOT raised; raise Monitor.MaxAlertsPerPass if this recurs.",
                suppressed, options.MaxAlertsPerPass);

        var elapsed = DateTime.UtcNow - started;
        SetStatus(_ => new MonitorStatus
        {
            Enabled        = true,
            MarketOpen     = _calendar.GetStatus().IsOpen,
            LastPassUtc    = started,
            LastPassMs     = (long)elapsed.TotalMilliseconds,
            SymbolsCovered = analyzed,
            AlertsRaised   = raised.Count,
            AlertsSuppressed = suppressed,
            IntervalSeconds = Math.Clamp(options.IntervalSeconds, 30, 3600),
            ConfirmPasses  = thresholds.ConfirmPasses,
            Trigger        = trigger,
            Warnings       = history.Warnings,
            Message = $"Analyzed {analyzed} symbol(s) in {elapsed.TotalSeconds:F1}s; "
                    + $"{raised.Count} alert(s) raised."
                    + (suppressed > 0 ? $" {suppressed} suppressed by the per-pass cap." : "")
        });

        return Status;
    }

    /// <summary>Drops alert history past the retention window so the table has a ceiling.</summary>
    private async Task PruneAsync(CancellationToken ct)
    {
        var days = _options.Value.Monitor.RetentionDays;
        if (days <= 0) return;

        try
        {
            var removed = await _repository.PruneAlertsAsync(DateTime.UtcNow.AddDays(-days), ct);
            if (removed > 0)
                _logger.LogInformation("[WatchlistMonitor] Pruned {Count} alert(s) older than {Days} days.",
                    removed, days);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WatchlistMonitor] Alert pruning failed; retrying tomorrow.");
        }
    }

    private void SetStatus(Func<MonitorStatus, MonitorStatus> update)
    {
        lock (_statusLock) _status = update(_status);
    }
}

/// <summary>What the monitor is doing, and with which settings — so "why did it not alert" is answerable.</summary>
public sealed record MonitorStatus
{
    public bool Enabled { get; init; } = true;
    public bool MarketOpen { get; init; }
    public DateTime? LastPassUtc { get; init; }
    public long LastPassMs { get; init; }
    public int SymbolsCovered { get; init; }
    public int AlertsRaised { get; init; }
    public int AlertsSuppressed { get; init; }
    public int IntervalSeconds { get; init; }
    public int ConfirmPasses { get; init; }
    public string? Trigger { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string Message { get; init; } = "";
}
