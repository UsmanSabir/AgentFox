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
using TradingAgent.Risk;

namespace TradingAgent.Trading;

public sealed record PersistentOrderSubmissionResult(
    bool Accepted,
    PersistentOrderIntent Intent,
    TradingExecutionResult? Execution,
    string Reason);

public sealed record PersistentOrderRetryResult(
    bool Found,
    bool Placed,
    string State,
    string Message,
    string? ExecutionId = null);

public sealed record PersistentOrderResolveResult(
    bool Found,
    bool Applied,
    string State,
    string Message);

/// <summary>
/// What the broker is resting for one intent's symbol, split by whether this ledger can name it.
/// </summary>
/// <param name="Ours">Resting orders a placement row already claims.</param>
/// <param name="Unclaimed">
/// Resting orders matching this intent that NO placement claims. A candidate, never a conclusion —
/// shape is not identity, so these are shown to a person rather than acted on.
/// </param>
public sealed record PersistentOrderBrokerView(
    bool Read,
    string State,
    IReadOnlyList<BrokerWorkingOrder> Ours,
    IReadOnlyList<BrokerWorkingOrder> Unclaimed,
    string Message);

/// <summary>
/// Re-materialises eligible DAY orders once per trading date until exact broker fills satisfy the
/// requested quantity. A failed read, unknown submission, or ambiguous order identity always stops
/// automation instead of risking a duplicate trade.
/// </summary>
public sealed class PersistentOrderWorker : BackgroundService, IMarketSessionOpenParticipant
{
    public string Name => "persistent DAY orders";
    public int Order => 300;

    private readonly ITradingRepository _repository;
    private readonly TradingAgent.Manager.TradingManager _manager;
    private readonly ApprovalGate _approvals;
    private readonly OrderWindow _orderWindow;
    private readonly IMarketCalendar _calendar;
    private readonly TradingReconciliationState _reconciliation;
    private readonly IBrokerStateReader _brokerStateReader;
    private readonly BrokerOrderCancellationService _cancellations;
    private readonly TradingPolicyProvider _policyProvider;
    private readonly ApprovalIntentRegistry _intentRegistry;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly TradingActivityLog? _activity;
    private readonly ILogger<PersistentOrderWorker> _logger;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public PersistentOrderWorker(
        ITradingRepository repository,
        TradingAgent.Manager.TradingManager manager,
        ApprovalGate approvals,
        OrderWindow orderWindow,
        IMarketCalendar calendar,
        TradingReconciliationState reconciliation,
        IBrokerStateReader brokerStateReader,
        BrokerOrderCancellationService cancellations,
        TradingPolicyProvider policyProvider,
        ApprovalIntentRegistry intentRegistry,
        IOptions<TradingAgentOptions> options,
        ILogger<PersistentOrderWorker> logger,
        TradingActivityLog? activity = null)
    {
        _repository = repository;
        _manager = manager;
        _approvals = approvals;
        _orderWindow = orderWindow;
        _calendar = calendar;
        _reconciliation = reconciliation;
        _brokerStateReader = brokerStateReader;
        _cancellations = cancellations;
        _policyProvider = policyProvider;
        _intentRegistry = intentRegistry;
        _options = options;
        _logger = logger;
        _activity = activity;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(
            Math.Clamp(_options.Value.PersistentOrderPollSeconds, 15, 600));
        _logger.LogInformation(
            "[PersistentOrders] Worker started. Interval={Seconds}s.", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try { await RunNowAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[PersistentOrders] Lifecycle pass failed.");
            }
        }
    }

    /// <summary>Creates an intent and makes its explicitly-authorised first placement immediately.</summary>
    public async Task<PersistentOrderSubmissionResult> CreateAndSubmitAsync(
        PersistentOrderIntent intent,
        ExecutionAuthorization? authorization,
        CancellationToken ct = default)
    {
        if (PersistentOrderDecisions.ValidateEligibility(intent.OrderType) is { } problem)
            return new(false, intent, null, problem);

        await _runGate.WaitAsync(ct);
        try
        {
            await _repository.SavePersistentOrderAsync(intent, ct);
            var date = DateOnly.FromDateTime(_calendar.GetStatus().PktNow);
            var result = await PlaceAsync(intent, date, authorization, ct);
            var saved = await _repository.GetPersistentOrderAsync(intent.IntentId, ct) ?? intent;
            return result with { Intent = saved };
        }
        finally
        {
            _runGate.Release();
        }
    }

