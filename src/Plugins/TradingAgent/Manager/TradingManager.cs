using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingAgent.Broker;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Models;
using TradingAgent.Persistence;
using TradingAgent.Risk;
using TradingAgent.Reconciliation;
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
    private readonly TradingPolicyProvider _policyProvider;
    private readonly ITradingRiskEngine _riskEngine;
    private readonly TradingReconciliationState _reconciliation;
    private readonly ApprovalIntentRegistry _intentRegistry;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<TradingManager> _logger;

    public TradingManager(
        IBrokerAdapter broker,
        ITradingRepository repository,
        IMarketCalendar calendar,
        TradingPolicyProvider policyProvider,
        ITradingRiskEngine riskEngine,
        TradingReconciliationState reconciliation,
        ApprovalIntentRegistry intentRegistry,
        IOptions<TradingAgentOptions> options,
        ILogger<TradingManager> logger)
    {
        _broker = broker;
        _repository = repository;
        _calendar = calendar;
        _policyProvider = policyProvider;
        _riskEngine = riskEngine;
        _reconciliation = reconciliation;
        _intentRegistry = intentRegistry;
        _options = options;
        _logger = logger;
    }

    public Task<IReadOnlyDictionary<string, decimal?>> GetMarketPricesAsync(IReadOnlyList<string> symbols) =>
        _broker.GetMarketPricesAsync(symbols);

    public async Task<TradingExecutionResult> ExecuteGroupsAsync(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups,
        string? sourceMessage,
        ExecutionAuthorization? authorization = null,
        CancellationToken ct = default)
    {
        var policy = _policyProvider.Current();
        if (!policy.AutoExecute)
            return TradingExecutionResult.Rejected(policy.Version, "AutoExecute is disabled.");

        var mode = policy.ExecutionMode.Trim().ToUpperInvariant();
        if (mode == "DISABLED")
            return TradingExecutionResult.Rejected(policy.Version, "Trading execution mode is Disabled.");

        if (groups.Count == 0 || groups.All(g => g.Count == 0))
            return TradingExecutionResult.Rejected(policy.Version, "No orders were supplied.");

        var risk = _riskEngine.Validate(groups, policy.KillSwitch);
        if (!risk.Allowed)
            return TradingExecutionResult.Rejected(policy.Version,
                "Pre-trade risk validation failed: " + string.Join(" ", risk.Violations));

        if (mode == "APPROVALREQUIRED")
        {
            if (authorization?.Method != "host-tool-gate")
                return TradingExecutionResult.Rejected(policy.Version,
                    "ApprovalRequired mode needs an authorization from the host tool-approval gate.");

            var intentFailure = ValidateApprovalIntent(
                authorization.Intent, groups, sourceMessage, policy.Version);
            if (intentFailure is not null)
            {
                _logger.LogWarning("[TradingManager] Approval intent rejected: {Reason}", intentFailure);
                return TradingExecutionResult.Rejected(policy.Version, intentFailure);
            }
        }

        if (mode is "APPROVALREQUIRED" or "BOUNDEDAUTO"
            && _options.Value.RequireReconciliationHealthy)
        {
            var reconciliation = _reconciliation.Current;
            var maxAge = TimeSpan.FromSeconds(Math.Max(10, _options.Value.ReconciliationMaxAgeSeconds));
            if (!reconciliation.Supported || !reconciliation.Healthy
                || DateTime.UtcNow - reconciliation.CheckedUtc > maxAge)
                return TradingExecutionResult.Rejected(policy.Version,
                    "Broker reconciliation is not healthy: " + reconciliation.Reason);
        }

        var market = _calendar.GetStatus();
        if (!market.IsOpen)
            return TradingExecutionResult.Rejected(policy.Version, market.Reason);

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
                market.PktNow,
                market.ScheduleSource,
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
                Message = $"{mode} mode: broker submission suppressed.",
                RequestedPrice = order.EntryPrice,
                SubmittedPrice = order.EntryPrice
            }).ToList()).ToList();
            var result = new TradingExecutionResult(true, false, claim.ExecutionId,
                policy.Version, $"{mode} execution recorded without broker submission.", simulated);
            await PersistResultAsync(result, "simulated", ct);
            return result;
        }

        try
        {
            await _repository.AppendEventAsync(claim.ExecutionId, "broker_submission_started", "{}", ct);
            var brokerResults = await _broker.PlaceOrderGroupsAsync(groups);
            var success = brokerResults.SelectMany(x => x).All(x => x.Success);
            var result = new TradingExecutionResult(true, false, claim.ExecutionId,
                policy.Version,
                success ? "Broker accepted all attempted orders." : "One or more broker orders failed.",
                brokerResults);
            await PersistResultAsync(result, success ? "accepted" : "failed", ct);
            return result;
        }
        catch (Exception ex)
        {
            // Unknown is deliberately terminal for automatic retry. Reconciliation must resolve it.
            _logger.LogError(ex, "[TradingManager] Broker outcome unknown for {ExecutionId}.", claim.ExecutionId);
            var result = new TradingExecutionResult(false, false, claim.ExecutionId,
                policy.Version, $"Broker outcome unknown; manual reconciliation required: {ex.Message}",
                Array.Empty<IReadOnlyList<OrderResult>>());
            await PersistResultAsync(result, "unknown", ct);
            return result;
        }
    }

    private async Task PersistResultAsync(
        TradingExecutionResult result,
        string state,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(result, Json);
        await _repository.CompleteExecutionAsync(result.ExecutionId, state, json, ct);
        await _repository.AppendEventAsync(result.ExecutionId, state, json, ct);
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
