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
using TradingAgent.Watchlist;

namespace TradingAgent.Trading;

/// <summary>
/// Keeps protective stops honest: confirms the entry actually filled, places the native stop, and
/// re-places it every session for as long as the position exists.
///
/// <para>
/// <b>Why a separate worker rather than the monitor pass.</b> Both of this worker's readings —
/// holdings and the outstanding book — drive the real browser, and every broker action serialises on
/// one semaphore inside <see cref="AhkBroker"/>. Running them on the monitor's 30-second cadence
/// would put a multi-second page scrape in front of every order submission. So this runs on its own
/// slower clock, and does nothing at all — not even waking the browser — when no stop is open.
/// </para>
///
/// <para>
/// <b>The bias throughout is inaction.</b> Every unreadable value is treated as unknown rather than
/// as zero, and every ambiguity resolves to "do not place". The two mistakes are not symmetric: a
/// stop that failed to go in is visible in the panel and can be placed by hand, whereas a duplicate
/// stop sells the position twice — and this broker exposes no way to cancel a resting order.
/// </para>
/// </summary>
public sealed class ProtectiveStopWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly AhkBroker _broker;
    private readonly PortfolioReader _portfolio;
    private readonly TradingManager _manager;
    private readonly ApprovalGate _approvals;
    private readonly IMarketCalendar _calendar;
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

    /// <summary>
    /// The last holdings read, reused for a few seconds so that arming several stops in a row costs
    /// one page scrape rather than one each. Holdings cannot change between two reads that close
    /// together without an order of ours in between, and an order of ours would move the baseline
    /// out of scope anyway.
    /// </summary>
    private IReadOnlyDictionary<string, decimal?>? _recentHoldings;
    private DateTime _recentHoldingsUtc = DateTime.MinValue;

    private static readonly TimeSpan HoldingsReuseWindow = TimeSpan.FromSeconds(30);

    public ProtectiveStopWorker(
        IServiceScopeFactory scopes,
        AhkBroker broker,
        PortfolioReader portfolio,
        TradingManager manager,
        ApprovalGate approvals,
        IMarketCalendar calendar,
        TradingPolicyProvider policy,
        IOptions<TradingAgentOptions> options,
        ILogger<ProtectiveStopWorker> logger,
        TradingActivityLog? activity = null,
        IUserNotifier? notifier = null)
    {
        _activity  = activity;
        _notifier  = notifier;
        _scopes    = scopes;
        _broker    = broker;
        _portfolio = portfolio;
        _manager   = manager;
        _approvals = approvals;
        _calendar  = calendar;
        _policy    = policy;
        _options   = options;
        _logger    = logger;
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

            try { await RunPassAsync(stoppingToken); }
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

            var holdings = await HoldingsForBaselineAsync();
            if (holdings is null) return;

            var held = TryHeld(holdings, stop.Symbol);
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

    /// <summary>Reads holdings, reusing a very recent read. ASSUMES the baseline gate is held.</summary>
    private async Task<IReadOnlyDictionary<string, decimal?>?> HoldingsForBaselineAsync()
    {
        if (_recentHoldings is not null && DateTime.UtcNow - _recentHoldingsUtc < HoldingsReuseWindow)
            return _recentHoldings;

        var holdings = await ReadHoldingsAsync();
        if (holdings is not null)
        {
            _recentHoldings = holdings;
            _recentHoldingsUtc = DateTime.UtcNow;
        }
        return holdings;
    }

    private async Task RunPassAsync(CancellationToken ct)
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

        // ONE browser session for the whole pass. Both reads and any stop this pass places drive the
        // same portal, and the broker's on-demand lifecycle would otherwise launch, log in and close
        // Chromium separately for each of them — the visible symptom being a browser window that pops
        // up two or three times every few minutes.
        await using var session = _broker.LeaseSession();

        _activity?.Info("Stops", $"Checking {stops.Count} protective stop(s)");

        // One holdings read and one book read serve every stop in the pass. Reading per-stop would
        // multiply the most expensive thing this worker does by the number of positions held.
        //
        // The book read is also what makes overnight correctness work, and the reason is not obvious:
        // PSX orders are DAY orders, and the broker cancels every resting order at the close — confirmed
        // live on 2026-08-19, where a protective sell that had rested since 10:00 was gone minutes after
        // the bell along with the whole book. So a stop this worker placed yesterday does not exist today,
        // whatever the ledger remembers about placing it. Nothing here needs to special-case that as long
        // as the decision is driven by what is ACTUALLY resting rather than by what was recorded: the
        // first pass after the open sees an unprotected position and places the stop again. Anything that
        // starts trusting the stored state instead would leave positions unprotected every morning while
        // reporting them protected.
        var holdings = await ReadHoldingsAsync();
        var resting  = await ReadRestingAsync();
        var today    = DateOnly.FromDateTime(market.PktNow);

        // Share this read with any baseline capture that lands in the next few seconds, so arming a
        // stop just after a pass does not scrape the same page twice.
        if (holdings is not null)
        {
            _recentHoldings = holdings;
            _recentHoldingsUtc = DateTime.UtcNow;
        }

        foreach (var stop in stops)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (stop.State == "pending_fill")
                    await WatchForFillAsync(repository, stop, holdings, resting, ct);
                else if (stop.State == "active")
                    await MaintainAsync(repository, stop, holdings, resting, today, ct);
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
        var decision = ProtectiveStopDecisions.DecidePlacement(
            stop, held, today, resting ?? []);

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

        await PlaceNativeStopAsync(repository, stop, decision, today, ct);
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
    /// Holdings by symbol, or null when the grid could not be read at all. The distinction is the
    /// point: an empty dictionary would mean "you hold nothing", which is a very different claim.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, decimal?>?> ReadHoldingsAsync()
    {
        try
        {
            var snapshot = await _portfolio.GetPortfolioAsync();
            return snapshot.Holdings.ToDictionary(
                h => h.Symbol, h => h.Quantity, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ProtectiveStops] Holdings could not be read this pass.");
            _activity?.Warn("Stops", "Holdings could not be read this pass", ex.Message);
            return null;
        }
    }

    private async Task<IReadOnlyList<RestingOrder>?> ReadRestingAsync()
    {
        try { return await _broker.GetOutstandingOrdersAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ProtectiveStops] The outstanding book could not be read this pass.");
            _activity?.Warn("Stops", "The outstanding order book could not be read this pass", ex.Message);
            return null;
        }
    }

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
    private async Task AlertOnceAsync(ProtectiveStop stop, string reasonKey, DateOnly today, string message)
    {
        if (_notifier is null) return;

        if (_alertedFor != today)
        {
            // A new session is a new chance for the same condition to matter, and a stop that failed
            // to go in yesterday failing again today is news.
            _alerted.Clear();
            _alertedFor = today;
        }

        if (!_alerted.Add($"{stop.StopId}:{reasonKey}")) return;

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
