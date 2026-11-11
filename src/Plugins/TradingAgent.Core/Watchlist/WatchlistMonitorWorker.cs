using System.Collections.Concurrent;
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
    private readonly TradingAgent.Broker.IBrokerOutstandingOrdersReader _outstandingReader;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<WatchlistMonitorWorker> _logger;
    private readonly TradingActivityLog? _activity;
    private readonly IProtectiveStopReleaser? _stopReleaser;
    private readonly SellAvailabilityConfirmer _sellAvailability;
    private readonly RecentPriceWindow _recentPrices;

    /// <summary>
    /// When a triggered order that was refused may be tried again, and how many times in a row it has
    /// been refused. Keyed on the armed id. It began as the SELL-with-no-free-shares backoff below and
    /// since 2026-11-10 covers every refusal that returns an order to "armed" — a BUY as well, and a
    /// refusal by the approval gate or by the broker — because each of those repeated on every pass.
    ///
    /// <para>
    /// <b>Why it is needed even after ArmedSellOutlook.</b> That rule RETIRES an order whose position
    /// is gone. It deliberately leaves armed the case where the shares exist and are all committed to
    /// something resting — which clears when that commitment does, so the order must survive. But when
    /// the commitment is an order placed elsewhere (the broker's own mobile app, say) nothing here can
    /// stand it down, and the trigger simply keeps holding: MEASURED 2026-09-11, an armed exit was
    /// re-evaluated three times in seventeen seconds, because PriceTriggerWatcher nudges this worker on
    /// ticks rather than on a timer.
    /// </para>
    ///
    /// <para>
    /// <b>In memory, not durable, and that is the conservative direction.</b> A restart forgets the
    /// backoff and retries at once, which costs one broker read and is exactly what an operator who
    /// just restarted expects. Bounded by the number of armed orders and swept when each one leaves
    /// the loop, so it cannot grow (§0.1).
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, (DateTime NextUtc, int Failures)> _refusedFires =
        new(StringComparer.Ordinal);

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
        SellAvailabilityConfirmer sellAvailability,
        TradingAgent.Broker.IBrokerOutstandingOrdersReader outstandingReader,
        IOptions<TradingAgentOptions> options,
        ILogger<WatchlistMonitorWorker> logger,
        TradingActivityLog? activity = null,
        IProtectiveStopReleaser? stopReleaser = null,
        RecentPriceWindow? recentPrices = null)
    {
        _recentPrices = recentPrices ?? new RecentPriceWindow();
        _activity = activity;
        _stopReleaser = stopReleaser;
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
        _sellAvailability = sellAvailability;
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

    /// <summary>
    /// Ends every order a previous run left in <c>firing</c>.
    ///
    /// <para>
    /// <c>firing</c> is the claim taken before the broker is touched, and it is released by whatever
    /// the submission returns. A process stopped in between never releases it — and nothing else ever
    /// looked: every reader, the expiry sweep and the panel's default view ask for <c>armed</c> rows
    /// only, so the order simply vanished and stayed claimed for good.
    /// </para>
    ///
    /// <para>
    /// <b>To <c>failed</c>, never back to <c>armed</c>.</b> Nobody knows whether the submission reached
    /// the broker, which is the same state as a submission that threw, and gets the same answer: a
    /// person checks the broker's book before arming it again. Re-arming would let a timer send an
    /// order that may already be resting.
    /// </para>
    /// </summary>
    internal async Task RecoverInterruptedFiresAsync(CancellationToken ct)
    {
        await _runGate.WaitAsync(ct);
        try
        {
            var interrupted = (await _repository.GetArmedOrdersAsync(armedOnly: false, ct))
                .Where(order => order.State == "firing")
                .ToList();

            foreach (var order in interrupted)
            {
                const string reason =
                    "This order was being submitted when AgentFox stopped, so whether it reached the "
                    + "broker is unknown. Check the broker's order book before arming it again.";
                if (!await _repository.TrySetArmedOrderStateAsync(
                        order.ArmedId, "firing", "failed", reason, ct: ct))
                    continue;

                _logger.LogError(
                    "[ArmedOrders] {ArmedId} ({Symbol} {Action}) was left mid-submission by a previous "
                    + "run and is now failed. Verify it at the broker.",
                    order.ArmedId, order.Symbol, order.Action);
                _activity?.Error("Armed",
                    $"{order.Symbol}: armed {order.Action} was interrupted mid-submission — check the broker",
                    reason);
            }
        }
        finally { _runGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Before the enabled check and before any delay: an interrupted order is stuck whether or not
        // monitoring is on, and nothing in this process can be firing one yet.
        try { await RecoverInterruptedFiresAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ArmedOrders] Could not check for orders interrupted mid-submission.");
        }

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

                    // No prices and no broker call: hands back a take-profit fired ahead of its limit
                    // once the session is over, so the next open does not re-send it blind. The post-
                    // close pass may run before the persistent worker has noticed the day ended, so
                    // this closed-market poll is where the hand-back actually lands.
                    await RunPullbacksAsync(stoppingToken);
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
        // WHICH alerts hit the cap, not just how many. A bare count cannot be acted on: the operator
        // has no way to know whether the twenty that were dropped were the twenty that mattered.
        // Bounded so a market-wide move cannot turn one log line into a wall of text — the cap is a
        // circuit breaker for exactly that case, and its report must not undo it.
        var suppressedDetail = new List<string>();
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
            var detection = AlertDetector.Detect(
                previous, snapshot, multi, thresholds, DateTime.UtcNow);
            await _repository.SaveMonitorStateAsync(detection.NextState, ct);

            if (muted.Contains(symbol)) continue;

            foreach (var alert in detection.Fired)
            {
                if (raised.Count >= Math.Max(1, options.MaxAlertsPerPass))
                {
                    suppressed++;
                    if (suppressedDetail.Count < 20)
                        suppressedDetail.Add($"{alert.Symbol} {alert.Kind}");
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

        // Against the same prices, straight after: a take-profit fired ahead of its limit is pulled
        // back here once the market turns. See ArmedOrderPullback.
        await EvaluatePullbacksAsync(history.Live, ct);

        // Named, and built once for both the log and the activity row so the two cannot disagree.
        // Note these are alerts that reached the CAP — some might also have been inside their cooldown
        // and gone unraised anyway, because the cap is checked first on purpose: doing the durable
        // cooldown read first would spend a database read per alert in exactly the market-wide storm
        // the cap exists to survive.
        var suppressedNames = suppressed > 0
            ? string.Join(", ", suppressedDetail)
              + (suppressed > suppressedDetail.Count
                  ? $" and {suppressed - suppressedDetail.Count} more"
                  : "")
            : "";

        if (suppressed > 0)
            _logger.LogWarning(
                "[WatchlistMonitor] {Count} alert(s) reached the per-pass cap of {Cap} and were NOT "
                + "raised: {Names}. Raise Monitor.MaxAlertsPerPass if this recurs.",
                suppressed, options.MaxAlertsPerPass, suppressedNames);

        var elapsed = DateTime.UtcNow - started;

        // Only reported when the pass DID something. A line every 30 seconds saying "0 alerts" would
        // push everything that matters off the panel within a couple of minutes.
        if (raised.Count > 0 || suppressed > 0)
            _activity?.Record(
                suppressed > 0 ? ActivityLevel.Warn : ActivityLevel.Info, "Monitor",
                $"Monitoring pass raised {raised.Count} alert(s) across {analyzed} symbol(s)",
                suppressed > 0
                    ? $"{suppressed} reached the per-pass cap of {options.MaxAlertsPerPass} and were "
                      + $"not raised: {suppressedNames}."
                    : null);

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

        // Read once for the whole batch rather than per order: it is the same answer for every one of
        // them. Every date-bound order consults it, including a price trigger that starts on a future
        // date, so reaching both conditions while the board is shut cannot queue a market order for
        // the next opening auction.
        var marketIsOpen = _calendar.GetStatus().IsOpen;

        RecordWindowedPrices(armed, live, now, marketIsOpen);

        foreach (var order in armed)
        {
            ct.ThrowIfCancellationRequested();

            // Age it out first, so an expired trigger cannot fire on a late price tick.
            if (order.ExpiresUtc is { } expiry && now >= expiry)
            {
                ClearRefusal(order.ArmedId);
                await _repository.TrySetArmedOrderStateAsync(
                    order.ArmedId, "armed", "expired",
                    $"Expired at {expiry:u} without triggering.", ct: ct);
                _logger.LogInformation("[ArmedOrders] {ArmedId} ({Symbol}) expired unfired.",
                    order.ArmedId, order.Symbol);
                continue;
            }

            var price = live.TryGetValue(order.Symbol, out var quote) ? quote.Current : null;
            var alerts = alertsBySymbol.GetValueOrDefault(order.Symbol, Array.Empty<AlertKind>());
            var windowReference = order.IsWindowed
                ? _recentPrices.Extreme(order.Symbol, order.TriggerWindowMinutes!.Value,
                    PercentTrigger.WindowUsesHigh(order.TriggerKind))
                : null;

            var outcome = ArmedOrderEvaluator.Evaluate(
                order, price, alerts, now, out var why, marketIsOpen, windowReference);

            if (outcome == ArmedOrderOutcome.SkipMove)
            {
                // A bounded trail met its trigger outside its price range. Nothing is sent; the move is
                // locked out until a strictly new extreme. No ratchet either: a price on the far side
                // of the trigger is by definition not a new low (or high).
                await SkipTrailMoveAsync(order, why, ct);
                continue;
            }

            if (outcome != ArmedOrderOutcome.Fire)
            {
                // Not firing — so this is the moment a trailing trigger follows the price. Done after
                // the fire check so a fire never waits on a bookkeeping write, and only when the
                // reference actually moves, which keeps a flat symbol from writing every 30 seconds.
                //
                // Not before its start date: an order that begins watching on the 22nd must measure
                // its move from the 22nd, not arrive there anchored to a high from the week before.
                if (order.ActiveFromUtc is not { } activeFrom || now >= activeFrom)
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

            // An order that was refused waits before it is tried again — a SELL with no free shares, a
            // BUY or SELL a gate or the broker said no to. Placed BEFORE the claim, so a held order
            // costs no state write, no broker read and no log line — the whole sequence behind one
            // triggered SELL is five SOAP calls for the availability confirmation and five more at the
            // execution boundary.
            if (_refusedFires.TryGetValue(order.ArmedId, out var held) && now < held.NextUtc)
                continue;

            // Claim it before the broker sees anything.
            if (!await _repository.TrySetArmedOrderStateAsync(
                    order.ArmedId, "armed", "firing", why, ct: ct))
                continue;

            _logger.LogWarning("[ArmedOrders] {ArmedId} ({Symbol}) triggered: {Why}",
                order.ArmedId, order.Symbol, why);
            _activity?.Info("Armed", $"{order.Symbol}: armed {order.Action} triggered", why);

            // A windowed LIMIT is priced at the level it just fired at, and a trailing LIMIT at the
            // price that met its trigger, inside its bound — see ArmedOrder.PriceAtFire. Frozen from
            // here: the persistent order below re-places this same price every session.
            var firePrice = order.PriceAtFire(windowReference, price);

            try
            {
                PersistentOrderIntent? persistent = null;
                string? quantityAdjustment = null;
                TradingSignal signal;
                string source;

                // ONE confirming availability read for the whole triggered-SELL path, taken here and
                // handed to everything below it. It used to be taken twice — once inside the stop
                // release and again inside the persistence branch — which on the deployed wiring is
                // ten SOAP calls over the account's single session for one trigger.
                //
                // It answers the question that has to come first: whether this order can EVER fill.
                // An armed SELL is a standing instruction with a ten-day expiry and nothing but the
                // operator's Disarm button ever ended one early, so an exit that outlived its position
                // went on triggering against an empty holding until somebody noticed. MEASURED
                // 2026-09-11 on CNERGY: 29 triggers and 29 identical refusals in 53 minutes. See
                // ArmedSellOutlook for the incident and for why only a BROKER-CONFIRMED zero HOLDING
                // may retire an order.
                ConfirmedSellAvailability? sellAvailability = null;
                if (order.Action.Equals("SELL", StringComparison.OrdinalIgnoreCase))
                {
                    sellAvailability = await _sellAvailability.ForSellAsync(
                        order.Symbol, order.Quantity, ct);

                    var outlook = ArmedSellOutlook.For(
                        sellAvailability.Decision, sellAvailability.BrokerConfirmed, order.Symbol);
                    if (outlook.Disposition == ArmedSellDisposition.Retire)
                    {
                        // "expired", the same terminal state an unfired order ages into, rather than
                        // "failed": nothing went wrong here and nothing needs verifying at the broker.
                        // The position was closed by something else and this instruction is spent.
                        ClearRefusal(order.ArmedId);
                        await _repository.TrySetArmedOrderStateAsync(
                            order.ArmedId, "firing", "expired", outlook.Reason, ct: ct);
                        _logger.LogWarning(
                            "[ArmedOrders] {ArmedId} ({Symbol}) retired unfilled: the account holds "
                            + "no {Symbol} shares. {Why}", order.ArmedId, order.Symbol, order.Symbol,
                            outlook.Reason);
                        _activity?.Warn("Armed",
                            $"{order.Symbol}: armed {order.Action} retired — the position is gone",
                            outlook.Reason);
                        continue;
                    }
                }

                // A SELL blocked by our OWN protective stop is asked for its shares HERE, before the
                // persistence branch, because it has nothing to do with persistence.
                //
                // It used to live inside the PersistentUntilFilled branch only, which made whether a
                // triggered exit could execute depend on a flag about how the order is retried. An
                // ordinary armed trailing SELL over a fully committed holding would trigger, find zero
                // free shares, go back to "armed", and do that for the rest of the day while the stop
                // it was meant to tighten sat there holding every share. Nothing said why. Seen live
                // on 2026-09-02: QTECH carried a stop at 36 and a trailing exit at 42.60 over the same
                // 500 shares, and PRL a stop at 100 and a trailing exit at 102.92 over the same 200.
                //
                // Safe on both paths because the releaser matches the stop by the order number this
                // system recorded when it placed it, not by the armed order asking: an unreadable
                // book, an unreadable quantity, or shares held by somebody else's order all come back
                // "not released", and the caller leaves the sell armed rather than forcing anything.
                //
                // DECIDED here, DONE after approval. Standing a stop down is the one step on this path
                // that removes protection, and it used to run before the approval gate was asked — so a
                // sell the gate then refused (approval mode Always, no window open, the auto cap used
                // up, the kill switch) went back to "armed" with its stop already cancelled, and did
                // the same again on the next trigger once the stop had been re-placed. The order is
                // built below on the assumption the release will succeed, and the release itself
                // happens only once the gate has said yes.
                var needsRelease = _stopReleaser is not null
                    && sellAvailability is { MustDeferSizing: false, Decision: { Known: true, AvailableQuantity: <= 0 } };

                if (order.PersistentUntilFilled)
                {
                    if (PersistentOrderDecisions.ValidateEligibility(order.OrderType) is { } problem)
                    {
                        await _repository.TrySetArmedOrderStateAsync(
                            order.ArmedId, "firing", "failed", problem, ct: ct);
                        continue;
                    }

                    var effectiveQuantity = order.Quantity;
                    // Pattern-matched on the read rather than on the action string, so the one place
                    // that establishes "this is a SELL and its availability has been confirmed" is the
                    // block at the top of the trigger. Testing the action a second time would let the
                    // two drift apart, which is how a null slips into a branch that cannot handle one.
                    if (sellAvailability is { } confirmed)
                    {
                        // The read taken at the top of the trigger, not a second one. It was confirmed
                        // with the broker whenever the cached figure would shrink this sell, which is
                        // the QUIET half of the 2026-09-07 stale-snapshot incident: the order still
                        // goes out, just smaller than the thesis asked for, and nothing errors. On the
                        // deployed interval that picture can be 21 minutes old. See
                        // SellAvailabilityConfirmer.
                        //
                        // Re-reading here would answer the same thing anyway — a book read just after
                        // a release still shows the released order resting, which is what the
                        // releasedForThisOrder override below exists for — so the second read bought
                        // nothing and cost five SOAP calls on the account's one session.
                        var availability = confirmed.Decision;

                        // The broker could not be asked. Carry the full quantity and let the execution
                        // boundary size it against a fresh book — the same move the released-stop branch
                        // below makes, and for the same reason. Re-arming instead would postpone a
                        // triggered exit over a broker hiccup.
                        if (confirmed.MustDeferSizing)
                        {
                            _logger.LogWarning(
                                "[ArmedOrders] {ArmedId} ({Symbol}) is sized at the execution boundary: "
                                + "{Why}", order.ArmedId, order.Symbol, confirmed.ConfirmationFailure);
                        }
                        else if (!availability.Known)
                        {
                            await _repository.TrySetArmedOrderStateAsync(
                                order.ArmedId, "firing", "armed",
                                $"Trigger met, but SELL availability was unknown: {availability.Reason} "
                                + NoteRefusal(order),
                                ct: ct);
                            continue;
                        }
                        else
                        {
                            // Our own stop holds these shares and will be asked for them once the
                            // approval gate has said yes (see needsRelease). What is left here is the
                            // consequence for SIZING, and it is specific to the persistent path: a book
                            // read before the release shows the stop's order resting, so it answers 0.
                            // Carry the full quantity and let TradingManager decide — it re-reads the
                            // broker book for exactly this reason, and it is the single execution
                            // boundary every other caller is already sized by.
                            if (availability.AvailableQuantity <= 0 && needsRelease)
                                availability = new SellAvailabilityDecision(
                                    true, order.Quantity,
                                    "A protective stop is stood down to free these shares; the final "
                                    + "size is set at the execution boundary against a fresh book.");

                            if (availability.AvailableQuantity <= 0)
                            {
                                // Back to "armed", NOT "failed". Zero free quantity is a transient fact
                                // about the order book, not a verdict on this order: at this broker a
                                // SELL is sized against custody minus resting SELLs, so a protective
                                // stop covering the whole position leaves nothing free — and that
                                // clears the moment the stop is superseded, or at the close, when the
                                // venue clears the book. Marking it failed destroys a live thesis at
                                // the exact moment it was proved right, which is why the unknown branch
                                // above already returns here.
                                //
                                // Trustworthy as of 2026-09-07: this zero was confirmed with the broker
                                // rather than read from a snapshot up to a poll interval old, so it no
                                // longer postpones an exit whose shares were freed elsewhere.
                                await _repository.TrySetArmedOrderStateAsync(
                                    order.ArmedId, "firing", "armed",
                                    $"Trigger met, but every {order.Symbol} share is committed to a "
                                    + $"resting SELL — {availability.Reason} " + NoteRefusal(order),
                                    ct: ct);
                                continue;
                            }

                            effectiveQuantity = Math.Min(
                                effectiveQuantity, availability.AvailableQuantity);
                            if (effectiveQuantity != order.Quantity)
                            {
                                quantityAdjustment = new SellQuantityAdjustment(
                                    0, 0, order.Symbol, order.Quantity, effectiveQuantity).Message;
                            }
                        }
                    }

                    persistent = new PersistentOrderIntent
                    {
                        // Deterministic linkage makes a crash visible and prevents a second durable
                        // instruction being invented for one armed trigger.
                        //
                        // Distinct per re-arm: a pulled-back order fires again under a new id,
                        // because intent ids are a primary key. See ArmedOrderPullback.IntentIdFor.
                        IntentId = ArmedOrderPullback.IntentIdFor(order),
                        Symbol = order.Symbol,
                        Action = order.Action,
                        Quantity = effectiveQuantity,
                        OrderType = order.OrderType,
                        Price = firePrice,
                        LimitPrice = order.LimitPrice,
                        ExpiresUtc = order.ExpiresUtc ?? DateTime.UtcNow.AddDays(30),
                        SourceArmedId = order.ArmedId,
                        // The handover changes the lifecycle, not whose instruction it is.
                        OperatorOriginated = order.OperatorOriginated,
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
                    signal = (order with { Price = firePrice }).ToSignal();
                    // A retry after a refusal is a NEW submission and says so. The execution boundary
                    // keys idempotency on the source and the request, and a MARKET exit's request is
                    // identical every time — so without this a retry of an order the broker refused
                    // would be answered with a replay of that refusal and never reach the broker
                    // again. Double submission is already ruled out by the armed→firing claim above,
                    // and an outcome that is UNKNOWN ends the order rather than retrying it.
                    source = _refusedFires.ContainsKey(order.ArmedId)
                        ? $"armed:{order.ArmedId}:retry-{DateTime.UtcNow:yyyyMMddHHmmss}"
                        : $"armed:{order.ArmedId}";
                }

                var groups = new[] { (IReadOnlyList<TradingSignal>)[signal] };
                var severity = raisedThisPass
                    .FirstOrDefault(a => a.Symbol.Equals(order.Symbol, StringComparison.OrdinalIgnoreCase))
                    ?.Severity;

                var decision = _approvals.Decide(
                    groups, source,
                    // An order the operator armed by hand still fires on a manual-only symbol; one a
                    // strategy armed does not. Everything else the gate weighs is unaffected.
                    new ApprovalContext(severity, "armed-order", order.OperatorOriginated));
                var approvalReason = decision.Reason;

                if (!decision.MayProceed)
                {
                    // Not authorised to act unattended. Re-arm rather than consume: the condition may
                    // still hold next pass, and losing a protective stop because approval was in
                    // Always mode would be the worst possible outcome of a config choice.
                    //
                    // Spaced by a flat minute, never the growing backoff: nothing was sent, so there
                    // is nothing to protect the broker from, and an operator who opens an approval
                    // window must not then wait half an hour for the order they opened it for. The
                    // minute is what keeps this from being logged on every pass and every tick.
                    await _repository.TrySetArmedOrderStateAsync(
                        order.ArmedId, "firing", "armed",
                        $"Trigger met but not sent: {approvalReason} "
                        + NoteRefusal(order, escalate: false), ct: ct);
                    _logger.LogWarning(
                        "[ArmedOrders] {ArmedId} met its trigger but was NOT sent: {Reason} "
                        + "(it remains armed).", order.ArmedId, approvalReason);
                    _activity?.Warn("Armed",
                        $"{order.Symbol}: triggered but not sent — it stays armed", approvalReason);
                    continue;
                }

                // Approved — only now may our own stop give up its shares. See needsRelease.
                if (needsRelease)
                {
                    if (await ReleaseOwnStopIfBlockingAsync(order, sellAvailability!, ct) is { } releaseNote)
                    {
                        _activity?.Warn("Armed",
                            $"{order.Symbol}: a protective stop stood down so this sell could go through",
                            releaseNote);
                    }
                    else if (persistent is not null)
                    {
                        // Nothing could be stood down, so every share is still committed. Back to
                        // "armed", NOT "failed": that clears when the commitment does, or at the close.
                        // (The one-shot path below carries on to the execution boundary instead, which
                        // sizes against a fresh book and refuses for itself — as it always has.)
                        await _repository.TrySetArmedOrderStateAsync(
                            order.ArmedId, "firing", "armed",
                            $"Trigger met, but every {order.Symbol} share is committed to a resting "
                            + "SELL that could not be stood down. " + NoteRefusal(order),
                            ct: ct);
                        continue;
                    }
                }

                if (persistent is not null)
                {
                    ClearRefusal(order.ArmedId);
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

                // Read by ArmedFireOutcome, not by result.Executed: that flag means the broker
                // ANSWERED, and a venue rejection is an answer. Keying on it recorded a refused exit
                // as "fired" with nothing resting and nothing retrying.
                var (disposition, outcome2) = ArmedFireOutcome.For(result);
                var executionId = string.IsNullOrWhiteSpace(result.ExecutionId) ? null : result.ExecutionId;

                switch (disposition)
                {
                    case ArmedFireDisposition.Fired:
                        ClearRefusal(order.ArmedId);
                        await _repository.TrySetArmedOrderStateAsync(
                            order.ArmedId, "firing", "fired", $"{why} {approvalReason}", executionId, ct);
                        _logger.LogWarning("[ArmedOrders] {ArmedId} FIRED: {Reason}",
                            order.ArmedId, result.Reason);
                        _activity?.Info("Armed", $"{order.Symbol}: armed {order.Action} fired", result.Reason);
                        break;

                    case ArmedFireDisposition.Rearm:
                        // Spaced out for a BUY and a SELL alike, and without asking WHY it was refused,
                        // deliberately: this is the path a trailing MARKET exit takes, it is the one
                        // that spun 29 times on 2026-09-11, and a refusal this code cannot classify is
                        // precisely the one that must not loop.
                        await _repository.TrySetArmedOrderStateAsync(
                            order.ArmedId, "firing", "armed",
                            $"Trigger met but not placed: {outcome2} {NoteRefusal(order)}",
                            executionId, ct);
                        _logger.LogWarning("[ArmedOrders] {ArmedId} refused: {Reason}",
                            order.ArmedId, outcome2);
                        _activity?.Warn("Armed",
                            $"{order.Symbol}: armed {order.Action} refused — it stays armed", outcome2);
                        break;

                    default:
                        // The broker may hold this order, or will never take it. Neither is a timer's
                        // to retry, so the order ends here and says why.
                        ClearRefusal(order.ArmedId);
                        await _repository.TrySetArmedOrderStateAsync(
                            order.ArmedId, "firing", "failed", outcome2, executionId, ct);
                        _logger.LogError("[ArmedOrders] {ArmedId} stopped: {Reason}",
                            order.ArmedId, outcome2);
                        _activity?.Error("Armed",
                            $"{order.Symbol}: armed {order.Action} stopped — check the broker", outcome2);
                        break;
                }
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
    /// Runs the pull-back rule on its own, outside a detection pass — for the closed-market poll, which
    /// has no prices and needs none. Serialised with the passes by the same gate.
    /// </summary>
    public async Task RunPullbacksAsync(CancellationToken ct = default)
    {
        await _runGate.WaitAsync(ct);
        try { await EvaluatePullbacksAsync(new Dictionary<string, PsxLiveQuote>(), ct); }
        finally { _runGate.Release(); }
    }

    /// <summary>
    /// Applies <see cref="ArmedOrderPullback"/> to every fired take-profit that carries a pull-back
    /// level: cancels the working sell when the price has fallen to that level, hands it back at the
    /// close, and re-arms it once the cancel is confirmed.
    ///
    /// <para>
    /// <b>The cancel goes through <see cref="PersistentOrderWorker.CancelAsync"/>,</b> the path that
    /// confirms the order is gone with the broker and asks the broker whether a vanished order filled
    /// (MWMP, 2026-09-21). Nothing is re-armed on anything short of that: a pull-back that re-armed over
    /// a sell still resting would put two sells over one set of shares.
    /// </para>
    ///
    /// <para>
    /// <b>Failure here is logged and otherwise ignored,</b> like a missed trail. The cost of skipping a
    /// pass is a sell that rests one pass longer — exactly what every take-profit did before this.
    /// </para>
    /// </summary>
    private async Task EvaluatePullbacksAsync(
        IReadOnlyDictionary<string, PsxLiveQuote> live, CancellationToken ct)
    {
        IReadOnlyList<ArmedOrder> watched;
        try { watched = await _repository.GetArmedOrdersWatchedForPullbackAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[ArmedOrders] Could not read the orders watched for a pull-back.");
            return;
        }

        if (watched.Count == 0) return;

        var market = _calendar.GetStatus();
        var today = DateOnly.FromDateTime(market.PktNow);
        // A later date's open, not merely "shut": Friday's midday break is shut too, and an order placed
        // that morning may still be resting through it.
        var sessionEnded = !market.IsOpen
            && market.NextOpenPkt is { } nextOpen
            && DateOnly.FromDateTime(nextOpen) > today;

        foreach (var order in watched)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var intentId = ArmedOrderPullback.IntentIdFor(order);
                var intent = await _repository.GetPersistentOrderAsync(intentId, ct);
                var price = live.TryGetValue(order.Symbol, out var quote) ? quote.Current : null;
                var decision = ArmedOrderPullback.Decide(
                    order, intent, price, market.IsOpen, sessionEnded, today);

                switch (decision.Action)
                {
                    case PullbackAction.Finish:
                        await _repository.TryEndArmedOrderPullbackAsync(order.ArmedId,
                            $"No longer watched for a pull-back: {decision.Reason}.", ct);
                        break;

                    case PullbackAction.PullBack:
                        await PullBackAsync(order, intent!, decision.Reason, ct);
                        break;

                    case PullbackAction.HandBack:
                        // The MARKER first, then the intent. A cancelled persistent order WITHOUT the
                        // marker reads as the operator's cancel, which an edition may answer by arming
                        // a fresh sell (premium does) — so an intent cancelled here must never be
                        // visible unmarked, even between two writes, or both that fresh sell and this
                        // re-arm would go out over one set of shares.
                        if (!await _repository.TryRequestArmedOrderPullbackAsync(order.ArmedId,
                                $"Handing back: {decision.Reason}.", ct))
                            break;
                        // Only from an idle state: if the persistent worker has moved it since the
                        // read, take the marker back off and let the next poll re-decide.
                        if (!await _repository.TrySetPersistentOrderStateAsync(intent!.IntentId,
                                ["active", "partial"], "cancelled",
                                "Handed back at the close: the venue cleared the book, and the take-profit "
                                + "that created this order re-arms rather than being re-sent at the open.", ct))
                        {
                            await _repository.TryWithdrawArmedOrderPullbackRequestAsync(order.ArmedId, ct);
                            break;
                        }
                        await SettlePullbackAsync(order, intentId, bounce: false, ct);
                        break;

                    case PullbackAction.Rearm:
                        // A pull-back requested on an earlier pass whose cancel has since settled, or a
                        // hand-back interrupted by a restart. Counted as a bounce — the conservative
                        // reading, since it can only bring the pull-back cap closer.
                        await SettlePullbackAsync(order, intentId, bounce: true, ct);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "[ArmedOrders] Pull-back check failed for {ArmedId} ({Symbol}); the sell stays as it is.",
                    order.ArmedId, order.Symbol);
            }
        }
    }

    private async Task PullBackAsync(
        ArmedOrder order, PersistentOrderIntent intent, string why, CancellationToken ct)
    {
        // Marked BEFORE the cancel, so a restart mid-cancel still knows this cancel was ours and re-arms
        // once it settles — rather than reading a cancelled order as the operator's and leaving it.
        if (!await _repository.TryRequestArmedOrderPullbackAsync(order.ArmedId, $"Pulling back: {why}.", ct))
            return;

        _logger.LogWarning(
            "[ArmedOrders] {ArmedId} ({Symbol}) pulling back its working SELL: {Why}",
            order.ArmedId, order.Symbol, why);
        _activity?.Warn("Armed", $"{order.Symbol}: take-profit pulled back — the price turned", why);

        var (completed, state, message) = await _persistentOrders.CancelAsync(
            intent.IntentId, ct, requestedBy: "a take-profit pull-back");
        if (!completed)
        {
            // Not confirmed gone. The marker stays, the order stays fired, and a later pass re-arms once
            // the persistent worker settles the cancel — never before.
            _logger.LogWarning(
                "[ArmedOrders] {ArmedId} ({Symbol}) pull-back cancel not confirmed ({State}): {Message}",
                order.ArmedId, order.Symbol, state, message);
            return;
        }

        await SettlePullbackAsync(order, intent.IntentId, bounce: true, ct);
    }

    private async Task SettlePullbackAsync(
        ArmedOrder order, string intentId, bool bounce, CancellationToken ct)
    {
        var intent = await _repository.GetPersistentOrderAsync(intentId, ct);
        if (intent is null || intent.State is "fulfilled" or "attention")
        {
            var why = intent is null
                ? "its persistent order could not be read back"
                : intent.State == "fulfilled"
                    ? "the sell filled before it could be pulled back"
                    : "the cancel left an outcome a person has to settle";
            await _repository.TryEndArmedOrderPullbackAsync(order.ArmedId, $"Not re-armed: {why}.", ct);
            if (intent?.State == "attention")
                _activity?.Warn("Armed", $"{order.Symbol}: take-profit pull-back needs checking", why);
            return;
        }

        if (!intent.IsTerminal) return;   // still cancelling — a later pass settles it

        if (ArmedOrderPullback.Rearm(order, intent, bounce) is not { } rearm)
        {
            await _repository.TryEndArmedOrderPullbackAsync(order.ArmedId,
                "Not re-armed: every share was sold before it came back.", ct);
            return;
        }

        if (!await _repository.TryRearmAfterPullbackAsync(order.ArmedId, rearm, ct)) return;

        _logger.LogWarning(
            "[ArmedOrders] {ArmedId} ({Symbol}) re-armed: SELL {Qty} fires at {Trigger}. {Reason}",
            order.ArmedId, order.Symbol, rearm.Quantity, rearm.TriggerPrice, rearm.Reason);
        _activity?.Info("Armed", $"{order.Symbol}: take-profit re-armed", rearm.Reason);

        // The shares the sell was holding are free again: put the stop back over them now. Only in
        // session — the stop worker places nothing while the venue is shut, and the next open re-places
        // every stop anyway.
        if (bounce && _calendar.GetStatus().IsOpen) _stopReleaser?.RestoreSoon();
    }

    /// <summary>
    /// Feeds this pass's prices into the rolling window for every symbol a windowed trigger watches,
    /// and forgets the symbols none does any more. Recorded BEFORE evaluation so the window's extreme
    /// includes the price being judged.
    ///
    /// <para>
    /// Only while the market is open — a price recorded while it is shut would count as trading time.
    /// A gap longer than two passes (and at least five minutes) is read as the market having been shut
    /// in between; see <see cref="RecentPriceWindow"/> for why that carries a window across the close.
    /// </para>
    /// </summary>
    private void RecordWindowedPrices(
        IReadOnlyList<ArmedOrder> armed,
        IReadOnlyDictionary<string, PsxLiveQuote> live,
        DateTime now,
        bool marketIsOpen)
    {
        var watched = armed
            .Where(o => o.IsWindowed)
            .Select(o => o.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _recentPrices.Retain(watched);
        if (!marketIsOpen) return;

        var maxGap = RecentPriceWindow.MaxGapFor(_options.Value.Monitor.IntervalSeconds);

        foreach (var symbol in watched)
            if (live.TryGetValue(symbol, out var quote) && quote.Current is > 0)
                _recentPrices.Record(symbol, quote.Current.Value, now, maxGap);
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
    /// Records that a bounded trail's current move triggered outside its price range, so it waits for a
    /// strictly new extreme before it can submit.
    ///
    /// <para>
    /// A failed or missed write is logged and otherwise ignored, like a missed ratchet. Nothing was sent
    /// on this pass either way; the cost of a lost write is that the lock is absent, so a later price
    /// back inside the range could submit on the same move — still within the operator's bound, which
    /// is why losing it is a lapse in the skip rule rather than a breach of the price.
    /// </para>
    /// </summary>
    private async Task SkipTrailMoveAsync(ArmedOrder order, string why, CancellationToken ct)
    {
        if (order.ReferencePrice is not { } reference) return;

        try
        {
            if (!await _repository.TrySetArmedOrderTrailSkipAsync(order.ArmedId, reference, why, ct))
                return;

            _logger.LogWarning("[ArmedOrders] {ArmedId} ({Symbol}) skipped a move: {Why}",
                order.ArmedId, order.Symbol, why);
            _activity?.Info("Armed", $"{order.Symbol}: trailing {order.Action} skipped a move", why);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "[ArmedOrders] Could not record a skipped move on {ArmedId} ({Symbol}).",
                order.ArmedId, order.Symbol);
        }
    }

    /// <summary>
    /// Asks a protective stop of ours to stand down when it is the thing holding the shares a
    /// triggered SELL needs. Returns the release reason when a stop actually stood down, and null
    /// whenever nothing needed releasing or nothing could be released safely.
    ///
    /// <para>
    /// Called for every triggered SELL, persistent or not, with the availability the caller already
    /// confirmed — so the ONE place that decides "our own stop is in the way" acts on the SAME read
    /// that decided whether the order could fill at all, and the trigger costs one broker read rather
    /// than two.
    /// </para>
    ///
    /// <para>
    /// Every unknown resolves to doing nothing: an unreadable book, an unreadable quantity, or shares
    /// committed to an order this system did not place all come back not-released, and the caller
    /// leaves the sell armed. Cancelling on a guess here would cancel a stranger's live order.
    /// </para>
    /// </summary>
    /// <summary>
    /// Records that a triggered SELL went back to "armed" without submitting, and returns the sentence
    /// saying when it will be tried again.
    ///
    /// <para>
    /// Uses <see cref="PersistentOrderDecisions.AutoRetryDelayFor"/> rather than a schedule of its own —
    /// 1, 2, 4, 8, 16, 30 minutes — so this repository has ONE backoff curve across the three loops
    /// that retry a refused order (<c>PersistentOrderWorker</c>, <c>ProtectiveStopWorker</c> and this
    /// one) instead of three that drift apart. The first retry stays at one minute for the same reason
    /// it does there: a single refusal is usually a blip, and the common case must not pay for the
    /// pathological one.
    /// </para>
    ///
    /// <para>
    /// <b>It backs off; it never gives up.</b> The one state that ENDS an armed SELL is
    /// <see cref="ArmedSellOutlook"/>'s retirement, which needs a broker-confirmed empty holding.
    /// Everything else — a foreign order over the whole position, an unreadable book, a refusal this
    /// code cannot classify — costs a slower retry rather than a lost exit. That is the same asymmetry
    /// <c>OrderRejectionOutlook</c>'s default has, and for the same reason.
    /// </para>
    /// </summary>
    /// <param name="escalate">
    /// False holds the wait at the first step (one minute) however often it repeats. For a refusal that
    /// sent nothing and that the operator can lift at any moment — the approval gate.
    /// </param>
    private string NoteRefusal(ArmedOrder order, bool escalate = true)
    {
        var entry = _refusedFires.AddOrUpdate(
            order.ArmedId,
            _ => (DateTime.UtcNow + PersistentOrderDecisions.AutoRetryDelayFor(1), 1),
            (_, previous) =>
            {
                var failures = escalate ? previous.Failures + 1 : Math.Max(1, previous.Failures);
                return (DateTime.UtcNow + PersistentOrderDecisions.AutoRetryDelayFor(escalate ? failures : 1),
                    failures);
            });

        var wait = entry.NextUtc - DateTime.UtcNow;
        return $"Refused {entry.Failures} time(s) in a row; the next attempt is in "
             + $"{Math.Max(1, (int)Math.Round(wait.TotalMinutes))} minute(s). It stays armed and the "
             + "trigger is re-checked then.";
    }

    /// <summary>
    /// Forgets a SELL's refusal history. Called wherever the order stops being refused — it submitted,
    /// it was retired, or it aged out — so a later refusal starts at one minute rather than inheriting
    /// a ceiling earned hours earlier, and so the dictionary cannot accumulate ids (§0.1).
    /// </summary>
    private void ClearRefusal(string armedId) => _refusedFires.TryRemove(armedId, out _);

    private async Task<string?> ReleaseOwnStopIfBlockingAsync(
        ArmedOrder order, ConfirmedSellAvailability confirmed, CancellationToken ct)
    {
        if (_stopReleaser is null) return null;

        // Not known is not the same as zero, and neither is a positive figure: only a confirmed
        // "every share is committed" is worth cancelling protection over.
        //
        // BROKER-confirmed since 2026-09-07, and this is the site where that mattered most. Cancelling
        // a stop is the one action here that REMOVES protection, and it used to be taken on a snapshot
        // a background timer had last refreshed — 21 minutes earlier on the deployed interval. A
        // cancellation made anywhere else freed the shares without the snapshot noticing, so this could
        // stand a stop down to make room for a sell that needed no room at all, opening a protection
        // gap for nothing. A read that fails leaves the stop alone: unknown resolves to doing nothing,
        // which is this method's standing rule and is doubly right when the alternative is irreversible.
        if (confirmed.MustDeferSizing) return null;

        var availability = confirmed.Decision;
        if (!availability.Known || availability.AvailableQuantity > 0) return null;

        var release = await _stopReleaser.ReleaseForSellAsync(order.Symbol, order.Quantity, ct);
        _logger.LogWarning(
            "[ArmedOrders] {ArmedId} ({Symbol}) asked for shares held by a protective stop: "
            + "released={Released} — {Why}",
            order.ArmedId, order.Symbol, release.Released, release.Reason);

        return release.Released ? release.Reason : null;
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
        var allStops = await _repository.GetProtectiveStopsAsync(openOnly: false, ct);
        var stop = allStops.FirstOrDefault(s => s.StopId == order.ProtectiveStopId);

        if (ProtectiveStopDecisions.OrphanedBackstopReason(stop) is { } orphaned)
        {
            // Retired, not merely held: a closed stop never reopens, so holding this armed only repeats
            // the stand-down on every pass until it expires (THCCL, 2026-09-25, x41 after the sale).
            ClearRefusal(order.ArmedId);
            var retired = await _repository.TrySetArmedOrderStateAsync(
                order.ArmedId, "armed", "cancelled",
                $"Local backstop retired: {orphaned}.", ct: ct);
            return retired ? $"{orphaned}; the backstop has been retired" : orphaned;
        }

        IReadOnlyList<RestingOrder>? resting;
        try { resting = await _outstandingReader.GetOutstandingOrdersAsync(order.Symbol, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ArmedOrders] Could not read the outstanding book before firing backstop {ArmedId}.",
                order.ArmedId);
            resting = null;   // unreadable counts as "cannot rule out a resting stop"
        }

        // A native stop resting for a DIFFERENT stop row on this symbol covers a different tranche at a
        // different trigger, and standing down against it would leave these shares with nothing: the
        // sibling's order already made DecidePlacement skip this row's own native stop, so the backstop
        // is the only cover left. Matched by order number only, so an order this system cannot
        // positively attribute to another row still stands the backstop down, exactly as before.
        var siblings = ProtectiveStopDecisions.OrdersOwnedBySiblings(
            stop, allStops, DateOnly.FromDateTime(_calendar.GetStatus().PktNow));

        return ProtectiveStopDecisions.BackstopShouldStandDown(stop, resting, out var reason, siblings)
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
            //
            // Only while the market is open, which the sweep did not check. Out of hours the quotes are
            // yesterday's close, so a proposal stated against today's levels could be retired for
            // "drifting" from a price that is simply the last one anybody paid — and retiring is not
            // recoverable by waiting. The TTL rule below needs no prices and still runs, so an
            // out-of-hours sweep keeps doing the half of its job that is sound.
            IReadOnlyDictionary<string, PsxLiveQuote> live = new Dictionary<string, PsxLiveQuote>();
            var marketOpen = _calendar.GetStatus().IsOpen;
            if (drift > 0 && !marketOpen)
            {
                _logger.LogDebug(
                    "[Proposals] Market is closed; expiring on age only, since a drift check would "
                    + "compare today's proposals against the previous close.");
            }
            else if (drift > 0)
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
