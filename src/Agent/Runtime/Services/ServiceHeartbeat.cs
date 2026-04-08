using AgentFox.Agents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentFox.Runtime.Services;

/// <summary>
/// Background service that periodically sends heartbeat commands to the command queue.
/// Enables FoxAgent to perform scheduled tasks and health checks while running as a service.
/// Implements IHostedService for integration with ASP.NET Core hosting.
/// </summary>
public class ServiceHeartbeat : BackgroundService
{
    private readonly ICommandQueue _commandQueue;
    private readonly ServiceConfig _config;
    private readonly ILogger _logger;
    private readonly string _sessionKey;

    public ServiceHeartbeat(
        ICommandQueue commandQueue,
        ServiceConfig config,
        ILogger<ServiceHeartbeat> logger)
    {
        _commandQueue = commandQueue ?? throw new ArgumentNullException(nameof(commandQueue));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
        _sessionKey = "service-heartbeat";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_config.HeartbeatIntervalSeconds <= 0)
        {
            _logger.LogInformation("Service heartbeat is disabled (HeartbeatIntervalSeconds <= 0)");
            return;
        }

        _logger.LogInformation(
            $"Service heartbeat started (interval: {_config.HeartbeatIntervalSeconds}s)");

        int heartbeatCount = 0;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Wait for the interval before sending the first heartbeat
                await Task.Delay(
                    TimeSpan.FromSeconds(_config.HeartbeatIntervalSeconds),
                    stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                    break;

                heartbeatCount++;

                try
                {
                    // Create and queue a heartbeat command
                    var heartbeatCommand = new HeartbeatCommand(
                        runId: Guid.NewGuid().ToString(),
                        sessionKey: _sessionKey);

                    _commandQueue.Enqueue(heartbeatCommand);

                    _logger.LogDebug(
                        $"Service heartbeat #{heartbeatCount} sent " +
                        $"(timestamp: {DateTime.UtcNow:O}, interval: {_config.HeartbeatIntervalSeconds}s)");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        $"Error sending heartbeat #{heartbeatCount}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Service heartbeat stopped (cancellation requested)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in service heartbeat");
        }

        _logger.LogInformation(
            $"Service heartbeat terminated (total heartbeats sent: {heartbeatCount})");
    }
}

/// <summary>
/// Heartbeat command sent periodically by the service to trigger health checks and scheduled tasks.
/// </summary>
public class HeartbeatCommand : ICommand
{
    public string RunId { get; }
    public string SessionKey { get; }
    public CommandLane Lane => CommandLane.Main;
    public int Priority => 0;
    public DateTime CreatedAt { get; }

    public HeartbeatCommand(string runId, string sessionKey)
    {
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        SessionKey = sessionKey ?? throw new ArgumentNullException(nameof(sessionKey));
        CreatedAt = DateTime.UtcNow;
    }
}
