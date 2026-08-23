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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[Retention] Daily ledger cleanup failed; retrying tomorrow.");
        }
    }
}
