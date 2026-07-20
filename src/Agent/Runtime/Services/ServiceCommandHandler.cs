using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Text;

namespace AgentFox.Runtime.Services;

/// <summary>
/// Handles service management commands from both CLI flags and REPL interface.
/// Provides a unified interface for service installation, uninstallation, and control.
/// </summary>
public class ServiceCommandHandler
{
    private readonly ServiceConfig _config;
    private readonly ILogger _logger;

    public ServiceCommandHandler(ServiceConfig config, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    /// <summary>
    /// Factory method to create a handler from configuration.
    /// </summary>
    public static ServiceCommandHandler CreateFromConfiguration(IConfiguration configuration, ILogger logger)
    {
        var config = new ServiceConfig();
        configuration.GetSection("Services").Bind(config);
        
        // Set defaults if not configured
        if (string.IsNullOrWhiteSpace(config.ServiceName))
            config.ServiceName = "AgentFox";
        if (string.IsNullOrWhiteSpace(config.LogPath))
            config.LogPath = "{workspace}/logs/service.log";
        
        return new ServiceCommandHandler(config, logger);
    }

    /// <summary>
    /// Processes a service command from CLI arguments or REPL input.
    /// </summary>
    public async Task<ServiceResult> ProcessCommandAsync(string command)
    {
        if (!ServiceManagerFactory.IsSupported())
        {
            return new ServiceResult(false, 
                $"Service management is not supported on {ServiceManagerFactory.GetCurrentPlatformName()}");
        }

        var serviceManager = ServiceManagerFactory.Create(_config, _logger);

        return command.ToLowerInvariant().Trim() switch
        {
            "install-service" or "--install-service" => 
                await serviceManager.InstallAsync(),
            
            "uninstall-service" or "--uninstall-service" => 
                await serviceManager.UninstallAsync(),
            
            "start-service" or "--start-service" => 
                await serviceManager.StartAsync(),
            
            "stop-service" or "--stop-service" => 
                await serviceManager.StopAsync(),
            
            "restart-service" or "--restart-service" => 
                await serviceManager.RestartAsync(),
            
            "service-status" or "--service-status" => 
                await serviceManager.GetStatusAsync(),
            
            "service-config" or "--service-config" =>
                new ServiceResult(true, 
                    $"Service Configuration for '{_config.ServiceName}'",
                    FormatServiceConfig()),
            
            _ => new ServiceResult(false, 
                $"Unknown service command: '{command}'",
                "Available commands: install-service, uninstall-service, start-service, stop-service, restart-service, service-status, service-config")
        };
    }

    /// <summary>
    /// Checks if the given string is a recognized service command.
    /// </summary>
    public static bool IsServiceCommand(string input)
    {
        var commands = new[]
        {
            "install-service", "uninstall-service", "start-service", "stop-service", 
            "restart-service", "service-status", "service-config",
            "--install-service", "--uninstall-service", "--start-service", "--stop-service",
            "--restart-service", "--service-status", "--service-config"
        };

        return commands.Contains(input?.ToLowerInvariant());
    }

    /// <summary>
    /// Formats the service configuration for display.
    /// </summary>
    private string FormatServiceConfig()
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("[bold]Service Configuration[/]");
        sb.AppendLine($"  Service Name:        {_config.ServiceName}");
        sb.AppendLine($"  Display Name:        {_config.GetEffectiveDisplayName()}");
        sb.AppendLine($"  Description:         {_config.GetEffectiveDescription()}");
        sb.AppendLine($"  Port:                {_config.Port}");
        sb.AppendLine($"  Run as Admin:        {_config.RunAsAdmin}");
        sb.AppendLine($"  Auto-Start:          {_config.AutoStart}");
        sb.AppendLine($"  Log Path:            {_config.LogPath}");
        sb.AppendLine($"  Heartbeat Interval:  {_config.HeartbeatIntervalSeconds}s");
        sb.AppendLine($"  Platform:            {ServiceManagerFactory.GetCurrentPlatformName()}");
        
        return sb.ToString();
    }
}
