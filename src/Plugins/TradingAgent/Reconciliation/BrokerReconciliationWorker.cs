using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Persistence;

namespace TradingAgent.Reconciliation;

/// <summary>
/// Maintains the reconciliation health gate. Unsupported broker read APIs remain unhealthy and
/// therefore block live execution when RequireReconciliationHealthy is enabled.
/// </summary>
public sealed class BrokerReconciliationWorker : BackgroundService
{
    private readonly IBrokerStateReader _reader;
    private readonly TradingReconciliationState _state;
    private readonly ITradingRepository _repository;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<BrokerReconciliationWorker> _logger;

    public BrokerReconciliationWorker(
        IBrokerStateReader reader,
        TradingReconciliationState state,
        ITradingRepository repository,
        IOptions<TradingAgentOptions> options,
        ILogger<BrokerReconciliationWorker> logger)
    {
        _reader = reader;
        _state = state;
        _repository = repository;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(10, _options.Value.ReconciliationIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            BrokerReconciliationSnapshot snapshot;
            try
            {
                snapshot = await _reader.ReadSnapshotAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                snapshot = new(false, false, ex.Message, DateTime.UtcNow);
                _logger.LogError(ex, "[Reconciliation] Broker state read failed.");
            }

            _state.Update(snapshot);
            await _repository.RecordReconciliationAsync(snapshot, stoppingToken);

            // Fills become rows. Idempotent by design: this log is re-read every pass, so the same fill
            // arrives again on every one of them for the rest of the trading day.
            if (snapshot.Fills.Count > 0)
            {
                var stored = await _repository.RecordFillsAsync(snapshot.Fills, stoppingToken);
                if (stored > 0)
                    _logger.LogInformation(
                        "[BrokerReconciliation] Recorded {Stored} new fill(s) of {Seen} reported.",
                        stored, snapshot.Fills.Count);
            }
            if (!snapshot.Healthy)
                _logger.LogWarning("[Reconciliation] Unhealthy: {Reason}", snapshot.Reason);

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
