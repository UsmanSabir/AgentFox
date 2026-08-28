using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;
using TradingAgent.Broker;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Models;
using TradingAgent.Observability;
using TradingAgent.Persistence;
using TradingAgent.Risk;
using TradingAgent.Reconciliation;
using TradingAgent.Watchlist;
using Microsoft.Extensions.Options;

namespace TradingAgent.Manager;

/// <summary>
/// Deterministic execution boundary. It re-checks policy and market eligibility, claims durable
/// idempotency, records transitions, and is the only service allowed to call the broker adapter.
/// </summary>
public sealed class TradingManager
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IBrokerAdapter _broker;
    private readonly ITradingRepository _repository;
    private readonly IMarketCalendar _calendar;
    private readonly TradingAgent.Market.OrderWindow _orderWindow;
    private readonly TradingPolicyProvider _policyProvider;
    private readonly ITradingRiskEngine _riskEngine;
    private readonly TradingReconciliationState _reconciliation;
    private readonly IBrokerStateReader? _brokerStateReader;
    private readonly ApprovalIntentRegistry _intentRegistry;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<TradingManager> _logger;
    private readonly IUserNotifier? _notifier;
    private readonly TradingActivityLog? _activity;
    private readonly MonitoredUniverse? _universe;
    private readonly SemaphoreSlim _sellExecutionGate = new(1, 1);

    /// <summary>How long an execution alert may take to reach the channels before it is abandoned.</summary>
    private static readonly TimeSpan NotifyTimeout = TimeSpan.FromSeconds(20);

    /// <param name="notifier">
    /// Optional channel broadcaster. Defaulted so the manager still activates in a host that does
    /// not register <see cref="IUserNotifier"/> (and in tests); alerts are simply skipped then.
    /// </param>
    /// <param name="universe">
    /// Supplies the manual-only deny set. Defaulted for the same reason as the others; a manager built
    /// without it enforces every other gate and simply has no hand-managed symbols.
    /// </param>
    public TradingManager(
        IBrokerAdapter broker,
        ITradingRepository repository,
        IMarketCalendar calendar,
        TradingAgent.Market.OrderWindow orderWindow,
        TradingPolicyProvider policyProvider,
        ITradingRiskEngine riskEngine,
        TradingReconciliationState reconciliation,
        ApprovalIntentRegistry intentRegistry,
        IOptions<TradingAgentOptions> options,
        ILogger<TradingManager> logger,
        TradingActivityLog? activity = null,
        IUserNotifier? notifier = null,
        IBrokerStateReader? brokerStateReader = null,
        MonitoredUniverse? universe = null)
    {
        _universe = universe;
        _activity = activity;
        _notifier = notifier;
        _broker = broker;
        _repository = repository;
        _calendar = calendar;
        _orderWindow = orderWindow;
        _policyProvider = policyProvider;
        _riskEngine = riskEngine;
        _reconciliation = reconciliation;
        _brokerStateReader = brokerStateReader;
        _intentRegistry = intentRegistry;
        _options = options;
        _logger = logger;
    }

    public Task<IReadOnlyDictionary<string, decimal?>> GetMarketPricesAsync(IReadOnlyList<string> symbols) =>
        _broker.GetMarketPricesAsync(symbols);

    /// <summary>
    /// A refusal, recorded on the way out. Every gate in <see cref="ExecuteGroupsAsync"/> returns
    /// through here so the activity panel can answer "why did nothing happen" — which, for a system
    /// whose safe behaviour is to decline, is the question asked most often.
    /// </summary>
    private TradingExecutionResult Reject(string policyVersion, string reason)
    {
        _activity?.Warn("Orders", "Execution refused", reason);
        return TradingExecutionResult.Rejected(policyVersion, reason);
    }

    public async Task<TradingExecutionResult> ExecuteGroupsAsync(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups,
        string? sourceMessage,
        ExecutionAuthorization? authorization = null,
        CancellationToken ct = default)
    {
        var policy = _policyProvider.Current();
        if (!policy.AutoExecute)
            return Reject(policy.Version, "AutoExecute is disabled.");

        var mode = policy.ExecutionMode.Trim().ToUpperInvariant();
        if (mode == "DISABLED")
            return Reject(policy.Version, "Trading execution mode is Disabled.");

        if (groups.Count == 0 || groups.All(g => g.Count == 0))
            return Reject(policy.Version, "No orders were supplied.");

        // Approval authenticates the exact maximum the caller requested. A later holdings adjustment
        // may only reduce a SELL, never enlarge or otherwise alter that approved instruction.
        if (mode == "APPROVALREQUIRED")
        {
            if (authorization?.Method != "host-tool-gate")
                return Reject(policy.Version,
                    "ApprovalRequired mode needs an authorization from the host tool-approval gate.");

            var intentFailure = ValidateApprovalIntent(
                authorization.Intent, groups, sourceMessage, policy.Version);
            if (intentFailure is not null)
            {
                _logger.LogWarning("[TradingManager] Approval intent rejected: {Reason}", intentFailure);
                return Reject(policy.Version, intentFailure);
            }
        }

        var sellGateHeld = mode is not ("PAPER" or "SHADOW")
                           && SellQuantityRule.HasIndependentSell(groups);
        if (sellGateHeld)
            await _sellExecutionGate.WaitAsync(ct);

        try
        {

        // A retail SELL is bounded by custody, not by what the caller typed. Do this at the single
        // execution boundary so dashboard, agent tool, armed order, protective stop, and retry paths
        // cannot disagree. Risk limits below are evaluated against the quantity that can actually be
        // submitted, while the immutable approval above remains bound to the requested maximum.
        IReadOnlyList<SellQuantityAdjustment> sellAdjustments = [];
        IReadOnlyCollection<string> liquidationUniverse = [];
        if (mode is not ("PAPER" or "SHADOW"))
        {
            var maxAge = TimeSpan.FromSeconds(
                Math.Max(10, _options.Value.ReconciliationMaxAgeSeconds));
            var brokerState = _reconciliation.Current;
            if (sellGateHeld && _brokerStateReader is not null)
            {
                try
                {
                    brokerState = await _brokerStateReader.ReadSnapshotAsync(ct);
                    _reconciliation.Update(brokerState);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return Reject(policy.Version,
                        "SELL availability check failed while refreshing the broker book: " + ex.Message);
                }
            }
            var sizing = SellQuantityRule.SizeIndependentSells(
                groups, brokerState, DateTime.UtcNow, maxAge);
            if (sizing.Problem is { } sellProblem)
                return Reject(policy.Version, "SELL availability check failed: " + sellProblem);

            groups = sizing.Groups;
            sellAdjustments = sizing.Adjustments;
            if (brokerState.Healthy)
            {
                liquidationUniverse = brokerState.Positions
                    .Where(position => position.Quantity > 0)
                    .Select(position => position.Symbol)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }

        IReadOnlyList<string>? executionUniverse = null;
        if (_universe is not null)
            executionUniverse = await _universe.ForExecutionAsync(ct);

        var risk = _riskEngine.Validate(
            groups, policy.KillSwitch, executionUniverse, liquidationUniverse);
        if (!risk.Allowed)
            return Reject(policy.Version,
                "Pre-trade risk validation failed: " + string.Join(" ", risk.Violations));

        // ── Manual-only symbols ───────────────────────────────────────────────
        // The one gate that asks WHO is trading rather than WHAT. Everything above answers "may this
        // order exist", and the answer is the same for a person and a robot; a symbol the operator
        // hand-manages needs those answers to differ, so it cannot be expressed in the risk engine
        // (dropping it from AllowedSymbols would ban the operator too) and is enforced here instead.
        //
        // Placed at the single execution boundary on purpose. ApprovalGate refuses earlier and with a
        // better message, but only paths that ASK it are covered — and a strategy, retry worker, or a
        // future caller that submits without asking would otherwise sail past. Attendance is the test:
        // an authorization is required, and it must be one a human gave for THIS order. Absent
        // authorization (every unattended worker) and pre-authorized policy both fail it, which is the
        // direction a new caller should fail in.
        if (_universe is not null)
        {
            var symbols = groups.SelectMany(g => g).Select(o => o.Symbol);
            if (await _universe.FirstManualOnlyAsync(symbols, ct) is { } manualSymbol)
            {
                if (authorization is not { Attended: true })
                {
                    _logger.LogWarning(
                        "[TradingManager] Refused an unattended order for {Symbol}: the symbol is "
                        + "manual-only. Authorization was {Method}.",
                        manualSymbol, authorization?.Method ?? "none");
                    return Reject(policy.Version,
                        $"{manualSymbol} is manual-only: automation may not place orders for it, "
                        + "entries or exits. Place this one yourself, or turn automation back on for "
                        + "the symbol on the watchlist (or remove it from ManualOnlySymbols).");
                }

                // Attended and allowed — but recorded, because "who traded a hand-managed name" is
                // exactly the question this flag makes worth asking later.
                _activity?.Info("Orders", $"Manual-only {manualSymbol} traded by hand",
                    $"Authorized by {authorization.Actor} via {authorization.Method}.");
            }
        }

        if (mode is "APPROVALREQUIRED" or "BOUNDEDAUTO"
            && _options.Value.RequireReconciliationHealthy)
        {
            var reconciliation = _reconciliation.Current;
            var maxAge = TimeSpan.FromSeconds(Math.Max(10, _options.Value.ReconciliationMaxAgeSeconds));
            if (!reconciliation.Supported || !reconciliation.Healthy
                || DateTime.UtcNow - reconciliation.CheckedUtc > maxAge)
                return Reject(policy.Version,
                    "Broker reconciliation is not healthy: " + reconciliation.Reason);
        }

        // Whether the VENUE is accepting orders — not merely whether the regular matching session is
        // running. PSX's pre-open OHO state accepts orders that go live at the open, and gating on
        // the calendar alone silently forfeited that window. See OrderWindow for the full reasoning.
        var window = _orderWindow.Evaluate();
        if (!window.Allowed)
        {
            _logger.LogInformation(
                "[TradingManager] Order rejected by the {Source} order window: {Reason}",
                window.Source, window.Reason);
            return Reject(policy.Version, window.Reason);
        }

        var requestJson = JsonSerializer.Serialize(groups, Json);
        var idempotencyKey = BuildIdempotencyKey(sourceMessage, requestJson, policy.Version);
        var claim = await _repository.TryBeginExecutionAsync(
            idempotencyKey, requestJson, policy.Version, ct);

        if (!claim.Acquired)
        {
            if (!string.IsNullOrWhiteSpace(claim.ResultJson))
            {
                var replay = JsonSerializer.Deserialize<TradingExecutionResult>(claim.ResultJson, Json);
                if (replay is not null) return replay with { IsReplay = true };
            }
            return new(false, true, claim.ExecutionId, policy.Version,
                $"An execution with this idempotency key already exists in state '{claim.State}'.",
                Array.Empty<IReadOnlyList<OrderResult>>());
        }

        await _repository.AppendEventAsync(claim.ExecutionId, "validated",
            JsonSerializer.Serialize(new
            {
                policy.Version,
                PktNow = TradingAgent.Market.PsxTime.Now(),
                // Which authority allowed this order through ("broker" or "calendar") and what it
                // said. The gate can now pass on a state the calendar alone would have refused —
                // pre-open OHO — so an audit record that omitted this could not answer "why was this
                // order accepted at 09:05" after the fact.
                OrderWindowSource = window.Source,
                OrderWindowReason = window.Reason,
                SellQuantityAdjustments = sellAdjustments,
                authorization
            }, Json), ct);

        if (mode is "PAPER" or "SHADOW")
        {
            var simulated = groups.Select(group => (IReadOnlyList<OrderResult>)group.Select(order => new OrderResult
            {
                Success = true,
                OrderId = $"{mode.ToLowerInvariant()}-{Guid.NewGuid():N}",
                Action = order.Action,
                Symbol = order.Symbol,
                Quantity = order.Quantity,
                Message = $"{mode} mode: broker submission suppressed.",
                RequestedPrice = order.EntryPrice,
                SubmittedPrice = order.EntryPrice
            }).ToList()).ToList();
            AnnotateSellAdjustments(simulated, sellAdjustments);
            var result = new TradingExecutionResult(true, false, claim.ExecutionId,
                policy.Version, WithSellAdjustment(
                    $"{mode} execution recorded without broker submission.", sellAdjustments), simulated);
            await PersistResultAsync(result, "simulated", ct);
            return result;
        }

        try
        {
            await _repository.AppendEventAsync(claim.ExecutionId, "broker_submission_started", "{}", ct);
            var brokerResults = await _broker.PlaceOrderGroupsAsync(groups);
            AnnotateSellAdjustments(brokerResults, sellAdjustments);
            var success = brokerResults.SelectMany(x => x).All(x => x.Success);
            var result = new TradingExecutionResult(true, false, claim.ExecutionId,
                policy.Version,
                WithSellAdjustment(
                    success ? "Broker accepted all attempted orders." : "One or more broker orders failed.",
                    sellAdjustments),
                brokerResults);

            // ── Recording the answer is NOT part of getting it ──────────────────────────────────
            // The broker has replied and `result` holds what it said. A failure to write that down is
            // a bookkeeping problem, and reporting it as "broker outcome unknown" would be a lie in
            // the most expensive direction: it discards an answer we have, tells the operator to go
            // reconcile something that is not actually uncertain, and — because the write never
            // landed — makes it genuinely unknown afterwards. CONFIRMED live 2026-08-28, where a
            // UNIQUE-constraint violation on a REUSED broker order number did exactly that.
            try
            {
                await PersistResultAsync(result, success ? "accepted" : "failed", ct);
            }
            catch (Exception persistEx)
            {
                _logger.LogError(persistEx,
                    "[TradingManager] The broker ANSWERED for {ExecutionId} but the ledger write failed. "
                    + "The outcome below is accurate; the audit trail for it is not.", claim.ExecutionId);

                // Best effort, and deliberately a different event type: a reader must be able to find
                // executions whose orders are real but whose rows are missing.
                try
                {
                    await _repository.AppendEventAsync(claim.ExecutionId, "ledger_write_failed",
                        JsonSerializer.Serialize(new { error = persistEx.Message, result }), ct);
                }
                catch (Exception ex2)
                {
                    _logger.LogError(ex2,
                        "[TradingManager] {ExecutionId}: the ledger could not even record its own "
                        + "write failure.", claim.ExecutionId);
                }

                return result with
                {
                    Reason = result.Reason
                        + $" NOTE: the broker's answer could not be written to the ledger "
                        + $"({persistEx.Message}). The orders above are what actually happened — the "
                        + "AUDIT TRAIL needs reconciling, not the position."
                };
            }

            return result;
        }
        catch (Exception ex)
        {
            // Reached only when the BROKER call itself failed in a way that leaves the outcome genuinely
            // undetermined. Unknown is deliberately terminal for automatic retry; reconciliation must
            // resolve it.
            _logger.LogError(ex, "[TradingManager] Broker outcome unknown for {ExecutionId}.", claim.ExecutionId);
            var result = new TradingExecutionResult(false, false, claim.ExecutionId,
                policy.Version, $"Broker outcome unknown; manual reconciliation required: {ex.Message}",
                Array.Empty<IReadOnlyList<OrderResult>>());
            try { await PersistResultAsync(result, "unknown", ct); }
            catch (Exception persistEx)
            {
                _logger.LogError(persistEx,
                    "[TradingManager] {ExecutionId}: an unknown broker outcome could not be persisted "
                    + "either. This execution has no durable record at all.", claim.ExecutionId);
            }
            return result;
        }
        }
        finally
        {
            if (sellGateHeld) _sellExecutionGate.Release();
        }
    }

    private static void AnnotateSellAdjustments(
        IReadOnlyList<IReadOnlyList<OrderResult>> results,
        IReadOnlyList<SellQuantityAdjustment> adjustments)
    {
        foreach (var adjustment in adjustments)
        {
            if (adjustment.GroupIndex >= results.Count
                || adjustment.OrderIndex >= results[adjustment.GroupIndex].Count)
                continue;

            var result = results[adjustment.GroupIndex][adjustment.OrderIndex];
            result.RequestedQuantity = adjustment.RequestedQuantity;
            result.Quantity = adjustment.SubmittedQuantity;
            result.QuantityAdjustment = adjustment.Message;
            result.Message = string.IsNullOrWhiteSpace(result.Message)
                ? adjustment.Message
                : result.Message.TrimEnd() + " " + adjustment.Message;
        }
    }

    private static string WithSellAdjustment(
        string reason, IReadOnlyList<SellQuantityAdjustment> adjustments) =>
        adjustments.Count == 0
            ? reason
            : reason + " " + string.Join(" ", adjustments.Select(a => a.Message));

    private async Task PersistResultAsync(
        TradingExecutionResult result,
        string state,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(result, Json);
        await _repository.CompleteExecutionAsync(result.ExecutionId, state, json, ct);
        await _repository.AppendEventAsync(result.ExecutionId, state, json, ct);

        // One row per order attempt, keyed by the exchange's order number. The blob above is the full
        // story; this is the part that has to be findable by order number rather than by grep.
        var placed = result.Groups.SelectMany(g => g).ToList();
        if (placed.Count > 0)
            await _repository.RecordBrokerOrdersAsync(result.ExecutionId, placed, ct);

        // Deliberately after the ledger writes: the durable record is the source of truth, and a
        // channel outage must never cost us an execution record. Every path into the broker funnels
        // through here, so this one call covers manual orders, armed orders, take-profit retries
        // and protective stops alike.
        await NotifyExecutionAsync(result, state);
    }

    /// <summary>
    /// Broadcasts the execution outcome to the user's messaging channels. Failure to deliver is
    /// logged and swallowed — the execution has already happened and been recorded, so throwing
    /// here would misreport a completed trade as a failed one.
    /// </summary>
    private async Task NotifyExecutionAsync(TradingExecutionResult result, string state)
    {
        if (_notifier is null) return;

        var options = _options.Value;
        if (!options.NotifyOnExecution) return;
        if (state == "simulated" && !options.NotifyOnSimulatedExecution) return;

        try
        {
            // Not cancelled with the caller's token: the orders are already at the broker, so the
            // user needs to hear about them even if the originating request was abandoned.
            var topic = TradingTopics.Order(state);

            var sent = await _notifier
                .NotifyAsync(BuildExecutionMessage(result, state), topic)
                .WaitAsync(NotifyTimeout);

            if (sent == 0)
                _logger.LogWarning(
                    "[TradingManager] Execution {ExecutionId} ({State}) reached no channels on '{Topic}'.",
                    result.ExecutionId, state, topic);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[TradingManager] Failed to deliver the execution alert for {ExecutionId} ({State}).",
                result.ExecutionId, state);
        }
    }

    private static string BuildExecutionMessage(TradingExecutionResult result, string state)
    {
        var heading = state switch
        {
            "accepted"  => "✅ Orders placed",
            "failed"    => "⚠️ Order execution partially failed",
            "unknown"   => "🚨 Broker outcome UNKNOWN — manual reconciliation required",
            "simulated" => "🧪 Simulated execution (no broker submission)",
            _           => $"Order execution ({state})"
        };

        var sb = new StringBuilder();
        sb.Append("**").Append(heading).Append("**\n");

        var orders = result.Groups.SelectMany(g => g).ToList();
        foreach (var order in orders)
        {
            var price = order.SubmittedPrice ?? order.RequestedPrice;
            sb.Append(order.Success ? "• " : "• ❌ ")
              .Append(order.Action.ToUpperInvariant())
              .Append(' ')
              .Append(order.Symbol);

            // Size before price. "BUY FFC @ 551" reads the same whether it bought 45 shares or
            // 4,500, and the difference is the entire trade.
            if (order.Quantity is { } quantity)
            {
                sb.Append(' ').Append(quantity.ToString("N0"));
                if (order.RequestedQuantity is { } requested && requested != quantity)
                    sb.Append(" (requested ").Append(requested.ToString("N0")).Append(')');
            }
            if (price.HasValue) sb.Append(" @ ").Append(price.Value.ToString("0.##"));

            if (order.Quantity is { } qty && price is { } unit && qty > 0 && unit > 0)
                sb.Append(" = ").Append((qty * unit).ToString("N0")).Append(" PKR");

            if (!string.IsNullOrWhiteSpace(order.OrderId)) sb.Append(" (#").Append(order.OrderId).Append(')');
            if (order.PriceAdjustment is { Length: > 0 } adj) sb.Append(" — ").Append(adj);
            if (!order.Success && !string.IsNullOrWhiteSpace(order.Message)) sb.Append(" — ").Append(order.Message);

            sb.Append('\n');
        }

        // An "unknown" outcome carries no orders at all, so the reason is the only useful content.
        if (orders.Count == 0 && !string.IsNullOrWhiteSpace(result.Reason))
            sb.Append(result.Reason).Append('\n');

        sb.Append("_Execution ").Append(result.ExecutionId).Append('_');
        return sb.ToString();
    }

    /// <summary>
    /// Revalidates the immutable approval intent immediately before submission. The intent must
    /// exist, be unexpired, match the current policy version, hash-match the exact groups being
    /// submitted, and never have been consumed before. Returns a rejection reason, or null when valid.
    /// </summary>
    private string? ValidateApprovalIntent(
        ApprovalIntent? intent,
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups,
        string? sourceMessage,
        string policyVersion)
    {
        if (intent is null)
            return "ApprovalRequired mode needs an immutable approval intent bound to the validated orders.";

        if (DateTime.UtcNow > intent.ExpiresUtc)
            return $"Approval intent {intent.IntentId} expired at {intent.ExpiresUtc:O}; re-approval is required.";

        if (!string.Equals(intent.PolicyVersion, policyVersion, StringComparison.Ordinal))
            return $"Approval intent {intent.IntentId} was approved under policy '{intent.PolicyVersion}' " +
                   $"but the current policy is '{policyVersion}'; re-approval is required.";

        var currentHash = ApprovalIntent.ComputeHash(groups, sourceMessage, policyVersion);
        if (!string.Equals(intent.IntegrityHash, currentHash, StringComparison.Ordinal))
            return $"Approval intent {intent.IntentId} integrity hash does not match the submitted orders; " +
                   "the request was modified after approval.";

        if (!_intentRegistry.TryConsume(intent.IntentId, out _))
            return $"Approval intent {intent.IntentId} was already consumed or never registered; replay rejected.";

        return null;
    }

    private static string BuildIdempotencyKey(string? sourceMessage, string requestJson, string policyVersion)
    {
        var source = string.IsNullOrWhiteSpace(sourceMessage)
            ? $"manual:{Guid.NewGuid():N}"
            : sourceMessage.Trim().ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{source}|{requestJson}|{policyVersion}"));
        return Convert.ToHexString(bytes);
    }
}