    public async Task RunNowAsync(CancellationToken ct = default)
    {
        await _runGate.WaitAsync(ct);
        try
        {
            var intents = await _repository.GetPersistentOrdersAsync(openOnly: true, ct);
            if (intents.Count == 0) return;

            var snapshot = _reconciliation.Current;
            var maxAge = TimeSpan.FromSeconds(
                Math.Max(10, _options.Value.ReconciliationMaxAgeSeconds));
            if (!snapshot.Supported || !snapshot.Healthy || DateTime.UtcNow - snapshot.CheckedUtc > maxAge)
            {
                _logger.LogWarning(
                    "[PersistentOrders] {Count} intent(s) paused: reconciliation is not healthy/fresh: {Reason}",
                    intents.Count, snapshot.Reason);
                return;
            }

            var market = _calendar.GetStatus();
            var today = DateOnly.FromDateTime(market.PktNow);
            foreach (var original in intents)
            {
                ct.ThrowIfCancellationRequested();
                try { await MaintainAsync(original, snapshot, today, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "[PersistentOrders] {IntentId} ({Symbol}) failed its lifecycle pass.",
                        original.IntentId, original.Symbol);
                }
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    public Task RunAtMarketOpenAsync(MarketSessionOpenContext context, CancellationToken ct) =>
        RunNowAsync(ct);

    public async Task<(bool Completed, string State, string Message)> CancelAsync(
        string intentId, CancellationToken ct = default)
    {
        await _runGate.WaitAsync(ct);
        try
        {
            var intent = await _repository.GetPersistentOrderAsync(intentId, ct);
            if (intent is null) return (false, "missing", "Persistent order was not found.");
            if (intent.IsTerminal) return (true, intent.State, $"Intent is already {intent.State}.");

            await _repository.TrySetPersistentOrderStateAsync(
                intentId,
                ["active", "placing", "resting", "partial", "attention", "expiring"],
                "cancelling", "Cancellation requested by the operator.", ct);

            var placements = await _repository.GetPersistentOrderPlacementsAsync(intentId, ct);
            foreach (var orderNo in placements
                         .Select(p => p.BrokerOrderNo)
                         .Where(n => !string.IsNullOrWhiteSpace(n))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var cancelled = await _cancellations.CancelExactAsync(orderNo!, ct);
                if (!cancelled.Gone)
                {
                    await _repository.SetPersistentOrderProgressAsync(
                        intentId, intent.FilledQuantity, "cancelling", cancelled.Message, ct);
                    return (false, "cancelling", cancelled.Message);
                }
            }

            await _repository.SetPersistentOrderProgressAsync(
                intentId, intent.FilledQuantity, "cancelled",
                "Cancelled by the operator; no broker order remains outstanding.", ct);
            return (true, "cancelled", "Persistent order cancelled and outstanding placements verified gone.");
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// Explicit operator recovery for a definitively failed attempt. A fresh, complete broker snapshot
    /// is mandatory and is searched for a matching resting order or fill before a second claim is made.
    /// Unknown/ambiguous outcomes never enter this path.
    /// </summary>
    public async Task<PersistentOrderRetryResult> RetryFailedTodayAsync(
        string intentId, string approvedBy, CancellationToken ct = default)
    {
        await _runGate.WaitAsync(ct);
        try
        {
            var intent = await _repository.GetPersistentOrderAsync(intentId, ct);
            if (intent is null)
                return new(false, false, "missing", "Persistent order was not found.");

            var today = DateOnly.FromDateTime(_calendar.GetStatus().PktNow);
            var placements = await _repository.GetPersistentOrderPlacementsAsync(intentId, ct);
            var latest = placements.LastOrDefault();
            if (!PersistentOrderDecisions.CanRetryFailedToday(
                    intent, latest, DateTime.UtcNow, today, out var eligibilityReason))
                return new(true, false, intent.State, eligibilityReason);

            var snapshot = await _brokerStateReader.ReadSnapshotAsync(ct);
            _reconciliation.Update(snapshot);
            if (!snapshot.Supported || !snapshot.Healthy)
            {
                return new(true, false, intent.State,
                    "The broker check was incomplete, so nothing was retried: " + snapshot.Reason);
            }

            intent = await _repository.GetPersistentOrderAsync(intentId, ct) ?? intent;
            if (intent.RemainingQuantity <= 0)
            {
                await _repository.SetPersistentOrderProgressAsync(
                    intentId, intent.FilledQuantity, "fulfilled",
                    $"Broker recheck confirmed the full {intent.Quantity} share(s) filled; no retry was sent.", ct);
                return new(true, false, "fulfilled", "The broker already reports the requested quantity filled.");
            }

            var prep = await PrepareRetryAsync(intent, placements, latest!, snapshot, ct);
            if (!prep.Ready)
            {
                if (!prep.RequiresAttention)
                    return new(true, false, intent.State, prep.Reason!);

                await _repository.SetPersistentOrderProgressAsync(
                    intentId, intent.FilledQuantity, "attention", prep.Reason!, ct);
                _activity?.Warn("Orders", $"{intent.Symbol}: retry stopped by broker evidence", prep.Reason!);
                return new(true, false, "attention", prep.Reason!);
            }

            var claim = await _repository.TryClaimPersistentOrderRetryAsync(intentId, today, ct);
            if (!claim.Acquired)
                return new(true, false, intent.State,
                    "The failed attempt changed while the broker was being checked; refresh before retrying.");

            var signal = intent.ToSignal(prep.Quantity);
            IReadOnlyList<IReadOnlyList<TradingSignal>> groups = [[signal]];
            var source = BuildSource(intent.IntentId, today, claim.Attempt);
            var policy = _policyProvider.Current();
            var approvalIntent = ApprovalIntent.Create(
                groups, source, policy.Version,
                TimeSpan.FromSeconds(Math.Max(10, _options.Value.ApprovalIntentTtlSeconds)));
            _intentRegistry.Register(approvalIntent);
            var authorization = ExecutionAuthorization.HostToolGate(approvedBy, approvalIntent);

            var result = await ExecuteClaimedAsync(
                intent, today, claim, authorization, ct, prep.Quantity);
            var saved = await _repository.GetPersistentOrderAsync(intentId, ct) ?? intent;
            return new(true, result.Accepted, saved.State, result.Reason, result.Execution?.ExecutionId);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// Explicit, attended resolution for an intent stuck in "attention" over a broker outcome from a
    /// PRIOR trading date. The broker's own activity API only reports the CURRENT trading day (see
    /// <c>AhkPortalClient.GetActivityLogAsync</c>), so once the day has turned over there is no
    /// evidence left for AgentFox to check itself — a live recheck of "today's" book proves nothing
    /// about what happened yesterday. Only a human who has checked the broker's own order
    /// history/statement outside the app can say what actually happened, so this method never
    /// infers an outcome; it only records what the operator attests to having verified.
    /// </summary>
    public async Task<PersistentOrderResolveResult> ResolveAttentionAsync(
        string intentId, string? resolution, int? filledQuantity, string note, string approvedBy,
        CancellationToken ct = default)
    {
        await _runGate.WaitAsync(ct);
        try
        {
            var intent = await _repository.GetPersistentOrderAsync(intentId, ct);
            if (intent is null)
                return new(false, false, "missing", "Persistent order was not found.");
            if (intent.State != "attention")
                return new(true, false, intent.State,
                    $"Only an intent in 'attention' can be resolved this way; this one is '{intent.State}'.");

            int confirmed;
            string newState;
            switch (resolution)
            {
                case "not_filled":
                    confirmed = intent.FilledQuantity;
                    newState = "active";
                    break;
                case "filled":
                    confirmed = intent.Quantity;
                    newState = "fulfilled";
                    break;
                case "partial":
                    if (filledQuantity is not { } q || q <= 0 || q >= intent.Quantity)
                        return new(true, false, intent.State,
                            $"A partial resolution needs a filled quantity between 1 and {intent.Quantity - 1}.");
                    confirmed = q;
                    newState = "partial";
                    break;
                default:
                    return new(true, false, intent.State,
                        "Resolution must be one of: not_filled, partial, filled.");
            }

            var reason = $"Operator {approvedBy} resolved the unobserved "
                + $"{intent.LastAttemptSessionDate:yyyy-MM-dd} outcome from a broker check outside the "
                + $"app: {resolution} ({confirmed}/{intent.Quantity} share(s)). Note: {note}";

            var applied = await _repository.SetPersistentOrderProgressAsync(
                intentId, confirmed, newState, reason, ct);
            if (!applied)
                return new(true, false, intent.State,
                    "The intent changed while resolving; refresh and retry.");

            _activity?.Info("Orders",
                $"{intent.Symbol}: attention resolved by {approvedBy} ({resolution})", reason);
            return new(true, true, newState, reason);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// Settles a persistent order's "attention" state left over from a PRIOR trading date using the
    /// account's own custody position as ground truth. Returns false (leaving the intent in
    /// "attention") only when no reconciliation snapshot exists from before the ambiguous placement —
    /// there is then genuinely no evidence to reason from, and only an operator who checks the
    /// broker's own history can resolve it (<see cref="ResolveAttentionAsync"/>).
    /// </summary>
    private async Task<bool> TryResolveAttentionFromHoldingsAsync(
        PersistentOrderIntent intent,
        PersistentOrderPlacement latestPlacement,
        BrokerReconciliationSnapshot snapshot,
        CancellationToken ct)
    {
        var baseline = await _repository.FindHoldingQuantityBeforeAsync(
            intent.Symbol, latestPlacement.CreatedUtc, ct);
        if (baseline is null) return false;

        var current = snapshot.Positions
            .FirstOrDefault(p => string.Equals(p.Symbol, intent.Symbol, StringComparison.OrdinalIgnoreCase))
            ?.Quantity ?? 0m;

        // BUY should only ever raise the position, SELL only ever lower it. A move the wrong way (an
        // unrelated trade in between) is treated the same as no move — it is not evidence THIS order
        // filled, and the conservative reading is "not filled".
        var delta = intent.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? current - baseline.Value
            : baseline.Value - current;

        if (delta <= 0)
        {
            var unchanged = $"Holdings for {intent.Symbol} are unchanged since before the "
                + $"{latestPlacement.SessionDate:yyyy-MM-dd} placement ({baseline.Value:0.##} -> "
                + $"{current:0.##} share(s)); treating that attempt as not filled and resuming daily retries.";
            await _repository.SetPersistentOrderProgressAsync(
                intent.IntentId, intent.FilledQuantity, "active", unchanged, ct);
            _activity?.Info("Orders", $"{intent.Symbol}: attention auto-resolved (holdings unchanged)", unchanged);
            return true;
        }

        var filled = (int)Math.Min(delta, intent.RemainingQuantity);
        var newFilled = intent.FilledQuantity + filled;
        var newState = newFilled >= intent.Quantity ? "fulfilled" : "partial";
        var moved = $"Holdings for {intent.Symbol} moved from {baseline.Value:0.##} to {current:0.##} "
            + $"share(s) since the {latestPlacement.SessionDate:yyyy-MM-dd} placement — consistent with "
            + $"{filled} share(s) filling.";
        await _repository.SetPersistentOrderProgressAsync(intent.IntentId, newFilled, newState, moved, ct);
        _activity?.Warn("Orders",
            $"{intent.Symbol}: attention auto-resolved from holdings evidence ({filled} filled)", moved);
        return true;
    }

    private readonly record struct RetryPreparation(
        bool Ready, bool RequiresAttention, string? Reason, int Quantity);

    /// <summary>
    /// The safety checks a retry of today's failed attempt must pass before it may claim a new attempt:
    /// current SELL availability, no broker-side evidence the order already exists (duplicate guard),
    /// and the order window still accepting orders. Shared by the operator-triggered
    /// <see cref="RetryFailedTodayAsync"/> and the unattended <see cref="TryAutoRetryFailedTodayAsync"/>
    /// so the two paths can never drift on what counts as safe to retry.
    /// </summary>
    private async Task<RetryPreparation> PrepareRetryAsync(
        PersistentOrderIntent intent,
        IReadOnlyList<PersistentOrderPlacement> placements,
        PersistentOrderPlacement latestPlacement,
        BrokerReconciliationSnapshot snapshot,
        CancellationToken ct)
    {
        var quantity = intent.RemainingQuantity;
        if (intent.Action.Equals("SELL", StringComparison.OrdinalIgnoreCase))
        {
            var ownNumbers = placements
                .Select(p => p.BrokerOrderNo?.Trim())
                .Where(number => !string.IsNullOrWhiteSpace(number))
                .Select(number => number!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var availability = SellQuantityRule.Available(
                snapshot,
                intent.Symbol,
                DateTime.UtcNow,
                TimeSpan.FromSeconds(Math.Max(10, _options.Value.ReconciliationMaxAgeSeconds)),
                ownNumbers);
            if (!availability.Known)
                return new(false, false, availability.Reason + " Nothing was retried.", 0);
            quantity = PersistentOrderDecisions.QuantityToPlace(
                intent, intent.FilledQuantity, availability.AvailableQuantity);
            if (quantity <= 0)
                return new(false, false,
                    $"The broker check found no uncommitted {intent.Symbol} holding available to sell.", 0);
        }

        if (PersistentOrderDecisions.FindPossibleBrokerMatch(
                intent, latestPlacement, quantity, snapshot) is { } brokerEvidence)
        {
            return new(false, true,
                brokerEvidence + " The retry was stopped to avoid a duplicate order.", 0);
        }

        var window = _orderWindow.Evaluate();
        if (!window.Allowed)
            return new(false, false, "Nothing was retried: " + window.Reason, 0);

        return new(true, false, null, quantity);
    }

    /// <summary>
    /// Unattended counterpart to <see cref="RetryFailedTodayAsync"/>, called once per poll cycle from
    /// <see cref="MaintainAsync"/> for a persistent order whose latest attempt today definitively
    /// failed. Retries ONLY when the policy ladder already authorises unattended execution for this
    /// order — the same <see cref="ApprovalGate.Decide"/> check a fresh attempt goes through. In any
    /// other mode this declines without acting, exactly as if nobody had asked, and the order is left
    /// for a human to retry by hand or for the next trading date — an unattended background loop must
    /// never grant itself the authority a human-clicked retry claims via HostToolGate.
    /// Returns true when it recorded a terminal-for-this-pass outcome (a retry attempt, or an
    /// "attention" state from duplicate broker evidence) so the caller must not overwrite it with the
    /// generic "waits for next date" message.
    /// </summary>
    private async Task<bool> TryAutoRetryFailedTodayAsync(
        PersistentOrderIntent intent,
        IReadOnlyList<PersistentOrderPlacement> placements,
        PersistentOrderPlacement latestPlacement,
        BrokerReconciliationSnapshot snapshot,
        DateOnly today,
        CancellationToken ct)
    {
        if (!PersistentOrderDecisions.CanRetryFailedToday(
                intent, latestPlacement, DateTime.UtcNow, today, out _))
            return false;

        var prep = await PrepareRetryAsync(intent, placements, latestPlacement, snapshot, ct);
        if (!prep.Ready)
        {
            if (!prep.RequiresAttention) return false;

            await _repository.SetPersistentOrderProgressAsync(
                intent.IntentId, intent.FilledQuantity, "attention", prep.Reason!, ct);
            _activity?.Warn("Orders", $"{intent.Symbol}: retry stopped by broker evidence", prep.Reason!);
            return true;
        }

        var signal = intent.ToSignal(prep.Quantity);
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups = [[signal]];
        var source = BuildSource(intent.IntentId, today, intent.AttemptCount + 1);
        var approval = _approvals.Decide(
            groups, source,
            new ApprovalContext(null, "persistent-order-retry", intent.OperatorOriginated));
        if (!approval.MayProceed) return false;

        var claim = await _repository.TryClaimPersistentOrderRetryAsync(intent.IntentId, today, ct);
        if (!claim.Acquired) return false;

        await ExecuteClaimedAsync(intent, today, claim, approval.Authorization, ct, prep.Quantity);
        return true;
    }

    private async Task MaintainAsync(
        PersistentOrderIntent original,
        BrokerReconciliationSnapshot snapshot,
        DateOnly today,
        CancellationToken ct)
    {
        // Re-read because repository projection folds newly-recorded fills into FilledQuantity.
        var intent = await _repository.GetPersistentOrderAsync(original.IntentId, ct) ?? original;
        var placements = await _repository.GetPersistentOrderPlacementsAsync(intent.IntentId, ct);
        var ownNumbers = placements
            .Select(p => p.BrokerOrderNo?.Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownOpen = snapshot.OpenOrders
            .Where(o => ownNumbers.Contains(o.OrderNo.Trim()))
            .ToList();

        if (intent.FilledQuantity >= intent.Quantity)
        {
            if (ownOpen.Count > 0)
            {
                foreach (var open in ownOpen)
                {
                    var cancelled = await _cancellations.CancelExactAsync(open.OrderNo, ct);
                    if (!cancelled.Gone)
                    {
                        await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                            intent.FilledQuantity, "attention",
                            "The requested quantity is filled but a broker order may still be resting. "
                            + cancelled.Message, ct);
                        return;
                    }
                }
            }

            await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                intent.FilledQuantity, "fulfilled",
                $"Fully filled: {intent.FilledQuantity}/{intent.Quantity} share(s).", ct);
            _activity?.Info("Orders", $"{intent.Symbol}: persistent order fulfilled",
                $"{intent.FilledQuantity:N0}/{intent.Quantity:N0} share(s) filled.");
            return;
        }

        if (intent.State == "placing")
        {
            // The order may well have reached the broker — this state means we stopped before learning.
            // Try to find it before falling back to "a human must check", because the fallback used to
            // be permanent: nothing else in this worker ever looks for an order it cannot name.
            if (await TryAdoptOrphanAsync(intent, placements, snapshot, ct)) return;

            await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                intent.FilledQuantity, "attention",
                "A placement was claimed but no result was recorded, usually because the process "
                + "stopped during submission. "
                + DescribeOrphanSearch(intent, placements, snapshot, submissionUnaccountedFor: true), ct);
            return;
        }

        if (intent.State is "expiring" or "cancelling")
        {
            if (ownOpen.Count == 0)
            {
                // "No order remains" is a claim about the BROKER, and ownOpen only knows the numbers this
                // ledger managed to write down. Measured 2026-09-01: a cancel was refused because the one
                // recorded number was stale, and this branch then reported the intent "cancelled; no
                // native order remains outstanding" while a 50-share SELL went on resting at the venue
                // and blocking every further sell. An unrecorded order is not an absent order — the
                // unknown-is-never-zero rule, applied to our own bookkeeping.
                if (await TryAdoptOrphanAsync(intent, placements, snapshot, ct)) return;

                var unclaimed = PersistentOrderDecisions.FindUnclaimedBrokerOrders(
                    intent, placements, snapshot);
                if (unclaimed.Count > 0)
                {
                    await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                        intent.FilledQuantity, "attention",
                        $"Not marked {(intent.State == "cancelling" ? "cancelled" : "expired")}: no "
                        + "order of ours could be named, but the broker is still resting "
                        + $"{DescribeCandidates(unclaimed)} on {intent.Symbol}. Cancel the right one by "
                        + "its number, or confirm it is not this order's.", ct);
                    return;
                }

                var terminal = intent.State == "cancelling" ? "cancelled" : "expired";
                await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                    intent.FilledQuantity, terminal,
                    $"Intent {terminal}; no native order remains outstanding.", ct);
            }
            return;
        }

        if (DateTime.UtcNow >= intent.ExpiresUtc)
        {
            if (ownOpen.Count == 0)
            {
                await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                    intent.FilledQuantity, "expired",
                    $"Expired with {intent.RemainingQuantity} share(s) unfilled.", ct);
                return;
            }

            await _repository.TrySetPersistentOrderStateAsync(intent.IntentId,
                ["active", "resting", "partial", "attention"], "expiring",
                "Expiry reached; cancelling the still-resting native order.", ct);
            foreach (var open in ownOpen)
            {
                var cancelled = await _cancellations.CancelExactAsync(open.OrderNo, ct);
                if (!cancelled.Gone)
                {
                    await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                        intent.FilledQuantity, "expiring", cancelled.Message, ct);
                    return;
                }
            }
            await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                intent.FilledQuantity, "expired",
                $"Expired with {intent.RemainingQuantity} share(s) unfilled; resting order cancelled.", ct);
            return;
        }

        if (intent.State == "attention")
        {
            // Exact evidence can safely settle an earlier unknown outcome; absence cannot.
            if (ownOpen.Count > 0)
            {
                await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                    intent.FilledQuantity, intent.FilledQuantity > 0 ? "partial" : "resting",
                    "The previously unknown placement is now visible in the outstanding book.", ct);
                return;
            }

            // The broker's own activity log only ever reports TODAY (see
            // AhkPortalClient.GetActivityLogAsync), so once the ambiguous trading date has passed there
            // is nothing left there to check. Custody holdings are a different, independent signal that
            // does not expire overnight: comparing what the account holds now against what it held right
            // before the ambiguous placement is ground truth about whether that order actually moved a
            // position, without guessing from silence. This is what keeps a persistent order genuinely
            // unattended day to day — ResolveAttentionAsync (an explicit operator action) remains the
            // fallback for the rare case where no reconciliation snapshot exists that far back at all.
            if (intent.LastAttemptSessionDate is { } priorDate && priorDate < today
                && placements.LastOrDefault(p => p.SessionDate == priorDate) is { } attentionPlacement)
            {
                await TryResolveAttentionFromHoldingsAsync(intent, attentionPlacement, snapshot, ct);
            }
            return;
        }

        if (ownOpen.Count > 0)
        {
            await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                intent.FilledQuantity, intent.FilledQuantity > 0 ? "partial" : "resting",
                $"Broker order #{ownOpen[0].OrderNo} is resting; "
                + $"{intent.RemainingQuantity} share(s) remain in the overall intent.", ct);
            return;
        }

        var latestPlacement = placements.LastOrDefault();
        if (PersistentOrderDecisions.PriorOutcomeWasNotObserved(intent, latestPlacement, today))
        {
            var priorDate = intent.LastAttemptSessionDate!.Value;
            await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                intent.FilledQuantity, "attention",
                $"Order #{latestPlacement!.BrokerOrderNo ?? "unknown"} was accepted on "
                + $"{priorDate:yyyy-MM-dd}, but its end-of-day outcome was not observed. "
                + "Automation stopped rather than risk duplicating a fill that occurred while "
                + "reconciliation was offline.", ct);
            return;
        }

        if (intent.LastAttemptSessionDate == today)
        {
            var marketNow = _calendar.GetStatus();
            // NextOpenPkt now projects across weekends and holidays. Today's session has ended when
            // the next opening belongs to a later trading date; a same-day opening is Friday's lunch
            // break and the native order may still be relevant to today's lifecycle.
            var dayEnded = !marketNow.IsOpen
                && (marketNow.NextOpenPkt is null
                    || DateOnly.FromDateTime(marketNow.NextOpenPkt.Value) > today);

            if (latestPlacement?.State == "accepted" && dayEnded)
            {
                await _repository.SetPersistentOrderPlacementStateAsync(
                    latestPlacement.PlacementId, "lapsed",
                    "The trading date ended with no native order outstanding; its unfilled remainder lapsed.", ct);
            }
            else if (string.Equals(latestPlacement?.State, "failed", StringComparison.OrdinalIgnoreCase)
                     && !dayEnded
                     && await TryAutoRetryFailedTodayAsync(intent, placements, latestPlacement!, snapshot, today, ct))
            {
                return;
            }

            await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                intent.FilledQuantity,
                intent.FilledQuantity > 0 ? "partial" : "active",
                intent.FilledQuantity > 0
                    ? $"Partially filled ({intent.FilledQuantity}/{intent.Quantity}); the native order "
                      + "is no longer outstanding. The remainder waits for the next trading date."
                    : "Today's native order is no longer outstanding and recorded no fill. The next "
                      + "placement is eligible on the next trading date.", ct);
            return;
        }

