using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Broker;
using TradingAgent.Config;
using TradingAgent.Manager;
using TradingAgent.Market;
using TradingAgent.Models;
using TradingAgent.Observability;
using TradingAgent.Persistence;
using TradingAgent.Reconciliation;
using TradingAgent.Watchlist;

namespace TradingAgent.Trading;

/// <summary>
/// Keeps protective stops honest: confirms the entry actually filled, places the native stop,
/// re-places it every session for as long as the position exists, and — when a newer stop supersedes
/// this one (a break-even lift, an ATR trail) — retires its native order at the broker once, and only
/// once, the replacement is confirmed resting.
///
/// <para>
/// <b>Why a separate worker rather than the monitor pass.</b> Reads and placements go through
/// <see cref="IBrokerStateReader"/>/<see cref="TradingManager"/> rather than this class talking to a
/// broker directly, so the cost here depends on which adapter is active — a browser-driven one pays
/// for a page scrape, an API/socket-driven one does not. Either way this runs on its own slower clock
/// rather than the monitor's 30-second cadence, and does nothing at all when no stop is open — see
/// <see cref="TriggerSoon"/> for the on-demand path that shortens the wait after a stop is raised
/// without changing that cadence for everything else.
/// </para>
///
/// <para>
/// <b>The bias throughout is inaction.</b> Every unreadable value is treated as unknown rather than
/// as zero, and every ambiguity resolves to "do not place". The two mistakes are not symmetric: a
/// stop that failed to go in is visible in the panel and can be placed by hand, whereas a duplicate
/// stop sells the position twice. Where a broker DOES expose a verified cancel
/// (<see cref="IBrokerOrderCanceller"/>), a superseded stop's old order is retired through it — but
/// only after its replacement is confirmed live, and only ever forward: a cancel that fails or cannot
/// be confirmed leaves the old order resting and the row retried next pass, never the reverse.
/// </para>
/// </summary>
public sealed class ProtectiveStopWorker : BackgroundService, IMarketSessionOpenParticipant
{
    public string Name => "protective stops";
    public int Order => 200;

    private readonly IServiceScopeFactory _scopes;
    private readonly IBrokerStateReader _stateReader;
    private readonly IBrokerOrderCanceller _canceller;
    private readonly TradingManager _manager;
    private readonly ApprovalGate _approvals;
    private readonly IMarketCalendar _calendar;
    private readonly OrderWindow _orderWindow;
    private readonly TradingPolicyProvider _policy;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<ProtectiveStopWorker> _logger;
    private readonly IUserNotifier? _notifier;
    private readonly TradingActivityLog? _activity;

    /// <summary>
    /// Alerts already sent, keyed by stop and reason, so a condition that persists across passes is
    /// reported once rather than every few minutes. A stop that cannot be placed stays unplaceable
    /// all session; re-sending that every 3 minutes is how a channel becomes noise the user mutes,
    /// which would cost them the one alert that mattered.
    /// </summary>
    private readonly HashSet<string> _alerted = [];
    private DateOnly _alertedFor;

    /// <summary>Serialises ad-hoc baseline captures and guards the shared snapshot below.</summary>
    private readonly SemaphoreSlim _baselineGate = new(1, 1);
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>
    /// The last account snapshot read, reused for a few seconds so that arming several stops in a row
    /// costs one broker read rather than one each. Holdings cannot change between two reads that close
    /// together without an order of ours in between, and an order of ours would move the baseline
    /// out of scope anyway.
    /// </summary>
    private BrokerReconciliationSnapshot? _recentSnapshot;
    private DateTime _recentSnapshotUtc = DateTime.MinValue;

    private static readonly TimeSpan HoldingsReuseWindow = TimeSpan.FromSeconds(30);

    public ProtectiveStopWorker(
        IServiceScopeFactory scopes,
        IBrokerStateReader stateReader,
        IBrokerOrderCanceller canceller,
        TradingManager manager,
        ApprovalGate approvals,
        IMarketCalendar calendar,
        OrderWindow orderWindow,
        TradingPolicyProvider policy,
        IOptions<TradingAgentOptions> options,
        ILogger<ProtectiveStopWorker> logger,
        TradingActivityLog? activity = null,
        IUserNotifier? notifier = null)
    {
        _activity    = activity;
        _notifier    = notifier;
        _scopes      = scopes;
        _stateReader = stateReader;
        _canceller   = canceller;
        _manager     = manager;
        _approvals   = approvals;
        _calendar    = calendar;
        _orderWindow = orderWindow;
        _policy      = policy;
        _options     = options;
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(
            Math.Clamp(_options.Value.ProtectiveStopPollMinutes, 1, 60));

        _logger.LogInformation("[ProtectiveStops] Worker started. Interval={Minutes}min.", interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try { await RunNowAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[ProtectiveStops] Pass failed.");
            }
        }

        _logger.LogInformation("[ProtectiveStops] Worker stopped.");
    }

    // ── Immediate baseline capture ────────────────────────────────────────────

