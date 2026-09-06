using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;

namespace TradingAgent.Persistence;

/// <summary>
/// Applies ledger retention independently of the market monitor. Cleanup used to live only in the
/// post-close monitor branch, which meant a disabled monitor or a process that was not running at
/// that moment could retain dismissed alerts forever.
/// </summary>
public sealed class TradingRetentionWorker : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly ITradingRepository _repository;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<TradingRetentionWorker> _logger;

    public TradingRetentionWorker(
        ITradingRepository repository,
        IOptions<TradingAgentOptions> options,
        ILogger<TradingRetentionWorker> logger)
    {
        _repository = repository;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await PruneAsync(stoppingToken);
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task PruneAsync(CancellationToken ct)
    {
        try
        {
            var alertDays = _options.Value.Monitor.RetentionDays;
            if (alertDays > 0)
            {
                var removed = await _repository.PruneAlertsAsync(DateTime.UtcNow.AddDays(-alertDays), ct);
                if (removed > 0)
                    _logger.LogInformation(
                        "[Retention] Pruned {Count} alert(s) older than {Days} days; SQLite can reuse the freed pages.",
                        removed, alertDays);
            }

            var proposalDays = _options.Value.Proposals.RetentionDays;
            if (proposalDays > 0)
            {
                var removed = await _repository.PruneProposalsAsync(DateTime.UtcNow.AddDays(-proposalDays), ct);
                if (removed > 0)
                    _logger.LogInformation(
                        "[Retention] Pruned {Count} resolved proposal(s) older than {Days} days.",
                        removed, proposalDays);
            }

            var ledgerDays = _options.Value.LedgerRetentionDays;
            if (ledgerDays > 0)
            {
                var result = await _repository.PruneExecutionsAsync(
                    DateTime.UtcNow.AddDays(-ledgerDays), ct);

                if (result.Removed > 0)
                    _logger.LogInformation(
                        "[Retention] Pruned {Count} completed execution(s) older than {Cutoff:u}, "
                        + "with their order events, broker orders and fills. Unresolved executions "
                        + "were kept regardless of age.",
                        result.Removed, result.EffectiveCutoff);

                // Reported even when nothing was removed. Otherwise an operator who set 14 days and
                // sees no rows go has no way to tell "nothing was old enough" from "an open campaign
                // is holding the cutoff months back" — and the second is a fact about their
                // positions rather than a fault, so it needs saying rather than inferring.
                if (result.HeldBackByOpenCampaign)
                    _logger.LogInformation(
                        "[Retention] The {Days}-day ledger cutoff was held back to {Cutoff:u} by an "
                        + "open automation campaign — its fills are still needed to compute realised "
                        + "P&L when it closes. Cleanup resumes once the campaign is closed.",
                        ledgerDays, result.EffectiveCutoff);
            }

            var reconciliationDays = _options.Value.ReconciliationRetentionDays;
            if (reconciliationDays > 0)
            {
                var removed = await _repository.PruneReconciliationRunsAsync(
                    DateTime.UtcNow.AddDays(-reconciliationDays), ct);
                if (removed > 0)
                    _logger.LogInformation(
                        "[Retention] Pruned {Count} reconciliation snapshot(s) older than {Days} days.",
                        removed, reconciliationDays);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[Retention] Daily ledger cleanup failed; retrying tomorrow.");
        }
    }
}
