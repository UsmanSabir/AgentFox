using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Analysis;
using TradingAgent.Config;
using TradingAgent.Manager;
using TradingAgent.Market;
using TradingAgent.Models;
using TradingAgent.Observability;
using TradingAgent.Persistence;
using TradingAgent.Reconciliation;
using TradingAgent.Research;
using TradingAgent.Risk;
using TradingAgent.Trading;

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
public sealed class WatchlistMonitorWorker : BackgroundService, IMarketSessionOpenParticipant
{
    public string Name => "watchlist and armed orders";
    public int Order => 400;

    /// <summary>Let the app finish starting before the first pass; monitoring is never urgent at t=0.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    /// <summary>Poll interval while the market is closed, purely to notice that it has opened.</summary>
    private static readonly TimeSpan ClosedPoll = TimeSpan.FromMinutes(5);

    private readonly MonitoredUniverse _universe;
    private readonly CandleHistoryProvider _history;
    private readonly PsxDataClient _dataClient;
    private readonly CompositeLiveQuoteSource _quotes;
    private readonly ITradingRepository _repository;
    private readonly IMarketCalendar _calendar;
    private readonly AlertBroadcaster _broadcaster;
    private readonly ApprovalGate _approvals;
    private readonly TradingAgent.Manager.TradingManager _manager;
    private readonly PersistentOrderWorker _persistentOrders;
    private readonly TradingReconciliationState _reconciliation;
    private readonly TradingAgent.Broker.IBrokerOutstandingOrdersReader _outstandingReader;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<WatchlistMonitorWorker> _logger;
    private readonly TradingActivityLog? _activity;

    private readonly object _statusLock = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private MonitorStatus _status;

    public WatchlistMonitorWorker(
        MonitoredUniverse universe,
        CandleHistoryProvider history,
        PsxDataClient dataClient,
        CompositeLiveQuoteSource quotes,
        ITradingRepository repository,
        IMarketCalendar calendar,
        AlertBroadcaster broadcaster,
        ApprovalGate approvals,
        TradingAgent.Manager.TradingManager manager,
        PersistentOrderWorker persistentOrders,
        TradingReconciliationState reconciliation,
        TradingAgent.Broker.IBrokerOutstandingOrdersReader outstandingReader,
        IOptions<TradingAgentOptions> options,
        ILogger<WatchlistMonitorWorker> logger,
        TradingActivityLog? activity = null)
    {
        _activity = activity;
        _universe = universe;
        _history = history;
        _dataClient = dataClient;
        _quotes = quotes;
        _repository = repository;
        _calendar = calendar;
        _broadcaster = broadcaster;
        _approvals = approvals;
        _manager = manager;
        _persistentOrders = persistentOrders;
        _reconciliation = reconciliation;
        _outstandingReader = outstandingReader;
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
                    await SweepProposalsAsync(stoppingToken);
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
        await _runGate.WaitAsync(ct);
        try { return await RunPassCoreAsync(trigger, ct); }
        finally { _runGate.Release(); }
    }

    public async Task RunAtMarketOpenAsync(MarketSessionOpenContext context, CancellationToken ct) =>
        await RunPassAsync("market-open", ct);

    private async Task<MonitorStatus> RunPassCoreAsync(string trigger, CancellationToken ct)
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
        var history = await _history.GetDailyAsync(symbols, sessions, includeLive: true, ct: ct);

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

        // Armed triggers are evaluated against the SAME snapshot the alerts came from, so an event
        // trigger sees exactly the alerts this pass raised — no second fetch, no drift between what
        // was detected and what fires on it.
        await EvaluateArmedOrdersAsync(history.Live, raised, ct);

        if (suppressed > 0)
            _logger.LogWarning(
                "[WatchlistMonitor] {Count} alert(s) suppressed by the per-pass cap of {Cap}. "
                + "They were NOT raised; raise Monitor.MaxAlertsPerPass if this recurs.",
                suppressed, options.MaxAlertsPerPass);

        var elapsed = DateTime.UtcNow - started;

        // Only reported when the pass DID something. A line every 30 seconds saying "0 alerts" would
        // push everything that matters off the panel within a couple of minutes.
        if (raised.Count > 0 || suppressed > 0)
            _activity?.Record(
                suppressed > 0 ? ActivityLevel.Warn : ActivityLevel.Info, "Monitor",
                $"Monitoring pass raised {raised.Count} alert(s) across {analyzed} symbol(s)",
                suppressed > 0 ? $"{suppressed} suppressed by the per-pass cap." : null);

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