        if (!_orderWindow.Evaluate().Allowed) return;

        if (!PersistentOrderDecisions.MayAttempt(
                intent, DateTime.UtcNow, today, ownOrderIsResting: false, out _))
            return;

        int? availableToSell = null;
        if (intent.Action.Equals("SELL", StringComparison.OrdinalIgnoreCase))
        {
            var availability = SellQuantityRule.Available(
                snapshot,
                intent.Symbol,
                DateTime.UtcNow,
                TimeSpan.FromSeconds(Math.Max(
                    10, _options.Value.ReconciliationMaxAgeSeconds)),
                ownNumbers);
            if (!availability.Known)
            {
                await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                    intent.FilledQuantity, intent.FilledQuantity > 0 ? "partial" : "active",
                    availability.Reason + " No recurring order was placed.", ct);
                return;
            }
            availableToSell = availability.AvailableQuantity;
        }

        var quantity = PersistentOrderDecisions.QuantityToPlace(
            intent, intent.FilledQuantity, availableToSell);
        if (quantity <= 0)
        {
            await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                intent.FilledQuantity, intent.FilledQuantity > 0 ? "partial" : "active",
                "No uncommitted holding is currently available for this recurring SELL; no order was placed.", ct);
            return;
        }

        var signal = intent.ToSignal(quantity);
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups = [[signal]];
        var source = BuildSource(intent.IntentId, today, intent.AttemptCount + 1);
        var approval = _approvals.Decide(groups, source,
            new ApprovalContext(null, "persistent-order", intent.OperatorOriginated));
        if (!approval.MayProceed)
        {
            await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                intent.FilledQuantity, intent.FilledQuantity > 0 ? "partial" : "active",
                $"Ready to place, but unattended execution is not authorised: {approval.Reason}", ct);
            return;
        }

        await PlaceAsync(intent with { FilledQuantity = intent.FilledQuantity },
            today, approval.Authorization, ct, quantity);
    }

    private async Task<PersistentOrderSubmissionResult> PlaceAsync(
        PersistentOrderIntent intent,
        DateOnly sessionDate,
        ExecutionAuthorization? authorization,
        CancellationToken ct,
        int? quantityOverride = null)
    {
        var quantity = quantityOverride ?? intent.RemainingQuantity;
        if (quantity <= 0)
            return new(true, intent, null, "The order is already fully filled.");

        var claim = await _repository.TryClaimPersistentOrderAttemptAsync(
            intent.IntentId, sessionDate, ct);
        if (!claim.Acquired)
            return new(false, intent, null,
                $"A placement was already claimed for {sessionDate:yyyy-MM-dd}.");

        return await ExecuteClaimedAsync(
            intent, sessionDate, claim, authorization, ct, quantity);
    }

    private async Task<PersistentOrderSubmissionResult> ExecuteClaimedAsync(
        PersistentOrderIntent intent,
        DateOnly sessionDate,
        PersistentOrderAttemptClaim claim,
        ExecutionAuthorization? authorization,
        CancellationToken ct,
        int quantity)
    {

        var signal = intent.ToSignal(quantity);
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups = [[signal]];
        var source = BuildSource(intent.IntentId, sessionDate, claim.Attempt);
        TradingExecutionResult execution;
        try
        {
            execution = await _manager.ExecuteGroupsAsync(groups, source, authorization, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var unknown = new PersistentOrderPlacement
            {
                PlacementId = Guid.NewGuid().ToString("N"),
                IntentId = intent.IntentId,
                SessionDate = sessionDate,
                Attempt = claim.Attempt,
                Quantity = quantity,
                State = "unknown",
                RequestedPrice = intent.Price,
                Message = ex.Message
            };
            await _repository.RecordPersistentOrderPlacementAsync(unknown, "attention",
                $"Submission threw and may have reached the broker: {ex.Message}", ct);
            return new(false, intent, null, unknown.Message!);
        }

        var result = execution.Groups.SelectMany(g => g).FirstOrDefault();
        var message = result?.Message ?? execution.Reason;
        var isUnknown = (!execution.Executed
                         && execution.Reason.Contains("unknown", StringComparison.OrdinalIgnoreCase))
                        || message.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("unconfirmed", StringComparison.OrdinalIgnoreCase);
        var accepted = execution.Executed && result is { Success: true };
        var submittedQuantity = result?.Quantity ?? quantity;
        var placement = new PersistentOrderPlacement
        {
            PlacementId = Guid.NewGuid().ToString("N"),
            IntentId = intent.IntentId,
            SessionDate = sessionDate,
            Attempt = claim.Attempt,
            Quantity = submittedQuantity,
            BrokerOrderNo = result?.OrderId,
            ExecutionId = execution.ExecutionId,
            State = accepted ? "accepted" : isUnknown ? "unknown" : "failed",
            RequestedPrice = result?.RequestedPrice ?? intent.Price,
            SubmittedPrice = result?.SubmittedPrice,
            Message = message
        };
        var nextState = accepted ? "resting" : isUnknown ? "attention" : "active";
        var reason = accepted
            ? $"Broker accepted order #{result!.OrderId ?? "unknown"} for {submittedQuantity} share(s)."
            : isUnknown
                ? $"Broker outcome is unknown; automation stopped: {message}"
                : $"No order was placed for this trading date: {message}";
        await _repository.RecordPersistentOrderPlacementAsync(
            placement, nextState, reason, ct);

        _activity?.Record(accepted ? ActivityLevel.Info : ActivityLevel.Warn, "Orders",
            accepted
                ? $"{intent.Symbol}: persistent {intent.Action} placed ({quantity:N0})"
                : $"{intent.Symbol}: persistent order not placed",
            reason);
        return new(accepted, intent, execution, reason);
    }

    /// <summary>
    /// Writes an unaccounted-for broker order onto this intent when the evidence identifies exactly one.
    ///
    /// <para>
    /// The argument has two halves and BOTH are required. First, this intent is known to have sent
    /// something whose outcome was never recorded
    /// (<see cref="PersistentOrderDecisions.HasUnaccountedSubmission"/>) — so an order of ours is missing,
    /// not merely imaginable. Second, exactly one resting order matches its shape and is claimed by no
    /// placement row. One unexplained submission and one unexplained order is an identification; either
    /// alone is a guess.
    /// </para>
    ///
    /// <para>
    /// <b>Two candidates are never resolved by picking one.</b> The caller reports them and stops. This
    /// is the same refusal as <c>BrokerChargeKey.Resolve</c> and <c>ProtectiveStopDecisions</c>'
    /// ambiguity handling, for the same reason: acting on the wrong order number cancels somebody else's
    /// order, and that is not recoverable by retrying.
    /// </para>
    ///
    /// <para>
    /// What it writes is a placement row in the ordinary shape, so cancel, expiry and reconciliation all
    /// start working on this intent again with no special cases. The row's message records that the
    /// number was matched by shape rather than reported by the broker, because that distinction is the
    /// difference between a measurement and an inference and must survive into the audit trail.
    /// </para>
    /// </summary>
    private async Task<bool> TryAdoptOrphanAsync(
        PersistentOrderIntent intent,
        IReadOnlyList<PersistentOrderPlacement> placements,
        BrokerReconciliationSnapshot snapshot,
        CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_calendar.GetStatus().PktNow);
        if (PersistentOrderDecisions.PlanAdoption(intent, placements, snapshot, today)
            is not { } adoption)
            return false;

        // Recorded through the ordinary path so the intent's own state moves with it. The write requires
        // the intent to be in a claimable state, so a concurrent change simply leaves the intent as it
        // was and another pass tries again — the safe outcome.
        await _repository.RecordPersistentOrderPlacementAsync(
            adoption.Placement, "resting", adoption.Reason, ct);

        _activity?.Warn("Orders", $"{intent.Symbol}: adopted unrecorded broker order", adoption.Reason);
        return true;
    }

    private static string DescribeCandidates(IReadOnlyList<BrokerWorkingOrder> candidates) =>
        string.Join(", ", candidates.Select(order =>
            $"#{order.OrderNo} ({order.Side} {order.RemainingQuantity?.ToString() ?? "?"}"
            + $"{(order.Price is { } price ? $" @ {price}" : "")})"));

    /// <summary>
    /// Describes the search for an order this intent cannot name.
    ///
    /// <para>
    /// <b>Only meaningful when something IS unaccounted for.</b> The zero-candidate wording says the
    /// submission never arrived, which is a real and useful conclusion for an intent stuck in
    /// <c>placing</c> — and nonsense for a healthy one, where finding no ORPHAN is the normal, good
    /// answer. Reported 2026-09-01: a resting, fully recorded SYS order was told "the submission most
    /// likely never arrived" directly above the line naming the order that was resting. So the caller
    /// states which question it is asking rather than letting this guess.
    /// </para>
    /// </summary>
    private static string DescribeOrphanSearch(
        PersistentOrderIntent intent,
        IReadOnlyList<PersistentOrderPlacement> placements,
        BrokerReconciliationSnapshot snapshot,
        bool submissionUnaccountedFor)
    {
        var unclaimed = PersistentOrderDecisions.FindUnclaimedBrokerOrders(
            intent, placements, snapshot);
        return unclaimed.Count switch
        {
            0 when submissionUnaccountedFor =>
                $"No unaccounted-for {intent.Symbol} order is resting at the broker, so the submission "
                + "most likely never arrived. Verify the broker before resuming.",
            0 => $"Nothing unexpected: every {intent.Symbol} order at the broker is accounted for by this "
                 + "order's own records.",
            1 when submissionUnaccountedFor =>
                $"One possible match is resting ({DescribeCandidates(unclaimed)}) but it could not be "
                + "adopted automatically. Check it and cancel by number if it is this order's.",
            1 => $"One {intent.Symbol} order is resting that this order's records do not mention "
                 + $"({DescribeCandidates(unclaimed)}). It may belong to something else entirely — check "
                 + "the broker before cancelling it.",
            _ => $"More than one {intent.Symbol} order matches ({DescribeCandidates(unclaimed)}), so "
                 + "which one belongs to this intent cannot be decided here. Cancel the right one by its "
                 + "number."
        };
    }

    /// <summary>
    /// What the broker is actually resting for this intent's symbol, split into what the ledger can name
    /// and what it cannot. Read-only, and the operator-facing half of the orphan problem: where the
    /// evidence cannot identify an order, the honest move is to show it and let a person decide rather
    /// than cancel something we cannot prove is ours.
    /// </summary>
    public async Task<PersistentOrderBrokerView> InspectBrokerOrdersAsync(
        string intentId, CancellationToken ct = default)
    {
        var intent = await _repository.GetPersistentOrderAsync(intentId, ct);
        if (intent is null)
            return new(false, "missing", [], [], "Persistent order was not found.");

        var snapshot = await _brokerStateReader.ReadSnapshotAsync(ct);
        _reconciliation.Update(snapshot);
        if (!snapshot.Supported || !snapshot.Healthy)
            return new(false, intent.State, [], [],
                "The broker's order book could not be read, so nothing is being reported as absent: "
                + snapshot.Reason);

        var placements = await _repository.GetPersistentOrderPlacementsAsync(intentId, ct);
        var claimed = placements
            .Select(placement => placement.BrokerOrderNo?.Trim())
            .Where(number => !string.IsNullOrWhiteSpace(number))
            .Select(number => number!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ours = snapshot.OpenOrders
            .Where(order => claimed.Contains(order.OrderNo.Trim()))
            .ToList();
        var unclaimed = PersistentOrderDecisions.FindUnclaimedBrokerOrders(intent, placements, snapshot);

        return new(true, intent.State, ours, unclaimed,
            ours.Count == 0 && unclaimed.Count == 0
                ? $"Nothing is resting at the broker for {intent.Symbol}."
                : DescribeOrphanSearch(intent, placements, snapshot,
                    PersistentOrderDecisions.HasUnaccountedSubmission(intent, placements)));
    }

    /// <summary>
    /// Cancels ONE broker order by number on the operator's instruction, then records it against this
    /// intent so the two stop disagreeing.
    ///
    /// <para>
    /// The number comes from a person who has looked at the broker's own book, which is the one source
    /// this system cannot second-guess. It is still checked against that book first: an order that is not
    /// resting, or is for a different symbol or side, is refused rather than sent — a mistyped number
    /// must not cancel an unrelated position (CLAUDE.md §6a: never match by number alone).
    /// </para>
    /// </summary>
    public async Task<(bool Applied, string State, string Message)> CancelBrokerOrderAsync(
        string intentId, string orderNo, string approvedBy, CancellationToken ct = default)
    {
        orderNo = (orderNo ?? "").Trim();
        if (orderNo.Length == 0) return (false, "invalid", "No broker order number was supplied.");

        await _runGate.WaitAsync(ct);
        try
        {
            var intent = await _repository.GetPersistentOrderAsync(intentId, ct);
            if (intent is null) return (false, "missing", "Persistent order was not found.");

            var snapshot = await _brokerStateReader.ReadSnapshotAsync(ct);
            _reconciliation.Update(snapshot);
            if (!snapshot.Supported || !snapshot.Healthy)
                return (false, intent.State,
                    "The broker's order book could not be read, so the order was not cancelled: "
                    + snapshot.Reason);

            var target = snapshot.OpenOrders.FirstOrDefault(order =>
                string.Equals(order.OrderNo.Trim(), orderNo, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return (false, intent.State,
                    $"No order #{orderNo} is resting at the broker, so nothing was cancelled. Refresh "
                    + "the broker view — it may already be gone.");

            if (!string.Equals(target.Symbol.Trim(), intent.Symbol.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                return (false, intent.State,
                    $"Order #{orderNo} is for {target.Symbol}, but this intent is for {intent.Symbol}. "
                    + "Refusing to cancel an order on a different symbol.");

            var cancelled = await _cancellations.CancelExactAsync(orderNo, ct);
            if (!cancelled.Gone)
                return (false, intent.State,
                    $"The broker did not confirm order #{orderNo} as gone: {cancelled.Message}");

            var reason =
                $"Operator {approvedBy} cancelled broker order #{orderNo} ({target.Side} "
                + $"{target.RemainingQuantity?.ToString() ?? "?"} {target.Symbol}) by number, after "
                + "checking the broker's own book. Confirmed gone.";

            var placements = await _repository.GetPersistentOrderPlacementsAsync(intentId, ct);
            var alreadyKnown = placements.Any(placement =>
                string.Equals(placement.BrokerOrderNo?.Trim(), orderNo, StringComparison.OrdinalIgnoreCase));
            if (!alreadyKnown)
            {
                // Record it even though it is now cancelled: the audit trail should show that this order
                // belonged to this intent, and the number must never be searched for again as an orphan.
                await _repository.RecordPersistentOrderPlacementAsync(
                    new PersistentOrderPlacement
                    {
                        PlacementId = Guid.NewGuid().ToString("N"),
                        IntentId = intentId,
                        SessionDate = intent.LastAttemptSessionDate
                                      ?? DateOnly.FromDateTime(_calendar.GetStatus().PktNow),
                        Attempt = Math.Max(1, intent.AttemptCount),
                        Quantity = target.RemainingQuantity is { } remaining && remaining > 0
                            ? (int)Math.Min(remaining, intent.Quantity)
                            : intent.RemainingQuantity,
                        BrokerOrderNo = orderNo,
                        State = "cancelled",
                        RequestedPrice = intent.Price,
                        SubmittedPrice = target.Price,
                        Message = reason
                    },
                    intent.State, reason, ct);
            }

            _activity?.Info("Orders", $"{intent.Symbol}: broker order #{orderNo} cancelled by operator",
                reason);
            var saved = await _repository.GetPersistentOrderAsync(intentId, ct) ?? intent;
            return (true, saved.State, reason);
        }
        finally
        {
            _runGate.Release();
        }
    }

    public static string BuildSource(string intentId, DateOnly sessionDate, int attempt) =>
        $"persistent-order:{intentId}:{sessionDate:yyyyMMdd}:attempt:{attempt}";
}
