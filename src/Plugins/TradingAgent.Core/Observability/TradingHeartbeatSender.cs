using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Market;

namespace TradingAgent.Observability;

/// <summary>
/// Pushes this agent's <see cref="TradingHealthReport"/> to the fleet watchdog (plan step 7; owner: one fleet watchdog
/// with per-tenant heartbeats — the tenant's own alerts go to the tenant, fleet failures to the owner).
///
/// <para>
/// <b>Push, not poll.</b> Hosted agents are on their own networks, never published on a port, and stopped outside
/// market hours, so the watchdog cannot reach them to ask; and the failure it exists for — a dead process — is exactly
/// the one that stops answering. The watchdog's rule is therefore "no heartbeat for two minutes inside a session", and
/// that only works if the agent is the one speaking. Inside a session it sends every
/// <see cref="WatchdogOptions.IntervalSeconds"/>; it also sends once at start and once when the host is stopping, so a
/// deliberate stop reads as one.
/// </para>
///
/// <para>Off unless <see cref="WatchdogOptions.HeartbeatUrl"/> is set. A failed send is logged and never retried
/// in a loop: the next beat is the retry, and a watchdog that hears nothing does the alerting.</para>
/// </summary>
public sealed class TradingHeartbeatSender : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TradingHealthReporter _reporter;
    private readonly IMarketCalendar _calendar;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<TradingHeartbeatSender> _logger;
    private readonly HttpClient _http;

    public TradingHeartbeatSender(
        TradingHealthReporter reporter,
        IMarketCalendar calendar,
        IOptions<TradingAgentOptions> options,
        ILogger<TradingHeartbeatSender> logger,
        HttpMessageHandler? handler = null)
    {
        _reporter = reporter;
        _calendar = calendar;
        _options = options;
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            _logger.LogInformation("[Watchdog] No heartbeat URL is configured; heartbeats are off.");
            return;
        }

        await SendAsync("start", stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_options.Value.Watchdog.IntervalSeconds, 15, 600)), stoppingToken); }
            catch (OperationCanceledException) { break; }
            if (_calendar.GetStatus().IsOpen) await SendAsync("beat", stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Enabled) await SendAsync("stopping", cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private bool Enabled => !string.IsNullOrWhiteSpace(_options.Value.Watchdog.HeartbeatUrl);

    /// <summary>Sends one heartbeat. Internal for tests; returns whether the watchdog accepted it.</summary>
    internal async Task<bool> SendAsync(string kind, CancellationToken ct)
    {
        var watchdog = _options.Value.Watchdog;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, watchdog.HeartbeatUrl)
            {
                Content = JsonContent.Create(new { kind, report = _reporter.Report() }, options: Json)
            };
            if (!string.IsNullOrWhiteSpace(watchdog.Token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", watchdog.Token);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("[Watchdog] The watchdog answered {Status} to a {Kind} heartbeat.", (int)response.StatusCode, kind);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning("[Watchdog] A {Kind} heartbeat could not be sent: {Message}", kind, ex.Message);
            return false;
        }
    }
}
