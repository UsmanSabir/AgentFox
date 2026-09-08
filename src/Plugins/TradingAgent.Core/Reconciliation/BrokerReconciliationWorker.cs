using System.Diagnostics;
using AgentFox.Plugins.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Persistence;

namespace TradingAgent.Reconciliation;

/// <summary>
/// Maintains the reconciliation health gate. Unsupported broker read APIs remain unhealthy and
/// therefore block live execution when RequireReconciliationHealthy is enabled.
/// </summary>
public sealed class BrokerReconciliationWorker : BackgroundService, IMarketSessionOpenParticipant
{
    public string Name => "broker reconciliation";
    public int Order => 100;

    private readonly IBrokerStateReader _reader;
    private readonly TradingReconciliationState _state;
    private readonly ITradingRepository _repository;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<BrokerReconciliationWorker> _logger;
    private readonly SemaphoreSlim _runGate = new(1, 1);

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
            try { await RunNowAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Runs one single-flight pass. The dashboard uses this after a user-initiated broker-session
    /// handshake, so an already open browser session can be harvested immediately instead of waiting
    /// for both the feed and the next timer tick.
    /// </summary>
    public async Task<BrokerReconciliationSnapshot> RunNowAsync(CancellationToken ct = default)
    {
        // Begin(), not Ensure(): a reconciliation pass is its own unit of work even when a caller
        // upstream had a correlation — an on-demand run triggered from a turn should not have its
        // rows filed under that turn, because the pass reads the WHOLE account, not that turn's
        // orders. The one place in this change where a fresh id is the honest answer.
        using var correlation = CorrelationContext.Begin();

        using var span = AgentTelemetry.Start(
            AgentTelemetry.Trading, "trading.reconcile", ActivityKind.Client);

        await _runGate.WaitAsync(ct);
        try
        {
            BrokerReconciliationSnapshot snapshot;
            try
            {
                snapshot = await _reader.ReadSnapshotAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                snapshot = new(false, false, ex.Message, DateTime.UtcNow);
                _logger.LogError(ex, "[Reconciliation] Broker state read failed.");
            }

            _state.Update(snapshot);
            await _repository.RecordReconciliationAsync(snapshot, ct);

            if (snapshot.Fills.Count > 0)
            {
                var stored = await _repository.RecordFillsAsync(snapshot.Fills, ct);
                if (stored > 0)
                    _logger.LogInformation(
                        "[BrokerReconciliation] Recorded {Stored} new fill(s) of {Seen} reported.",
                        stored, snapshot.Fills.Count);
            }
            if (!snapshot.Healthy)
                _logger.LogWarning("[Reconciliation] Unhealthy: {Reason}", snapshot.Reason);

            span?.SetTag("agentfox.reconciliation.healthy", snapshot.Healthy);
            span?.SetTag("agentfox.reconciliation.fill_count", snapshot.Fills.Count);
            // An unhealthy snapshot is a refusal, not an exception: the read failed and was
            // recorded as such rather than throwing. That distinction matters because an unhealthy
            // reconciliation is what pauses order submission — it needs to be findable in a trace,
            // and it never surfaces as an error anywhere else.
            AgentTelemetry.SetOutcome(span, snapshot.Healthy, snapshot.Healthy ? null : snapshot.Reason);
            return snapshot;
        }
        finally
        {
            _runGate.Release();
        }
    }

    public async Task RunAtMarketOpenAsync(MarketSessionOpenContext context, CancellationToken ct) =>
        await RunNowAsync(ct);

    /// <summary>
    /// How long a refresh request waits for a burst to settle before reading the account.
    ///
    /// <para>
    /// One order's life pushes several events within seconds — accepted, then one event per fill — and
    /// every one of them describes the SAME account, so reading once per event would pay five SOAP calls
    /// for each restatement of the same fact. A login replaying a backlog of stored messages is the
    /// extreme case of that.
    /// </para>
    ///
    /// <para>
    /// Two seconds is chosen against the thing this exists to fix: an operator who cancels an order
    /// elsewhere and immediately tries to sell. Their round trip is tens of seconds, so two is invisible
    /// to them while still collapsing a burst. It is deliberately NOT configurable — a delay nobody will
    /// tune is not configuration, it is a constant with a settings page (§0.2).
    /// </para>
    /// </summary>
    private static readonly TimeSpan RefreshBurstWindow = TimeSpan.FromSeconds(2);

    private CoalescingRefresh? _eventRefresh;

    public void RefreshSoon(string reason)
    {
        // Built on first use rather than in the constructor: the delegate closes over RunNowAsync, and a
        // worker that is never asked for an event-driven refresh should not carry the machinery.
        _eventRefresh ??= new CoalescingRefresh(
            why =>
            {
                _logger.LogInformation(
                    "[Reconciliation] Reading the account now rather than at the next tick: {Why}", why);
                return RunNowAsync();
            },
            RefreshBurstWindow,
            _logger,
            "Reconciliation");

        _eventRefresh.Request(reason);
    }
}
