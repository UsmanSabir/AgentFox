using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Broker;
using TradingAgent.Config;
using TradingAgent.Manager;
using TradingAgent.Market;
using TradingAgent.Models;

namespace TradingAgent.Trading;

/// <summary>
/// Background worker that retries take-profit SELLs which couldn't be placed immediately (the paired BUY
/// limit hadn't filled yet, so the account had no shares to sell). It wakes on an interval, and while the
/// market is open re-attempts each due sell via the broker. A sell is removed as soon as the broker
/// ACCEPTS it (the limit then rests at the broker and fills on its own) or once it has burned through the
/// configured attempt budget. Disk-backed via <see cref="PendingTakeProfitStore"/>, so a restart resumes.
/// </summary>
public sealed class TakeProfitRetryWorker : BackgroundService
{
    private readonly PendingTakeProfitStore _store;
    private readonly TradingAgent.Manager.TradingManager _manager;
    private readonly IMarketCalendar _calendar;
    private readonly TradingPolicyProvider _policyProvider;
    private readonly IOptions<TradingAgentOptions> _opts;
    private readonly ILogger<TakeProfitRetryWorker> _logger;

    public TakeProfitRetryWorker(
        PendingTakeProfitStore store,
        TradingAgent.Manager.TradingManager manager,
        IMarketCalendar calendar,
        TradingPolicyProvider policyProvider,
        IOptions<TradingAgentOptions> opts,
        ILogger<TakeProfitRetryWorker> logger)
    {
        _store  = store;
        _manager = manager;
        _calendar = calendar;
        _policyProvider = policyProvider;
        _opts   = opts;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _opts.Value;
        if (!opts.RetryFailedTakeProfit)
        {
            _logger.LogInformation("[TakeProfit] Retry worker disabled (RetryFailedTakeProfit=false).");
            return;
        }

        var interval    = TimeSpan.FromMinutes(Math.Max(1, opts.TakeProfitRetryIntervalMinutes));
        var maxAttempts = Math.Max(1, opts.TakeProfitRetryMaxAttempts);

        _logger.LogInformation(
            "[TakeProfit] Retry worker started. Interval={Interval}min MaxAttempts={Max}.",
            opts.TakeProfitRetryIntervalMinutes, maxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                if (_store.Count == 0) continue;

                // Only spin up the browser when the market is open — a sell can't fill (or even be
                // placed) otherwise, and we don't want attempts to expire against a closed market.
                if (!_calendar.GetStatus().IsOpen) continue;

                // Background order submission is autonomous by definition. ApprovalRequired mode
                // must not be bypassed by a hosted worker.
                if (!_policyProvider.Current().ExecutionMode.Equals(
                        "BoundedAuto", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "[TakeProfit] Pending exits exist, but background submission requires ExecutionMode=BoundedAuto.");
                    continue;
                }

                foreach (var pending in _store.GetDue(DateTime.UtcNow))
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await TryPlaceAsync(pending, interval, maxAttempts);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TakeProfit] Retry cycle failed.");
            }
        }

        _logger.LogInformation("[TakeProfit] Retry worker stopped.");
    }

    private async Task TryPlaceAsync(PendingTakeProfit pending, TimeSpan interval, int maxAttempts)
    {
        var signal = new TradingSignal
        {
            Action     = "SELL",
            Symbol     = pending.Symbol,
            Quantity   = pending.Quantity,
            EntryPrice = pending.TargetPrice,   // the broker re-clamps into the day's band
            OrderType  = "LIMIT",
            Confidence = "HIGH"
        };

        OrderResult result;
        try
        {
            var execution = await _manager.ExecuteGroupsAsync(
                new[] { (IReadOnlyList<TradingSignal>)new[] { signal } },
                $"take-profit:{pending.Id}:attempt:{pending.Attempts + 1}");
            result = execution.Groups.FirstOrDefault()?.FirstOrDefault()
                ?? new OrderResult { Success = false, Message = execution.Reason };
        }
        catch (Exception ex)
        {
            result = new OrderResult { Success = false, Message = ex.Message };
        }

        if (result.Success)
        {
            _store.Remove(pending.Id);
            _logger.LogInformation(
                "[TakeProfit] Retry SUCCEEDED on attempt {Attempt}: SELL {Symbol} x{Qty} @ {Price}. {Adj}",
                pending.Attempts + 1, pending.Symbol, pending.Quantity,
                result.SubmittedPrice ?? pending.TargetPrice, result.PriceAdjustment);
            return;
        }

        // Still failing. If the reason has become permanent (not the transient settlement/exposure case),
        // stop retrying — repeatedly hammering a hard reject is pointless.
        var attemptsSoFar = pending.Attempts + 1;
        if (attemptsSoFar >= maxAttempts || !PendingTakeProfitStore.IsRetryable(result.Message))
        {
            _store.Remove(pending.Id);
            _logger.LogWarning(
                "[TakeProfit] GIVING UP on SELL {Symbol} x{Qty} @ {Price} after {Attempts} attempt(s): {Reason}",
                pending.Symbol, pending.Quantity, pending.TargetPrice, attemptsSoFar, result.Message);
            return;
        }

        _store.RecordFailure(pending.Id, result.Message, (int)interval.TotalMinutes);
        _logger.LogInformation(
            "[TakeProfit] Retry {Attempt}/{Max} failed for SELL {Symbol} @ {Price} — will retry. Reason: {Reason}",
            attemptsSoFar, maxAttempts, pending.Symbol, pending.TargetPrice, result.Message);
    }
}
