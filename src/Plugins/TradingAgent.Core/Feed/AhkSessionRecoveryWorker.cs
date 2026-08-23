using AgentFox.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingAgent.Config;
using TradingAgent.Observability;
using TradingAgent.Reconciliation;

namespace TradingAgent.Feed;

/// <summary>
/// Single background owner of the AHK session lifecycle. It renews an existing session through the
/// cheap <c>/Home/Relogin</c> keepalive before expiry and recovers a genuinely expired session under
/// the portal client's global login cooldown/backoff. It does not create a session on host startup:
/// recovery begins only after some real consumer has requested the broker once.
/// </summary>
public sealed class AhkSessionRecoveryWorker : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ActiveCheck = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DormantCheck = TimeSpan.FromSeconds(30);

    private readonly AhkPortalClient _portal;
    private readonly IRuntimePluginOptions<AhkFeedConfig> _feedConfig;
    private readonly IRuntimePluginOptions<AhkConfig> _brokerConfig;
    private readonly BrokerReconciliationWorker _reconciliation;
    private readonly ILogger<AhkSessionRecoveryWorker> _logger;
    private readonly TradingActivityLog? _activity;

    private DateTime _nextKeepAliveUtc = DateTime.MinValue;
    private int _consecutiveKeepAliveFailures;

    public AhkSessionRecoveryWorker(
        AhkPortalClient portal,
        IRuntimePluginOptions<AhkFeedConfig> feedConfig,
        IRuntimePluginOptions<AhkConfig> brokerConfig,
        BrokerReconciliationWorker reconciliation,
        ILogger<AhkSessionRecoveryWorker> logger,
        TradingActivityLog? activity = null)
    {
        _portal = portal;
        _feedConfig = feedConfig;
        _brokerConfig = brokerConfig;
        _reconciliation = reconciliation;
        _logger = logger;
        _activity = activity;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = ActiveCheck;
            try
            {
                if (!_portal.AutomaticRecoveryArmed)
                {
                    // No component has needed the broker in this process. Do not turn ordinary host
                    // startup into a login; the first feed/order/account consumer arms recovery.
                    delay = DormantCheck;
                }
                else if (_portal.FreshLoginRequired)
                {
                    if (await _portal.EstablishFreshLoginAsync(stoppingToken))
                        await SessionRecoveredAsync(
                            "The stale broker session was replaced automatically.", stoppingToken);
                }
                else if (!_portal.HasSession)
                {
                    if (await _portal.EnsureSessionAsync(stoppingToken))
                        await SessionRecoveredAsync(
                            "The expired broker session was restored automatically.", stoppingToken);
                }
                else if (DateTime.UtcNow >= _nextKeepAliveUtc)
                {
                    await KeepAliveAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The client is fail-soft already; this protects the lifecycle loop itself from an
                // unexpected bug and leaves the next bounded pass able to recover.
                _logger.LogWarning(ex, "[AhkSession] Background session maintenance failed.");
            }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task KeepAliveAsync(CancellationToken ct)
    {
        var intervalSeconds = Math.Max(15, _feedConfig.Current.ReloginSeconds);
        if (await _portal.ReloginAsync(ct))
        {
            if (_consecutiveKeepAliveFailures > 0)
                _logger.LogInformation("[AhkSession] Broker session keepalive recovered.");

            _consecutiveKeepAliveFailures = 0;
            _nextKeepAliveUtc = DateTime.UtcNow.AddSeconds(intervalSeconds);
            return;
        }

        if (!_portal.HasSession)
        {
            // A definitive expiry (8, redirect, 401/403 or login HTML) invalidates the session.
            // The next loop performs one login, subject to the shared cooldown/backoff.
            _nextKeepAliveUtc = DateTime.MinValue;
            return;
        }

        // Network failure, 5xx, or an unknown response is not proof of expiry. Keep the existing
        // session and retry Relogin with backoff; starting a new login while the portal is down is
        // exactly how an outage becomes an account block.
        _consecutiveKeepAliveFailures++;
        var retry = AhkSessionRetryPolicy.KeepAliveBackoff(
            _consecutiveKeepAliveFailures,
            (int)Math.Ceiling(intervalSeconds),
            _brokerConfig.Current.LoginRetryMaxSeconds);
        _nextKeepAliveUtc = DateTime.UtcNow + retry;

        _logger.LogWarning(
            "[AhkSession] Keepalive failed ({Failures} consecutive) but expiry was not confirmed; " +
            "retaining the session and retrying at {RetryUtc} without logging in.",
            _consecutiveKeepAliveFailures, _nextKeepAliveUtc);
    }

    private async Task SessionRecoveredAsync(string message, CancellationToken ct)
    {
        _consecutiveKeepAliveFailures = 0;
        _nextKeepAliveUtc = DateTime.UtcNow.AddSeconds(
            Math.Max(15, _feedConfig.Current.ReloginSeconds));
        _logger.LogInformation("[AhkSession] {Message}", message);
        _activity?.Info("Broker", message);

        // Do not leave the dashboard showing the old failed snapshot until the periodic worker's
        // next (potentially long) interval. This remains a passive read on the session just restored.
        await _reconciliation.RunNowAsync(ct);
    }
}