    /// <summary>
    /// Evaluates every armed order against this pass's prices and alerts, and submits the ones whose
    /// condition is met.
    ///
    /// <para>
    /// Three properties matter here. The trigger is claimed with a compare-and-set BEFORE the broker is
    /// touched, so a slow submission overlapping the next pass cannot fire it twice. Approval is asked
    /// for explicitly through <see cref="ApprovalGate"/> — an armed order fires with nobody watching, so
    /// it must either be pre-authorised by policy or refuse to send. And a refusal returns the order to
    /// <c>armed</c> rather than consuming it, because "the market just closed" should not silently
    /// disarm a protective stop.
    /// </para>
    /// </summary>
    private async Task EvaluateArmedOrdersAsync(
        IReadOnlyDictionary<string, PsxLiveQuote> live,
        IReadOnlyList<AlertRecord> raisedThisPass,
        CancellationToken ct)
    {
        IReadOnlyList<ArmedOrder> armed;
        try
        {
            armed = await _repository.GetArmedOrdersAsync(armedOnly: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[ArmedOrders] Could not read armed orders this pass.");
            return;
        }

        if (armed.Count == 0) return;

        // Alerts raised this pass, indexed by symbol, for the event triggers.
        var alertsBySymbol = raisedThisPass
            .GroupBy(a => a.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyCollection<AlertKind>)g
                    .Select(a => Enum.TryParse<AlertKind>(a.Kind, out var k) ? k : (AlertKind?)null)
                    .Where(k => k is not null)
                    .Select(k => k!.Value)
                    .ToHashSet(),
                StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;

        foreach (var order in armed)
        {
            ct.ThrowIfCancellationRequested();

            // Age it out first, so an expired trigger cannot fire on a late price tick.
            if (order.ExpiresUtc is { } expiry && now >= expiry)
            {
                await _repository.TrySetArmedOrderStateAsync(
                    order.ArmedId, "armed", "expired",
                    $"Expired at {expiry:u} without triggering.", ct: ct);
                _logger.LogInformation("[ArmedOrders] {ArmedId} ({Symbol}) expired unfired.",
                    order.ArmedId, order.Symbol);
                continue;
            }

            var price = live.TryGetValue(order.Symbol, out var quote) ? quote.Current : null;
            var alerts = alertsBySymbol.GetValueOrDefault(order.Symbol, Array.Empty<AlertKind>());

            if (!ArmedOrderEvaluator.ShouldFire(order, price, alerts, now, out var why))
            {
                // Not firing — so this is the moment a trailing trigger follows the price. Done after
                // the fire check so a fire never waits on a bookkeeping write, and only when the
                // reference actually moves, which keeps a flat symbol from writing every 30 seconds.
                await TrailAsync(order, price, ct);
                continue;
            }

            // A backstop is not an ordinary armed order: it exists to cover the window where the
            // native stop does not, and must stand down the moment that stop is resting. Skipping
            // this check is exactly how "native plus a local backstop" becomes two orders selling
            // the same position — and this broker offers no way to cancel either of them.
            if (order.ProtectiveStopId is not null
                && await BackstopMustStandDownAsync(order, ct) is { } standDown)
            {
                _logger.LogInformation(
                    "[ArmedOrders] {ArmedId} ({Symbol}) met its trigger but stood down: {Why}",
                    order.ArmedId, order.Symbol, standDown);
                _activity?.Info("Armed", $"{order.Symbol}: local backstop stood down", standDown);
                continue;
            }

            // Claim it before the broker sees anything.
            if (!await _repository.TrySetArmedOrderStateAsync(
                    order.ArmedId, "armed", "firing", why, ct: ct))
                continue;

            _logger.LogWarning("[ArmedOrders] {ArmedId} ({Symbol}) triggered: {Why}",
                order.ArmedId, order.Symbol, why);
            _activity?.Info("Armed", $"{order.Symbol}: armed {order.Action} triggered", why);

            try
            {
                PersistentOrderIntent? persistent = null;
                string? quantityAdjustment = null;
                TradingSignal signal;
                string source;
                if (order.PersistentUntilFilled)
                {
                    if (PersistentOrderDecisions.ValidateEligibility(order.OrderType) is { } problem)
                    {
                        await _repository.TrySetArmedOrderStateAsync(
                            order.ArmedId, "firing", "failed", problem, ct: ct);
                        continue;
                    }

                    var effectiveQuantity = order.Quantity;
                    if (order.Action.Equals("SELL", StringComparison.OrdinalIgnoreCase))
                    {
                        var availability = SellQuantityRule.Available(
                            _reconciliation.Current,
                            order.Symbol,
                            DateTime.UtcNow,
                            TimeSpan.FromSeconds(Math.Max(
                                10, _options.Value.ReconciliationMaxAgeSeconds)));
                        if (!availability.Known)
                        {
                            await _repository.TrySetArmedOrderStateAsync(
                                order.ArmedId, "firing", "armed",
                                $"Trigger met, but SELL availability was unknown: {availability.Reason}",
                                ct: ct);
                            continue;
                        }
                        if (availability.AvailableQuantity <= 0)
                        {
                            await _repository.TrySetArmedOrderStateAsync(
                                order.ArmedId, "firing", "failed",
                                $"Trigger met, but no uncommitted {order.Symbol} shares were available to sell.",
                                ct: ct);
                            continue;
                        }

                        effectiveQuantity = Math.Min(effectiveQuantity, availability.AvailableQuantity);
                        if (effectiveQuantity != order.Quantity)
                        {
                            quantityAdjustment = new SellQuantityAdjustment(
                                0, 0, order.Symbol, order.Quantity, effectiveQuantity).Message;
                        }
                    }

                    persistent = new PersistentOrderIntent
                    {
                        // Deterministic linkage makes a crash visible and prevents a second durable
                        // instruction being invented for one armed trigger.
                        IntentId = $"armed-{order.ArmedId}",
                        Symbol = order.Symbol,
                        Action = order.Action,
                        Quantity = effectiveQuantity,
                        OrderType = order.OrderType,
                        Price = order.Price,
                        LimitPrice = order.LimitPrice,
                        ExpiresUtc = order.ExpiresUtc ?? DateTime.UtcNow.AddDays(30),
                        SourceArmedId = order.ArmedId,
                        Note = quantityAdjustment is null
                            ? order.Note
                            : string.Join(" ", new[] { order.Note, quantityAdjustment }
                                .Where(x => !string.IsNullOrWhiteSpace(x)))
                    };
                    signal = persistent.ToSignal(effectiveQuantity);
                    source = PersistentOrderWorker.BuildSource(
                        persistent.IntentId,
                        DateOnly.FromDateTime(_calendar.GetStatus().PktNow),
                        attempt: 1);
                }
                else
                {
                    signal = order.ToSignal();
                    source = $"armed:{order.ArmedId}";
                }

                var groups = new[] { (IReadOnlyList<TradingSignal>)[signal] };
                var severity = raisedThisPass
                    .FirstOrDefault(a => a.Symbol.Equals(order.Symbol, StringComparison.OrdinalIgnoreCase))
                    ?.Severity;

                var decision = _approvals.Decide(
                    groups, source,
                    new ApprovalContext(severity, "armed-order"));
                var approvalReason = decision.Reason;

                if (!decision.MayProceed)
                {
                    // Not authorised to act unattended. Re-arm rather than consume: the condition may
                    // still hold next pass, and losing a protective stop because approval was in
                    // Always mode would be the worst possible outcome of a config choice.
                    await _repository.TrySetArmedOrderStateAsync(
                        order.ArmedId, "firing", "armed",
                        $"Trigger met but not sent: {approvalReason}", ct: ct);
                    _logger.LogWarning(
                        "[ArmedOrders] {ArmedId} met its trigger but was NOT sent: {Reason} "
                        + "(it remains armed).", order.ArmedId, approvalReason);
                    _activity?.Warn("Armed",
                        $"{order.Symbol}: triggered but not sent — it stays armed", approvalReason);
                    continue;
                }

                if (persistent is not null)
                {
                    var submission = await _persistentOrders.CreateAndSubmitAsync(
                        persistent, decision.Authorization, ct);
                    await _repository.TrySetArmedOrderStateAsync(
                        order.ArmedId, "firing", "fired",
                        $"{why} Persistent order created: "
                        + (quantityAdjustment is null ? "" : quantityAdjustment + " ")
                        + submission.Reason,
                        submission.Execution?.ExecutionId, ct);
                    _logger.LogWarning(
                        "[ArmedOrders] {ArmedId} handed to persistent order {IntentId}: {Reason}",
                        order.ArmedId, persistent.IntentId, submission.Reason);
                    _activity?.Record(
                        submission.Accepted ? ActivityLevel.Info : ActivityLevel.Warn,
                        "Armed",
                        $"{order.Symbol}: trigger created a persistent {order.Action}",
                        submission.Reason);
                    continue;
                }

                var result = await _manager.ExecuteGroupsAsync(
                    groups, source, decision.Authorization, ct);

                await _repository.TrySetArmedOrderStateAsync(
                    order.ArmedId, "firing",
                    result.Executed ? "fired" : "armed",
                    result.Executed
                        ? $"{why} {approvalReason}"
                        : $"Trigger met but execution refused: {result.Reason}",
                    string.IsNullOrWhiteSpace(result.ExecutionId) ? null : result.ExecutionId, ct);

                _logger.LogWarning(
                    "[ArmedOrders] {ArmedId} {Outcome}: {Reason}",
                    order.ArmedId, result.Executed ? "FIRED" : "refused", result.Reason);
                _activity?.Record(
                    result.Executed ? ActivityLevel.Info : ActivityLevel.Warn, "Armed",
                    result.Executed
                        ? $"{order.Symbol}: armed {order.Action} fired"
                        : $"{order.Symbol}: armed {order.Action} refused",
                    result.Reason);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A thrown submission is genuinely ambiguous — it may or may not have reached the
                // broker — so it is NOT re-armed. Reconciliation owns that question.
                await _repository.TrySetArmedOrderStateAsync(
                    order.ArmedId, "firing", "failed",
                    $"Submission threw: {ex.Message}. Verify manually before re-arming.", ct: ct);
                _logger.LogError(ex, "[ArmedOrders] {ArmedId} submission failed.", order.ArmedId);
                _activity?.Error("Armed",
                    $"{order.Symbol}: submission threw — verify at the broker before re-arming",
                    ex.Message);
            }
        }
    }

    /// <summary>
    /// Moves a trailing percent trigger's reference to a new favourable extreme, if this pass produced
    /// one.
    ///
    /// <para>
    /// A failed write is logged and otherwise ignored on purpose. The reference only ever falls BEHIND
    /// the price when a ratchet is missed, which leaves the trigger where it already was — a stop that
    /// is momentarily wider than intended, never one that has moved closer to firing. Aborting the
    /// pass over it would be strictly worse: every other armed order would stop being evaluated.
    /// </para>
    /// </summary>
    private async Task TrailAsync(ArmedOrder order, decimal? price, CancellationToken ct)
    {
        if (ArmedOrderEvaluator.NextTrailReference(order, price) is not { } reference) return;
        if (PercentTrigger.Level(order.TriggerKind, reference, order.TriggerPercent) is not { } level)
            return;

        var ratchetUp = order.TriggerKind == ArmedTriggerKind.PercentDrop;

        try
        {
            if (!await _repository.TrySetArmedOrderTrailAsync(
                    order.ArmedId, reference, level, ratchetUp, ct))
                return;

            _logger.LogInformation(
                "[ArmedOrders] {ArmedId} ({Symbol}) trailed: reference {From} → {To}, "
                + "trigger now {Level} ({Percent}% {Direction}).",
                order.ArmedId, order.Symbol, order.ReferencePrice, reference, level,
                order.TriggerPercent, ratchetUp ? "below" : "above");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "[ArmedOrders] Could not trail {ArmedId} ({Symbol}); its trigger stays at {Level}.",
                order.ArmedId, order.Symbol, order.EffectiveTriggerPrice);
        }
    }

