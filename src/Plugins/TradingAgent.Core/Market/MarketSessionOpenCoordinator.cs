using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingAgent.Observability;

namespace TradingAgent.Market;

/// <summary>
/// A subsystem that must run once when a regular-market session opens.
/// Lower orders run first, so fresh broker truth and existing risk are handled before new signals.
/// </summary>
public interface IMarketSessionOpenParticipant
{
    string Name { get; }
    int Order { get; }
    Task RunAtMarketOpenAsync(MarketSessionOpenContext context, CancellationToken ct);
}

public sealed record MarketSessionOpenContext(
    DateTime ScheduledOpenPkt,
    DateTime StartedPkt,
    string SessionKey)
{
    public double StartLagMilliseconds =>
        Math.Max(0, (StartedPkt - ScheduledOpenPkt).TotalMilliseconds);
}

/// <summary>
/// Wakes on the calendar's opening edge and runs registered participants in safety order.
/// Periodic worker loops remain in place as retries; this removes their cadence from the first pass.
/// </summary>
public sealed class MarketSessionOpenCoordinator : BackgroundService
{
    private static readonly TimeSpan OpenRecheck = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MissingScheduleRecheck = TimeSpan.FromMinutes(1);

    private readonly IMarketCalendar _calendar;
    private readonly IReadOnlyList<IMarketSessionOpenParticipant> _participants;
    private readonly TimeProvider _time;
    private readonly ILogger<MarketSessionOpenCoordinator> _logger;
    private readonly TradingActivityLog? _activity;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private string? _lastSessionKey;

    public MarketSessionOpenCoordinator(
        IMarketCalendar calendar,
        IEnumerable<IMarketSessionOpenParticipant> participants,
        TimeProvider time,
        ILogger<MarketSessionOpenCoordinator> logger,
        TradingActivityLog? activity = null)
    {
        _calendar = calendar;
        _participants = participants.OrderBy(p => p.Order).ThenBy(p => p.Name).ToList();
        _time = time;
        _logger = logger;
        _activity = activity;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[MarketOpen] Coordinator started with {Count} participant(s): {Participants}.",
            _participants.Count,
            string.Join(", ", _participants.Select(p => $"{p.Order}:{p.Name}")));

        while (!stoppingToken.IsCancellationRequested)
        {
            var status = StatusNow();
            if (status.IsOpen)
            {
                await TryRunOpenSessionAsync(stoppingToken);
                await DelayAsync(OpenRecheck, stoppingToken);
                continue;
            }

            var delay = status.NextOpenPkt is { } next
                ? Until(next)
                : MissingScheduleRecheck;
            await DelayAsync(delay, stoppingToken);
        }
    }

    /// <summary>
    /// Runs the current opening once. Public for deterministic tests and an operator diagnostics path;
    /// duplicate callers are collapsed by the session key before any participant is entered.
    /// </summary>
    public async Task<bool> TryRunOpenSessionAsync(CancellationToken ct = default)
    {
        await _runGate.WaitAsync(ct);
        try
        {
            var status = StatusNow();
            if (!status.IsOpen || status.SessionOpenPkt is not { } scheduledOpen) return false;

            var key = $"{scheduledOpen:yyyy-MM-dd/HH:mm}";
            if (string.Equals(_lastSessionKey, key, StringComparison.Ordinal)) return false;

            // Claim before doing work. If one participant fails, periodic workers retry their own
            // operation; replaying every earlier participant would risk duplicate submissions.
            _lastSessionKey = key;
            var context = new MarketSessionOpenContext(scheduledOpen, status.PktNow, key);

            _logger.LogInformation(
                "[MarketOpen] Session {SessionKey} opened; starting {Count} participant(s), lag {Lag:F0}ms.",
                key, _participants.Count, context.StartLagMilliseconds);
            _activity?.Info(
                "Market", "Market opened — refreshing trading state",
                $"Session {key}; opening scheduler lag {context.StartLagMilliseconds:F0} ms.");

            foreach (var participant in _participants)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await participant.RunAtMarketOpenAsync(context, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[MarketOpen] Participant {Participant} failed for session {SessionKey}; "
                        + "later participants will still run and its periodic loop remains the retry.",
                        participant.Name, key);
                    _activity?.Error(
                        "Market", $"Opening task failed: {participant.Name}", ex.Message);
                }
            }

            return true;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private MarketStatus StatusNow() =>
        _calendar.GetStatus(_time.GetUtcNow().UtcDateTime);

    private TimeSpan Until(DateTime nextOpenPkt)
    {
        var now = StatusNow().PktNow;
        var delay = nextOpenPkt - now;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, _time, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