    /// <summary>
    /// Records the pre-entry holding for a freshly armed stop, in the background.
    ///
    /// <para>
    /// <b>Why this is not simply left to the periodic pass.</b> A fill is proved by holdings rising,
    /// which needs the number from before the entry went in. The pass refreshes that number every few
    /// minutes, so an entry armed at a level the price is already touching can trigger before any
    /// reading was taken — and a stop with no baseline cannot confirm its fill and stays dormant.
    /// Kicking a read here shrinks that window from minutes to seconds.
    /// </para>
    ///
    /// <para>
    /// Deliberately fire-and-forget: this is called from the arm request, and blocking it on a page
    /// scrape would freeze the dialog for seconds on every arm to close a window most orders never
    /// enter. Failure is not fatal — the periodic pass is still the durable path.
    /// </para>
    /// </summary>
    public void CaptureBaselineSoon(string stopId) =>
        _ = Task.Run(async () =>
        {
            try { await CaptureBaselineAsync(stopId); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[ProtectiveStops] Immediate baseline capture failed for {StopId}; the periodic "
                    + "pass will try again.", stopId);
            }
        });

    private async Task CaptureBaselineAsync(string stopId)
    {
        await _baselineGate.WaitAsync();
        try
        {
            using var scope = _scopes.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ITradingRepository>();

            var stop = (await repository.GetProtectiveStopsAsync(openOnly: true))
                .FirstOrDefault(s => s.StopId == stopId);
            if (stop is null || stop.State != "pending_fill") return;

            // The load-bearing check. By the time this read completes the entry may already have
            // fired and filled — recording holdings NOW would enshrine the post-fill number as the
            // "before" figure, making the delta zero and the fill look like it never happened. Only
            // an entry that has still not gone in can supply a baseline.
            var parent = stop.ParentArmedId is null
                ? null
                : (await repository.GetArmedOrdersAsync(armedOnly: false))
                    .FirstOrDefault(o => o.ArmedId == stop.ParentArmedId);

            if (parent is null || parent.State is not ("armed" or "firing"))
            {
                _logger.LogInformation(
                    "[ProtectiveStops] {StopId}: the entry had already left the armed state before a "
                    + "baseline could be taken; leaving it to the fill watch.", stopId);
                return;
            }

            var snapshot = await SnapshotForBaselineAsync();
            if (snapshot is not { Healthy: true }) return;

            var held = TryHeld(HoldingsFrom(snapshot), stop.Symbol);
            if (held is not { } quantity) return;

            if (await repository.RecordProtectiveStopBaselineAsync(stopId, (int)Math.Floor(quantity)))
            {
                _logger.LogInformation(
                    "[ProtectiveStops] {StopId} ({Symbol}): baseline holding recorded as {Qty}.",
                    stopId, stop.Symbol, (int)Math.Floor(quantity));
                _activity?.Info("Stops",
                    $"{stop.Symbol}: pre-entry holding recorded as {(int)Math.Floor(quantity)}",
                    "This is what the fill will be measured against.");
            }
        }
        finally
        {
            _baselineGate.Release();
        }
    }

    /// <summary>Reads the account snapshot, reusing a very recent read. ASSUMES the baseline gate is held.</summary>
    private async Task<BrokerReconciliationSnapshot?> SnapshotForBaselineAsync()
    {
        if (_recentSnapshot is not null && DateTime.UtcNow - _recentSnapshotUtc < HoldingsReuseWindow)
            return _recentSnapshot;

        var snapshot = await ReadAccountSnapshotAsync();
        if (snapshot is { Healthy: true })
        {
            _recentSnapshot = snapshot;
            _recentSnapshotUtc = DateTime.UtcNow;
        }
        return snapshot;
    }

    public async Task RunNowAsync(CancellationToken ct = default)
    {
        await _runGate.WaitAsync(ct);
        try { await RunPassCoreAsync(ct); }
        finally { _runGate.Release(); }
    }

    /// <summary>
    /// Fire-and-forget on-demand pass, for a caller that just wrote a stop (raised or newly armed) and
    /// wants it acted on sooner than the periodic clock — mirroring <see cref="CaptureBaselineSoon"/>'s
    /// reasoning. Not awaited by design: the caller (e.g. a strategy's exit evaluation) must not be
    /// blocked on a broker round trip, and the periodic pass remains the durable fallback if this one
    /// is skipped, races another, or fails outright.
    /// </summary>
    public void TriggerSoon() =>
        _ = Task.Run(async () =>
        {
            try { await RunNowAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[ProtectiveStops] On-demand pass failed; the periodic pass will try again.");
            }
        });

    public Task RunAtMarketOpenAsync(MarketSessionOpenContext context, CancellationToken ct) =>
        RunNowAsync(ct);

    private async Task RunPassCoreAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITradingRepository>();

        var stops = await repository.GetProtectiveStopsAsync(openOnly: true, ct);
        if (stops.Count == 0) return;

        // Deliberately the CALENDAR here, not OrderWindow — unlike order placement and the take-profit
        // retry, which now also run during the pre-open OHO state.
        //
        // Two reasons this worker must not follow them. First, it evaluates TRIGGERS against live
        // prices, and pre-open there are none: the feed republishes reference data, so a stop would be
        // judged against a stale number and could fire spuriously or fail to fire. Second, it does not
        // need to: OHO accepts orders but performs no matching, so no position can be filled before
        // the open and therefore none can need protecting during it. The first pass after the bell —
        // within the normal interval — is soon enough, and it runs on real prices.
        var market = _calendar.GetStatus();
        if (!market.IsOpen) return;

        _activity?.Info("Stops", $"Checking {stops.Count} protective stop(s)");

        // One account read serves every stop in the pass. Reading per-stop would multiply the most
        // expensive thing this worker does by the number of positions held. Cost here depends on the
        // active IBrokerStateReader implementation — a browser-driven adapter pays for a page scrape,
        // an API/socket-driven one does not; this worker no longer knows or cares which.
        //
        // The resting-book read is also what makes overnight correctness work, and the reason is not
        // obvious: PSX orders are DAY orders, and the broker cancels every resting order at the close —
        // confirmed live on 2026-08-19, where a protective sell that had rested since 10:00 was gone
        // minutes after the bell along with the whole book. So a stop this worker placed yesterday does
        // not exist today, whatever the ledger remembers about placing it. Nothing here needs to
        // special-case that as long as the decision is driven by what is ACTUALLY resting rather than by
        // what was recorded: the first pass after the open sees an unprotected position and places the
        // stop again. Anything that starts trusting the stored state instead would leave positions
        // unprotected every morning while reporting them protected.
        var snapshot = await ReadAccountSnapshotAsync();

        // Unknown is never zero: a snapshot that is not fully healthy could mean holdings specifically
        // failed to read, and an empty Positions list from THAT would look identical to "you hold
        // nothing" — which would close a stop still protecting a real position. So an unhealthy snapshot
        // is treated as holdings AND resting-book unknown together, not per-field, even though a failure
        // may really have been narrower than that. Coarser than the old per-field reads, but never wrong
        // in the dangerous direction.
        var holdings = snapshot is { Healthy: true } ? HoldingsFrom(snapshot) : null;
        var resting  = snapshot is { Healthy: true } ? RestingFrom(snapshot) : null;
        var today    = DateOnly.FromDateTime(market.PktNow);

        if (snapshot is not { Healthy: true })
            _logger.LogWarning(
                "[ProtectiveStops] Account snapshot unhealthy this pass ({Reason}); holdings and the " +
                "resting book are treated as unknown, so nothing will be placed or cancelled.",
                snapshot?.Reason ?? "no snapshot");

        // Share this read with any baseline capture that lands in the next few seconds, so arming a
        // stop just after a pass does not re-read the account twice.
        if (snapshot is { Healthy: true })
        {
            _recentSnapshot = snapshot;
            _recentSnapshotUtc = DateTime.UtcNow;
        }

        // Superseded rows first: a stop here has already lost its place to a newer one, so retiring its
        // broker order is retried before anything else this pass — including a fresh supersede that
        // MaintainAsync below may itself create and immediately retire in the same pass.
        foreach (var stop in stops.Where(s => s.State == "superseded_pending_cancel"))
        {
            ct.ThrowIfCancellationRequested();
            try { await RetireSupersededAsync(repository, stop, resting, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[ProtectiveStops] {StopId} ({Symbol}) failed to retire this pass.",
                    stop.StopId, stop.Symbol);
            }
        }

        // A predecessor superseded mid-loop (see the hook in PlaceNativeStopAsync) is transitioned in
        // the DATABASE immediately, but the in-memory `stops` snapshot taken at the top of this pass
        // still shows it "active" — the newest-first ordering means the new stop it lost its place to
        // is visited BEFORE it. Without this set, reaching its stale entry later in this same loop
        // would call MaintainAsync on a row already headed for retirement. Empty in the ordinary case;
        // populated only when a supersede actually happens during this pass.
        var retiredThisPass = new HashSet<string>();

        foreach (var stop in stops.Where(s => s.State is "pending_fill" or "active"
                                            && !retiredThisPass.Contains(s.StopId)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (stop.State == "pending_fill")
                    await WatchForFillAsync(repository, stop, holdings, resting, ct);
                else
                    await MaintainAsync(repository, stop, holdings, resting, today, stops, retiredThisPass, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[ProtectiveStops] {StopId} ({Symbol}) failed this pass.",
                    stop.StopId, stop.Symbol);
            }
        }
    }

    // ── Fill confirmation ─────────────────────────────────────────────────────

    /// <summary>
    /// Decides whether the entry behind a dormant stop has actually executed.
    ///
    /// <para>
    /// While the entry is still armed the holdings reading is not a fill test at all — it is the
    /// baseline, and it is refreshed each pass so the number the fill is later measured against is
    /// the one immediately before the entry went in.
    /// </para>
    /// </summary>
    private async Task WatchForFillAsync(
        ITradingRepository repository,
        ProtectiveStop stop,
        IReadOnlyDictionary<string, decimal?>? holdings,
        IReadOnlyList<RestingOrder>? resting,
        CancellationToken ct)
    {
        var parent = stop.ParentArmedId is null
            ? null
            : (await repository.GetArmedOrdersAsync(armedOnly: false, ct))
                .FirstOrDefault(o => o.ArmedId == stop.ParentArmedId);

        if (parent is null)
        {
            await CloseAsync(repository, stop,
                "The entry this stop was attached to no longer exists.", ct);
            return;
        }

        // Still waiting on the trigger: keep the baseline current instead of testing for a fill.
        if (parent.State is "armed" or "firing")
        {
            if (holdings is not null && TryHeld(holdings, stop.Symbol) is { } held)
                await repository.RecordProtectiveStopBaselineAsync(
                    stop.StopId, (int)Math.Floor(held), ct);
            return;
        }

        if (parent.State is "cancelled" or "expired" or "failed")
        {
            await CloseAsync(repository, stop,
                $"The entry ended as '{parent.State}' without filling, so there is nothing to protect.", ct);
            return;
        }

        // A recurring entry can be legitimately between native DAY orders when this pass runs. Its
        // durable child intent, not the momentary absence of an outstanding row, owns whether the
        // entry is still alive; otherwise the attached protection would close every night before the
        // entry got its next placement.
        PersistentOrderIntent? persistentEntry = null;
        if (parent.PersistentUntilFilled)
        {
            persistentEntry = (await repository.GetPersistentOrdersAsync(openOnly: false, ct))
                .FirstOrDefault(i => i.SourceArmedId == parent.ArmedId);
        }

        if (persistentEntry is { FilledQuantity: > 0 } exactFill)
        {
            var confirmed = Math.Min(parent.Quantity, exactFill.FilledQuantity);
            var reason = $"Persistent entry broker orders report {confirmed} exact filled share(s).";
            await repository.RecordProtectiveStopFillAsync(stop.StopId, confirmed, reason, ct);
            await ArmBackstopAsync(repository, stop with
            {
                State = "active",
                DesiredQuantity = Math.Max(stop.DesiredQuantity, confirmed)
            }, confirmed, ct);
            _activity?.Info("Stops",
                $"{stop.Symbol}: exact entry fill activated protection ({confirmed})", reason);
            return;
        }

        var entryStillResting = persistentEntry is { IsTerminal: false }
            || resting is null
            || resting.Any(r => r.Symbol.Equals(stop.Symbol, StringComparison.OrdinalIgnoreCase));

        // The watch ends with the entry itself: past that, a holdings change is somebody else's trade.
        var deadlinePassed = parent.ExpiresUtc is { } expiry && DateTime.UtcNow >= expiry;

        var verdict = ProtectiveStopDecisions.EvaluateFill(
            stop, holdings is null ? null : TryHeld(holdings, stop.Symbol), entryStillResting, deadlinePassed);

        switch (verdict.Outcome)
        {
            case FillOutcome.Filled:
                await repository.RecordProtectiveStopFillAsync(
                    stop.StopId, verdict.Quantity, verdict.Reason, ct);
                _logger.LogWarning(
                    "[ProtectiveStops] {StopId} ({Symbol}) ACTIVE on a confirmed fill of {Qty}. {Why}",
                    stop.StopId, stop.Symbol, verdict.Quantity, verdict.Reason);
                _activity?.Info("Stops",
                    $"{stop.Symbol}: entry filled ({verdict.Quantity}) — the stop is now active",
                    verdict.Reason);
                await ArmBackstopAsync(repository, stop with
                {
                    State = "active",
                    DesiredQuantity = Math.Max(stop.DesiredQuantity, verdict.Quantity)
                }, verdict.Quantity, ct);
                break;

            case FillOutcome.NeverFilled:
            case FillOutcome.TimedOut:
                await CloseAsync(repository, stop, verdict.Reason, ct);
                break;

            case FillOutcome.NoBaseline:
                // Loud, and left for a person. Activating on a guess would size a real sell order
                // against a number nobody measured.
                _logger.LogError(
                    "[ProtectiveStops] {StopId} ({Symbol}) cannot be confirmed: {Why}",
                    stop.StopId, stop.Symbol, verdict.Reason);
                await AlertOnceAsync(stop, "no-baseline", DateOnly.FromDateTime(_calendar.GetStatus().PktNow),
                    $"🛡️ **Stop needs you — {stop.Symbol}**\n"
                    + $"• The entry fired, but your holding before it was never recorded, so the fill "
                    + "cannot be confirmed\n"
                    + $"• The stop at {stop.StopTrigger:0.##} will NOT be placed automatically\n"
                    + "_Place it manually, or disarm it from the trading UI._");
                break;

            default:
                _logger.LogDebug("[ProtectiveStops] {StopId} ({Symbol}): {Why}",
                    stop.StopId, stop.Symbol, verdict.Reason);
                break;
        }
    }

    // ── Placement and session recurrence ──────────────────────────────────────

    private async Task MaintainAsync(
        ITradingRepository repository,
        ProtectiveStop stop,
        IReadOnlyDictionary<string, decimal?>? holdings,
        IReadOnlyList<RestingOrder>? resting,
        DateOnly today,
        IReadOnlyList<ProtectiveStop> allStops,
        HashSet<string> retiredThisPass,
        CancellationToken ct)
    {
        if (stop.ParentArmedId is { } parentId)
        {
            var entry = (await repository.GetPersistentOrdersAsync(openOnly: false, ct))
                .FirstOrDefault(i => i.SourceArmedId == parentId);
            if (entry is { FilledQuantity: > 0 } && entry.FilledQuantity > stop.DesiredQuantity)
            {
                var confirmed = entry.FilledQuantity;
                await repository.RecordProtectiveStopFillAsync(stop.StopId, confirmed,
                    $"Persistent entry cumulative fill rose to {confirmed} share(s).", ct);
                if (stop.LocalBackstopArmedId is { } backstopId)
                    await repository.TrySetArmedOrderQuantityAsync(backstopId, confirmed, ct);
                stop = stop with { DesiredQuantity = confirmed };
            }
        }

        // A stop that has already had its session and is not recurring has done its job; it is not
        // re-placed, but it is also not closed, because the position may still be held.
        if (!stop.Recurring && stop.LastPlacedSessionDate is { } placed && placed != today)
        {
            await CloseAsync(repository, stop,
                $"Single-session stop; it was placed for {placed:yyyy-MM-dd} and is not set to recur. "
                + "The position is no longer protected by this system.", ct);
            return;
        }

        var held = holdings is null ? null : TryHeld(holdings, stop.Symbol);

        // ── A raise has to settle its predecessor before it can be placed at all ─────────────────
        // The broker sizes every SELL against custody MINUS quantity already committed to resting
        // SELLs, so a predecessor holding the whole position makes the replacement unplaceable — the
        // "make" of make-before-break cannot succeed until the "break" has happened. Deciding that
        // here, rather than discovering it as a placement failure, is what stops a raise retrying
        // forever while the operator is told it succeeded.
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (stop.SupersedesStopId is { Length: > 0 } predecessorId)
        {
            var predecessor = allStops.FirstOrDefault(s => s.StopId == predecessorId);

            // Cancelling the old order is only worth the gap it opens if a replacement can actually go
            // out afterwards. If the window is shut, waiting is not merely safer — it is CORRECT: the
            // venue clears the book at the close, so the raise is placed clean next session with
            // nothing cancelled and no gap at all.
            var window = _orderWindow.Evaluate();
            var supersede = ProtectiveStopDecisions.DecideSupersede(
                stop, predecessor, held, resting, replacementWindowAllowed: window.Allowed);

            switch (supersede.Action)
            {
                case SupersedeAction.CancelPredecessorThenPlace when predecessor is not null:
                    if (!await OpenReplacementWindowAsync(
                            repository, stop, predecessor, held, supersede.Reason,
                            retiredThisPass, resting, today, ct))
                        return;
                    // The cancel is CONFIRMED gone, but `resting` is this pass's snapshot and still
                    // lists it. Without excluding it, a raise inside the price-match tolerance would
                    // be skipped as "already protected" by an order that no longer exists — and the
                    // placement below would size against a book that has moved on.
                    if (predecessor.LastOrderNo is { Length: > 0 } cancelled)
                        excluded.Add(cancelled.Trim());
                    break;

                case SupersedeAction.Wait:
                    // Visible, not LogDebug: "the raise you asked for has not reached the broker" is
                    // precisely the state this whole change exists to stop hiding. Once per stop per
                    // day, because it persists all session by design and re-reporting it every pass is
                    // how a channel becomes noise.
                    _logger.LogInformation(
                        "[ProtectiveStops] {StopId} ({Symbol}): raise to {Trigger} is PENDING — {Why}",
                        stop.StopId, stop.Symbol, stop.StopTrigger, supersede.Reason);
                    if (MarkAlerted(stop, "supersede-pending", today))
                        _activity?.Warn("Stops",
                            $"{stop.Symbol}: the stop raise to {stop.StopTrigger:0.##} has NOT reached "
                            + "the broker yet", supersede.Reason);
                    return;

                case SupersedeAction.RetirePredecessorFirst when predecessor is not null:
                    // Its order is already gone, so there is nothing to cancel and nothing to lose by
                    // retiring it now — and retiring it BEFORE placing is what keeps both rows from
                    // placing an order each next session.
                    if (await repository.TrySetProtectiveStopStateAsync(
                            predecessor.StopId, predecessor.State, "superseded_pending_cancel",
                            $"Superseded by {stop.StopId}; its own order is no longer resting.", ct))
                    {
                        retiredThisPass.Add(predecessor.StopId);
                        await RetireSupersededAsync(
                            repository, predecessor with { State = "superseded_pending_cancel" },
                            resting, ct);
                        _logger.LogInformation(
                            "[ProtectiveStops] {StopId} ({Symbol}): retired predecessor {Predecessor} " +
                            "before placing — {Why}",
                            stop.StopId, stop.Symbol, predecessor.StopId, supersede.Reason);
                    }
                    break;

                case SupersedeAction.Proceed when predecessor?.LastOrderNo is { Length: > 0 } resting1:
                    // Room for both. Exclude the predecessor's order so a small raise is not mistaken
                    // for "already protected at this level" by the price match below.
                    excluded.Add(resting1.Trim());
                    break;
            }
        }

        var decision = ProtectiveStopDecisions.DecidePlacement(
            stop, held, today, resting ?? [], excluded);

        switch (decision.Action)
        {
            case PlacementAction.Close:
                await CloseAsync(repository, stop, decision.Reason, ct);
                // The position going to zero usually means the stop fired — an exit the operator did
                // not initiate, and the single most important thing on this worker to hear about.
                await AlertOnceAsync(stop, "closed", today,
                    $"🛡️ **Protection ended — {stop.Symbol}**\n"
                    + $"• {decision.Reason}\n"
                    + $"• The stop at {stop.StopTrigger:0.##} is no longer being managed\n"
                    + "_If the stop executed, the sale itself was reported separately._");
                return;

            case PlacementAction.Skip:
                _logger.LogDebug("[ProtectiveStops] {StopId} ({Symbol}): {Why}",
                    stop.StopId, stop.Symbol, decision.Reason);
                return;
        }

        await PlaceNativeStopAsync(repository, stop, decision, today, allStops, resting, retiredThisPass, ct);
    }

    /// <summary>
    /// Break-before-make: covers the position locally, cancels the predecessor's native order, retires
    /// its row, and hands back to the caller to place the replacement in the same pass.
    ///
    /// <para>
    /// <b>Why the ordering is not negotiable.</b> This broker sizes every SELL against custody MINUS
    /// the quantity committed to resting SELLs, so while the predecessor rests there is nothing to place
    /// the replacement against — make-before-break cannot complete, and no amount of retrying changes
    /// that. Cancelling first is the only route, which means deliberately opening a moment with no
    /// native stop at the broker. The local backstop is armed BEFORE the cancel goes out so that moment
    /// is covered rather than merely short.
    /// </para>
    ///
    /// <para>
    /// <b>Every failure leaves more protection, never less.</b> The backstop cannot be armed → nothing
    /// is cancelled. The cancel cannot be VERIFIED against the book → nothing is cancelled and the row
    /// is retried next pass, with the old order still resting. Only a cancel proven gone advances the
    /// state, and it advances it durably: the predecessor is closed in the ledger before the placement
    /// is attempted, so a crash in between resumes correctly — the next pass sees a closed predecessor,
    /// <see cref="ProtectiveStopDecisions.DecideSupersede"/> returns Proceed, and the replacement is
    /// placed against a free holding.
    /// </para>
    ///
    /// <para>
    /// <b>There is no rollback, deliberately.</b> Once the cancel is confirmed the shares are free, so a
    /// placement failure is transient (a socket, a refused approval) rather than structural, and the
    /// right response is the retry the next pass already performs — not re-placing the OLD, lower stop
    /// and having to supersede it all over again. The honest cost is that between the cancel and a
    /// successful placement the position is covered only by the local backstop, which needs this process
    /// running. That is the same caveat the existing "stop was not placed" path already carries.
    /// </para>
    /// </summary>
    /// <returns>True when the caller should go on to place the replacement this pass.</returns>
    private async Task<bool> OpenReplacementWindowAsync(
        ITradingRepository repository,
        ProtectiveStop successor,
        ProtectiveStop predecessor,
        decimal? held,
        string why,
        HashSet<string> retiredThisPass,
        IReadOnlyList<RestingOrder>? resting,
        DateOnly today,
        CancellationToken ct)
    {
        if (predecessor.LastOrderNo is not { Length: > 0 } orderNo)
            return true;   // nothing at the broker to cancel; DecideSupersede would not have sent us here

        // ── Cover first ──────────────────────────────────────────────────────
        // A raised stop is written straight to "active" by whoever raised it, so unlike a stop that grew
        // out of a pending fill it has never been through ArmBackstopAsync and has no local cover at
        // all. Arming it here is what makes the coming gap survivable.
        if (successor.LocalBackstopArmedId is null)
        {
            var quantity = held is { } h
                ? Math.Min(successor.DesiredQuantity, (int)Math.Floor(h))
                : 0;
            if (quantity <= 0)
            {
                _logger.LogWarning(
                    "[ProtectiveStops] {StopId} ({Symbol}): cannot size a backstop to cover the "
                    + "replacement window, so the old order stays. {Why}",
                    successor.StopId, successor.Symbol, why);
                return false;
            }

            await ArmBackstopAsync(repository, successor, quantity, ct);

            var armed = (await repository.GetProtectiveStopsAsync(openOnly: true, ct))
                .FirstOrDefault(s => s.StopId == successor.StopId);
            if (armed?.LocalBackstopArmedId is null)
            {
                // Unconfirmed cover is no cover. Leave the predecessor resting.
                _logger.LogError(
                    "[ProtectiveStops] {StopId} ({Symbol}): the local backstop could not be confirmed "
                    + "armed, so the predecessor's order was NOT cancelled. The position stays "
                    + "protected at the old trigger.", successor.StopId, successor.Symbol);
                _activity?.Warn("Stops",
                    $"{successor.Symbol}: the stop raise is still waiting — local cover for the "
                    + "changeover could not be armed",
                    "Nothing was cancelled; the old stop is still resting at the broker.");
                return false;
            }
            successor = armed;
        }

        _logger.LogWarning(
            "[ProtectiveStops] {StopId} ({Symbol}): cancelling predecessor order {OrderNo} to make room "
            + "for the raise to {Trigger}. {Why}",
            successor.StopId, successor.Symbol, orderNo, successor.StopTrigger, why);

        BrokerCancellationResult cancellation;
        try
        {
            cancellation = await _canceller.CancelOrderAsync(orderNo, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ProtectiveStops] {StopId} ({Symbol}): the cancel of predecessor order {OrderNo} threw; "
                + "it stays resting and the raise is retried next pass.",
                successor.StopId, successor.Symbol, orderNo);
            return false;
        }

        if (!cancellation.Gone)
        {
            // Not PROVEN gone. The book is the only authority here — an accepted request that cannot be
            // verified is exactly the case where assuming success would leave the position bare.
            _logger.LogWarning(
                "[ProtectiveStops] {StopId} ({Symbol}): predecessor order {OrderNo} is not confirmed "
                + "cancelled ({Message}); the raise waits and the old stop still protects the position.",
                successor.StopId, successor.Symbol, orderNo, cancellation.Message);
            if (MarkAlerted(successor, "replacement-cancel-unverified", today))
                _activity?.Warn("Stops",
                    $"{successor.Symbol}: the stop raise to {successor.StopTrigger:0.##} is waiting on a "
                    + "cancellation that could not be confirmed", cancellation.Message);
            return false;
        }

        // Confirmed gone. Close the predecessor BEFORE placing, so the durable state can only ever say
        // "the old order is gone" once it actually is — and so a crash here resumes rather than
        // re-cancelling something already cancelled.
        if (await repository.TrySetProtectiveStopStateAsync(
                predecessor.StopId, predecessor.State, "superseded_pending_cancel",
                $"Superseded by {successor.StopId}; its order {orderNo} was cancelled to free the "
                + "shares for the replacement.", ct))
        {
            retiredThisPass.Add(predecessor.StopId);
            await RetireSupersededAsync(
                repository, predecessor with { State = "superseded_pending_cancel" }, resting, ct);
        }

        _activity?.Info("Stops",
            $"{successor.Symbol}: old stop {orderNo} cancelled to make room for the raise to "
            + $"{successor.StopTrigger:0.##}",
            "The local backstop covers the position until the new stop is resting at the broker.");
        return true;
    }

    /// <summary>
    /// Sends the native Stop Loss SELL.
    ///
    /// <para>
    /// It goes through <see cref="ApprovalGate"/> like any other unattended submission. A hosted
    /// worker placing real orders must not be the one path that quietly bypasses
    /// <c>ApprovalRequired</c>, and a refusal here leaves the stop active so the next session can try
    /// again rather than consuming the intent.
    /// </para>
    /// </summary>
    private async Task PlaceNativeStopAsync(
        ITradingRepository repository,
        ProtectiveStop stop,
        PlacementDecision decision,
        DateOnly today,
        IReadOnlyList<ProtectiveStop> allStops,
        IReadOnlyList<RestingOrder>? resting,
        HashSet<string> retiredThisPass,
        CancellationToken ct)
    {
        var signal = new TradingSignal
        {
            IsSignal   = true,
            Action     = "SELL",
            Symbol     = stop.Symbol,
            Quantity   = decision.Quantity,
            OrderType  = "STOPLOSS",
            EntryPrice = stop.StopTrigger,
            LimitPrice = stop.StopLimit,
            Confidence = "HIGH",
            RawMessage = $"protective-stop:{stop.StopId}"
        };

        var groups = new[] { (IReadOnlyList<TradingSignal>)[signal] };
        var key    = $"protective-stop:{stop.StopId}:{today:yyyyMMdd}:{decision.Quantity}";

        var approval = _approvals.Decide(groups, key, new ApprovalContext(null, "protective-stop"));
        if (!approval.MayProceed)
        {
            _logger.LogWarning(
                "[ProtectiveStops] {StopId} ({Symbol}) needs a stop placed but was NOT authorised: "
                + "{Reason}. The position is UNPROTECTED at the broker.",
                stop.StopId, stop.Symbol, approval.Reason);
            _activity?.Error("Stops",
                $"{stop.Symbol}: stop not authorised, so nothing is resting at the broker",
                approval.Reason);

            await AlertOnceAsync(stop, "unauthorised", today,
                $"🛡️ **Stop NOT placed — {stop.Symbol} is unprotected**\n"
                + $"• SELL {decision.Quantity:N0} {stop.Symbol} at {stop.StopTrigger:0.##} was not authorised\n"
                + $"• {approval.Reason}\n"
                + "_Nothing is resting at the broker. Place it manually, or fix the approval mode._");
            return;
        }

        var result = await _manager.ExecuteGroupsAsync(groups, key, approval.Authorization, ct);
        var order  = result.Groups.FirstOrDefault()?.FirstOrDefault();

        if (result.Executed && order is { Success: true })
        {
            await repository.RecordProtectiveStopPlacementAsync(
                stop.StopId, today, decision.Quantity, order.OrderId, ct);
            _logger.LogWarning(
                "[ProtectiveStops] {StopId}: native stop PLACED — SELL {Qty} {Symbol} "
                + "trigger {Trigger} limit {Limit}. {Why}",
                stop.StopId, decision.Quantity, stop.Symbol, stop.StopTrigger, stop.StopLimit,
                decision.Reason);
            _activity?.Info("Stops",
                $"{stop.Symbol}: stop placed at the broker — SELL {decision.Quantity:N0} "
                + $"trigger {stop.StopTrigger:0.##} limit {stop.StopLimit:0.##}");

            // This row supersedes an earlier stop (a break-even lift, an ATR trail) — and this is the
            // exact moment its replacement was confirmed live. Only NOW does the predecessor move
            // toward retirement; see ProtectiveStop.SupersedesStopId and RetireSupersededAsync for why
            // this ordering — never the reverse — is what guarantees the position is never briefly
            // covered by zero stops.
            if (stop.SupersedesStopId is { Length: > 0 } supersededId)
            {
                var predecessor = allStops.FirstOrDefault(s => s.StopId == supersededId);
                if (predecessor is not null)
                {
                    var transitioned = await repository.TrySetProtectiveStopStateAsync(
                        predecessor.StopId, "active", "superseded_pending_cancel",
                        $"Superseded by {stop.StopId} (new trigger {stop.StopTrigger}), now confirmed " +
                        "resting at the broker.", ct);
                    if (transitioned)
                    {
                        // The predecessor's entry in this pass's `stops` snapshot is now stale (still
                        // shows "active" in memory) — mark it so the main loop's later pass over that
                        // snapshot skips it instead of re-running MaintainAsync on a row already headed
                        // for retirement.
                        retiredThisPass.Add(predecessor.StopId);
                        await RetireSupersededAsync(
                            repository, predecessor with { State = "superseded_pending_cancel" },
                            resting, ct);
                    }
                }
            }
            return;
        }

        // Not placed. The intent stays active so the next pass retries; the position is meanwhile
        // covered only by the local backstop, and only while this process is running.
        _logger.LogError(
            "[ProtectiveStops] {StopId} ({Symbol}): the native stop was NOT placed — {Reason}. "
            + "The position is protected only by the local backstop until this succeeds.",
            stop.StopId, stop.Symbol, order?.Message ?? result.Reason);
        _activity?.Error("Stops",
            $"{stop.Symbol}: the stop was NOT placed — the position is unprotected at the broker",
            order?.Message ?? result.Reason);

        await AlertOnceAsync(stop, "placement-failed", today,
            $"🛡️ **Stop rejected by the broker — {stop.Symbol}**\n"
            + $"• SELL {decision.Quantity:N0} {stop.Symbol} at {stop.StopTrigger:0.##} was refused\n"
            + $"• {order?.Message ?? result.Reason}\n"
            + "_Retrying next pass. Until it succeeds the position is covered only by the local "
            + "backstop, which needs AgentFox running._");
    }

    // ── Supersession ────────────────────────────────────────────────────────────

    /// <summary>
    /// Retires a stop that has already lost its place to a newer one — cancels its native order at the
    /// broker if it still has one resting, then closes the row. Never the other way around.
    ///
    /// <para>
    /// <b>Reached from two paths, deliberately.</b> <see cref="PlaceNativeStopAsync"/> calls this the
    /// moment a replacement is confirmed live, for low latency. The per-pass sweep in
    /// <see cref="RunPassCoreAsync"/> calls it for every row still in this state, which is what makes a
    /// failed or unconfirmed cancel — a network error, a crash mid-call, anything — self-heal: the row
    /// stays <c>superseded_pending_cancel</c> in the durable ledger rather than in memory, so the very
    /// next pass (periodic, or the next <see cref="TriggerSoon"/>) picks up exactly where it left off.
    /// </para>
    ///
    /// <para>
    /// <b>The failure mode this exists to avoid is zero stops resting, never two.</b> If the cancel
    /// cannot be verified, the row is left exactly as it is — <c>superseded_pending_cancel</c>, its
    /// native order (if any) still resting — so the position stays covered by BOTH the old and the new
    /// stop until the cancel actually succeeds. That is the same "briefly covered twice" state this
    /// codebase already accepts for a locally-superseded stop; core sizes every sell against custody
    /// minus resting sells, so two resting stops cannot oversell the position between them.
    /// </para>
    /// </summary>
    private async Task RetireSupersededAsync(
        ITradingRepository repository,
        ProtectiveStop stop,
        IReadOnlyList<RestingOrder>? resting,
        CancellationToken ct)
    {
        if (stop.LastOrderNo is not { Length: > 0 } orderNo)
        {
            // No native order was ever placed for this row — there is nothing at the broker to cancel.
            await repository.TrySetProtectiveStopStateAsync(
                stop.StopId, "superseded_pending_cancel", "closed",
                "Superseded before any native order was placed for it; nothing to cancel.", ct);
            return;
        }

        // Already gone from the book — fired, expired, or cancelled some other way. A resting-book read
        // that itself failed this pass (resting == null) is NOT evidence of anything, so it falls
        // through to attempting the cancel below rather than assuming the order is already gone.
        if (resting is not null
            && !resting.Any(r => string.Equals(r.OrderNo?.Trim(), orderNo, StringComparison.OrdinalIgnoreCase)))
        {
            await repository.TrySetProtectiveStopStateAsync(
                stop.StopId, "superseded_pending_cancel", "closed",
                $"Order {orderNo} is no longer in the outstanding book; nothing left to cancel.", ct);
            return;
        }

        BrokerCancellationResult result;
        try
        {
            result = await _canceller.CancelOrderAsync(orderNo, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ProtectiveStops] {StopId} ({Symbol}): cancel of superseded order {OrderNo} threw; " +
                "left resting, retried next pass.", stop.StopId, stop.Symbol, orderNo);
            return;
        }

        if (result.Gone)
        {
            await repository.TrySetProtectiveStopStateAsync(
                stop.StopId, "superseded_pending_cancel", "closed",
                $"Superseded order {orderNo} confirmed cancelled: {result.Message}", ct);
            _logger.LogInformation(
                "[ProtectiveStops] {StopId} ({Symbol}): superseded order {OrderNo} cancelled and " +
                "confirmed gone.", stop.StopId, stop.Symbol, orderNo);
            _activity?.Info("Stops", $"{stop.Symbol}: superseded stop {orderNo} cancelled", result.Message);
            return;
        }

        // Not verified gone. The row stays superseded_pending_cancel — retried next pass — and the old
        // order stays resting, so the position remains covered rather than briefly protected by nothing.
        _logger.LogWarning(
            "[ProtectiveStops] {StopId} ({Symbol}): cancel NOT verified for superseded order {OrderNo}: " +
            "{Message}. Left resting; the position is covered by both the old and new stop meanwhile.",
            stop.StopId, stop.Symbol, orderNo, result.Message);
        _activity?.Warn("Stops",
            $"{stop.Symbol}: could not confirm cancellation of superseded stop {orderNo}", result.Message);

        await AlertOnceAsync(stop, "cancel-unconfirmed", DateOnly.FromDateTime(_calendar.GetStatus().PktNow),
            $"⚠️ **Superseded stop could not be cancelled — {stop.Symbol}**\n"
            + $"• Old order #{orderNo} is still resting at the broker after a newer stop replaced it\n"
            + $"• {result.Message}\n"
            + "_The position is protected by both stops meanwhile — retrying automatically. If this "
            + "persists, cancel the old order manually._");
    }

    // ── Local backstop ────────────────────────────────────────────────────────

    /// <summary>
    /// Arms the local SELL that covers the window the native stop cannot — between the fill
    /// confirming and the next placement, and between sessions.
    ///
    /// <para>
    /// It is a gap-filler, never a second stop: before it fires, the monitor re-reads the outstanding
    /// book and stands down if a native stop is resting (see
    /// <see cref="ProtectiveStopDecisions.BackstopShouldStandDown"/>). Without that check, "native
    /// plus a backstop" would be two orders that both sell the same position.
    /// </para>
    /// </summary>
    private async Task ArmBackstopAsync(
        ITradingRepository repository,
        ProtectiveStop stop,
        int quantity,
        CancellationToken ct)
    {
        if (stop.LocalBackstopArmedId is not null) return;

        var backstop = new ArmedOrder
        {
            ArmedId       = Guid.NewGuid().ToString("N"),
            Symbol        = stop.Symbol,
            TriggerKind   = ArmedTriggerKind.PriceBelow,
            TriggerPrice  = stop.StopTrigger,
            Action        = "SELL",
            Quantity      = quantity,
            OrderType     = "STOPLOSS",
            Price         = stop.StopTrigger,
            LimitPrice    = stop.StopLimit,
            ExpiresUtc    = null,   // it lives as long as the intent does
            Note          = $"Local backstop for protective stop {stop.StopId}. Stands down while a "
                          + "native stop is resting at the broker.",
            ProtectiveStopId = stop.StopId
        };

        await repository.SaveArmedOrderAsync(backstop, ct);
        await repository.SetProtectiveStopBackstopAsync(stop.StopId, backstop.ArmedId, ct);

        _logger.LogInformation(
            "[ProtectiveStops] {StopId} ({Symbol}): local backstop armed at {Trigger} for {Qty}.",
            stop.StopId, stop.Symbol, stop.StopTrigger, quantity);
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    private async Task CloseAsync(
        ITradingRepository repository, ProtectiveStop stop, string reason, CancellationToken ct)
    {
        await repository.TrySetProtectiveStopStateAsync(stop.StopId, stop.State, "closed", reason, ct);

        if (stop.LocalBackstopArmedId is { } backstopId)
            await repository.TrySetArmedOrderStateAsync(
                backstopId, "armed", "cancelled",
                $"The protective stop it backed was closed: {reason}", ct: ct);

        _logger.LogWarning("[ProtectiveStops] {StopId} ({Symbol}) closed: {Reason}",
            stop.StopId, stop.Symbol, reason);
        _activity?.Warn("Stops", $"{stop.Symbol}: protection ended", reason);
    }

    /// <summary>
    /// The account snapshot behind this worker's holdings and resting-book reads — whichever
    /// <see cref="IBrokerStateReader"/> is active for this edition/broker. Null only when the read
    /// itself threw; an unhealthy-but-present snapshot is handled by the caller (see the "Unknown is
    /// never zero" note in <see cref="RunPassCoreAsync"/>), not here.
    /// </summary>
    private async Task<BrokerReconciliationSnapshot?> ReadAccountSnapshotAsync()
    {
        try { return await _stateReader.ReadSnapshotAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ProtectiveStops] The account snapshot could not be read this pass.");
            _activity?.Warn("Stops", "The account snapshot could not be read this pass", ex.Message);
            return null;
        }
    }

    private static IReadOnlyDictionary<string, decimal?> HoldingsFrom(BrokerReconciliationSnapshot snapshot) =>
        snapshot.Positions.ToDictionary(
            p => p.Symbol, p => (decimal?)p.Quantity, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps the broker-neutral working-order shape onto the RestingOrder shape
    /// <see cref="ProtectiveStopDecisions"/> was written against. OrderType and the raw row text have no
    /// equivalent in <see cref="BrokerWorkingOrder"/> and are left null/empty — neither is read by any
    /// decision in that type, only Symbol/Side/Price/OrderNo/Quantity are.
    /// </summary>
    private static IReadOnlyList<RestingOrder> RestingFrom(BrokerReconciliationSnapshot snapshot) =>
        snapshot.OpenOrders.Select(o => new RestingOrder(
            o.Symbol, o.Side, null, o.RemainingQuantity is { } q ? (int)q : null, o.Price, o.OrderNo, ""))
            .ToList();

    // ── Alerts ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tells the user, once per stop per session, that a position is not protected.
    ///
    /// <para>
    /// A successful placement needs no alert here — it goes to the broker through
    /// <c>TradingManager</c>, which already broadcasts every execution. What that path cannot report
    /// is the <b>absence</b> of an order: nothing was executed, so nothing is announced, and the
    /// operator is left believing a stop exists. This covers exactly that hole.
    /// </para>
    /// </summary>
    /// <summary>
    /// Claims the once-per-session slot for one (stop, reason) pair, so a condition that persists all
    /// session is reported once rather than every few minutes. Returns false when it has already been
    /// reported today. Shared by the notifier path and by activity-log lines that need the same
    /// restraint for the same reason.
    /// </summary>
    private bool MarkAlerted(ProtectiveStop stop, string reasonKey, DateOnly today)
    {
        if (_alertedFor != today)
        {
            // A new session is a new chance for the same condition to matter, and a stop that failed
            // to go in yesterday failing again today is news.
            _alerted.Clear();
            _alertedFor = today;
        }

        return _alerted.Add($"{stop.StopId}:{reasonKey}");
    }

    private async Task AlertOnceAsync(ProtectiveStop stop, string reasonKey, DateOnly today, string message)
    {
        if (_notifier is null) return;
        if (!MarkAlerted(stop, reasonKey, today)) return;

        try { await _notifier.NotifyAsync(message, TradingTopics.Stop(reasonKey)); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ProtectiveStops] Could not deliver the alert for {StopId}.", stop.StopId);
        }
    }

    private static decimal? TryHeld(IReadOnlyDictionary<string, decimal?> holdings, string symbol) =>
        // A symbol absent from a grid that WAS read is genuinely zero held; a symbol present with a
        // null quantity is a column that could not be parsed, and stays unknown.
        holdings.TryGetValue(symbol, out var quantity) ? quantity : 0m;
}