    /// <summary>
    /// Whether a protective stop's local backstop must hold fire, and why. Null means it may proceed.
    ///
    /// <para>
    /// The outstanding book is read only at this point — when a backstop has actually reached its
    /// trigger — rather than every pass, because it is a live broker read. That is a rare event, and
    /// paying one read to avoid selling a position twice is the right trade.
    /// </para>
    /// </summary>
    private async Task<string?> BackstopMustStandDownAsync(ArmedOrder order, CancellationToken ct)
    {
        var stop = (await _repository.GetProtectiveStopsAsync(openOnly: false, ct))
            .FirstOrDefault(s => s.StopId == order.ProtectiveStopId);

        if (stop is null)
            return "the protective stop it backs no longer exists";

        if (stop.State == "closed")
            return $"the protective stop it backs is closed ({stop.StateReason})";

        IReadOnlyList<RestingOrder>? resting;
        try { resting = await _outstandingReader.GetOutstandingOrdersAsync(order.Symbol, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ArmedOrders] Could not read the outstanding book before firing backstop {ArmedId}.",
                order.ArmedId);
            resting = null;   // unreadable counts as "cannot rule out a resting stop"
        }

        return ProtectiveStopDecisions.BackstopShouldStandDown(stop, resting, out var reason)
            ? reason
            : null;
    }

    /// <summary>
    /// Ages the proposal queue: expires anything past its TTL or whose stated entry has drifted too far
    /// from the live price, then prunes terminal rows past the retention window.
    ///
    /// <para>
    /// This is what keeps the inbox honest. A proposal is a plan priced at a moment; left alone it
    /// accumulates forever and eventually offers a level that no longer exists as though it were
    /// current. Expiry is a state change WITH A REASON rather than a delete, so the audit trail
    /// survives — only retention actually removes rows, and only terminal ones.
    /// </para>
    /// </summary>
    private async Task SweepProposalsAsync(CancellationToken ct)
    {
        var options = _options.Value.Proposals;

        try
        {
            var open = await _repository.GetOpenProposalsAsync(ct);
            if (open.Count == 0) return;

            var ttlCutoff = DateTime.UtcNow.AddHours(-Math.Max(1, options.TtlHours));
            var drift = options.InvalidateOnDriftPercent;

            // One market snapshot for the whole sweep, not one per proposal.
            IReadOnlyDictionary<string, PsxLiveQuote> live = new Dictionary<string, PsxLiveQuote>();
            if (drift > 0)
            {
                try { live = (await _quotes.GetQuotesAsync(ct)).Quotes; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Without prices only the TTL rule can be applied — which is the safe subset, so
                    // carry on rather than skipping the sweep entirely.
                    _logger.LogWarning(ex,
                        "[Proposals] Live prices unavailable; expiring on age only this pass.");
                }
            }

            var expired = 0;
            foreach (var proposal in open)
            {
                ct.ThrowIfCancellationRequested();

                string? reason = null;
                if (proposal.CreatedUtc < ttlCutoff)
                    reason = $"Not acted on within {options.TtlHours}h of being raised.";
                else if (drift > 0 && DriftedTooFar(proposal, live, drift) is { } drifted)
                    reason = drifted;

                if (reason is null) continue;

                // 'executing' is deliberately not swept: something is mid-flight against the broker and
                // expiring it underneath that would race a live submission.
                if (proposal.Status == "executing") continue;

                if (await _repository.TrySetProposalStateAsync(
                        proposal.ProposalId, proposal.Status, "expired", reason, ct: ct))
                    expired++;
            }

            if (expired > 0)
                _logger.LogInformation("[Proposals] Expired {Count} stale proposal(s).", expired);

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[Proposals] Sweep failed; retrying after the next close.");
        }
    }

    /// <summary>
    /// Returns a reason when the proposal's stated entry has moved more than
    /// <paramref name="maxDriftPercent"/> from the live price, or null when it is still current.
    /// </summary>
    private static string? DriftedTooFar(
        TradeProposalRecord proposal,
        IReadOnlyDictionary<string, PsxLiveQuote> live,
        decimal maxDriftPercent)
    {
        if (!proposal.Proposal.TryGetProperty("orders", out var orders)
            || orders.ValueKind != System.Text.Json.JsonValueKind.Array)
            return null;

        foreach (var order in orders.EnumerateArray())
        {
            if (order.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            if (!order.TryGetProperty("symbol", out var s) || s.ValueKind != System.Text.Json.JsonValueKind.String)
                continue;

            decimal? entry = null;
            foreach (var key in new[] { "entry_price", "entryPrice", "price" })
            {
                if (order.TryGetProperty(key, out var p)
                    && p.ValueKind == System.Text.Json.JsonValueKind.Number
                    && p.TryGetDecimal(out var parsed)) { entry = parsed; break; }
            }

            if (entry is not > 0) continue;
            if (!live.TryGetValue(s.GetString() ?? "", out var quote)) continue;
            if (quote.Current is not > 0) continue;

            var moved = Math.Abs(quote.Current.Value - entry.Value) / entry.Value * 100m;
            if (moved > maxDriftPercent)
                return $"Stated entry {entry} for {s.GetString()} has drifted "
                     + $"{Math.Round(moved, 2)}% from the live price {quote.Current} "
                     + $"(limit {maxDriftPercent}%); the plan is no longer current.";
        }

        return null;
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
