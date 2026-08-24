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

/// <summary>
/// Re-materialises eligible DAY orders once per trading date until exact broker fills satisfy the
/// requested quantity. A failed read, unknown submission, or ambiguous order identity always stops
/// automation instead of risking a duplicate trade.
/// </summary>
public sealed class PersistentOrderWorker : BackgroundService
{
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

            var quantity = intent.RemainingQuantity;
            var ownNumbers = placements
                .Select(p => p.BrokerOrderNo?.Trim())
                .Where(number => !string.IsNullOrWhiteSpace(number))
                .Select(number => number!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (intent.Action.Equals("SELL", StringComparison.OrdinalIgnoreCase))
            {
                var availability = SellQuantityRule.Available(
                    snapshot,
                    intent.Symbol,
                    DateTime.UtcNow,
                    TimeSpan.FromSeconds(Math.Max(10, _options.Value.ReconciliationMaxAgeSeconds)),
                    ownNumbers);
                if (!availability.Known)
                    return new(true, false, intent.State, availability.Reason + " Nothing was retried.");
                quantity = PersistentOrderDecisions.QuantityToPlace(
                    intent, intent.FilledQuantity, availability.AvailableQuantity);
                if (quantity <= 0)
                    return new(true, false, intent.State,
                        $"The broker check found no uncommitted {intent.Symbol} holding available to sell.");
            }

            if (PersistentOrderDecisions.FindPossibleBrokerMatch(
                    intent, latest!, quantity, snapshot) is { } brokerEvidence)
            {
                var reason = brokerEvidence + " The retry was stopped to avoid a duplicate order.";
                await _repository.SetPersistentOrderProgressAsync(
                    intentId, intent.FilledQuantity, "attention", reason, ct);
                _activity?.Warn("Orders", $"{intent.Symbol}: retry stopped by broker evidence", reason);
                return new(true, false, "attention", reason);
            }

            var window = _orderWindow.Evaluate();
            if (!window.Allowed)
                return new(true, false, intent.State, "Nothing was retried: " + window.Reason);

            var claim = await _repository.TryClaimPersistentOrderRetryAsync(intentId, today, ct);
            if (!claim.Acquired)
                return new(true, false, intent.State,
                    "The failed attempt changed while the broker was being checked; refresh before retrying.");

            var signal = intent.ToSignal(quantity);
            IReadOnlyList<IReadOnlyList<TradingSignal>> groups = [[signal]];
            var source = BuildSource(intent.IntentId, today, claim.Attempt);
            var policy = _policyProvider.Current();
            var approvalIntent = ApprovalIntent.Create(
                groups, source, policy.Version,
                TimeSpan.FromSeconds(Math.Max(10, _options.Value.ApprovalIntentTtlSeconds)));
            _intentRegistry.Register(approvalIntent);
            var authorization = ExecutionAuthorization.HostToolGate(approvedBy, approvalIntent);

            var result = await ExecuteClaimedAsync(
                intent, today, claim, authorization, ct, quantity);
            var saved = await _repository.GetPersistentOrderAsync(intentId, ct) ?? intent;
            return new(true, result.Accepted, saved.State, result.Reason, result.Execution?.ExecutionId);
        }
        finally
        {
            _runGate.Release();
        }
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
            await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                intent.FilledQuantity, "attention",
                "A placement was claimed but no result was recorded, usually because the process "
                + "stopped during submission. Verify the broker manually before resuming.", ct);
            return;
        }

        if (intent.State is "expiring" or "cancelling")
        {
            if (ownOpen.Count == 0)
            {
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
                await _repository.SetPersistentOrderProgressAsync(intent.IntentId,
                    intent.FilledQuantity, intent.FilledQuantity > 0 ? "partial" : "resting",
                    "The previously unknown placement is now visible in the outstanding book.", ct);
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
            if (!marketNow.IsOpen
                && marketNow.NextOpenPkt is null
                && latestPlacement?.State == "accepted")
            {
                await _repository.SetPersistentOrderPlacementStateAsync(
                    latestPlacement.PlacementId, "lapsed",
                    "The trading date ended with no native order outstanding; its unfilled remainder lapsed.", ct);
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
            new ApprovalContext(null, "persistent-order"));
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

    public static string BuildSource(string intentId, DateOnly sessionDate, int attempt) =>
        $"persistent-order:{intentId}:{sessionDate:yyyyMMdd}:attempt:{attempt}";
}
