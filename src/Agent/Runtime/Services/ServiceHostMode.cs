using Microsoft.Extensions.Logging;

namespace AgentFox.Runtime.Services;

/// <summary>
/// Represents the execution mode for FoxAgent when running as a service.
/// This mode disables interactive CLI and focuses on web API and background task processing.
/// </summary>
public class ServiceHostMode
{
    private readonly ServiceConfig _config;
    private readonly ILogger _logger;

    public bool IsServiceMode { get; private set; }

    public ServiceHostMode(ServiceConfig config, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
        IsServiceMode = false;
    }

    /// <summary>
    /// Initializes service mode flags and returns true if running in service mode.
    /// Should be called early in Program.Main before CLI/web setup.
    /// </summary>
    /// <remarks>
    /// Matches both the bare switch and the <c>--service-mode=true</c> form. The installer uses
    /// the <c>=</c> form because <see cref="Microsoft.Extensions.Configuration.IConfigurationBuilder"/>'s
    /// command-line provider treats a bare <c>--switch</c> as a key whose value is the NEXT
    /// token — so a bare <c>--service-mode</c> silently swallows the argument after it.
    /// </remarks>
    public static bool DetectServiceMode(string[] args)
    {
        return args.Any(a =>
            a.Equals("--service-mode", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("--service-mode=", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Enables service mode and logs the transition.
    /// </summary>
    public void EnableServiceMode()
    {
        IsServiceMode = true;
        _logger?.LogInformation(
            $"Service mode enabled for '{_config.ServiceName}' " +
            $"(port: {_config.Port}, heartbeat: {_config.HeartbeatIntervalSeconds}s)");
    }

    /// <summary>
    /// Returns logging configuration suitable for service operation.
    /// When in service mode, all console UI should be disabled.
    /// </summary>
    public ServiceModeConfig GetConfiguration()
    {
        return new ServiceModeConfig
        {
            IsServiceMode = IsServiceMode,
            UseFileLogging = true,
            UseConsoleUI = false,
            Port = _config.Port,
            LogFilePath = _config.LogPath.Replace("{workspace}", AppContext.BaseDirectory),
            HeartbeatIntervalSeconds = _config.HeartbeatIntervalSeconds,
            ServiceName = _config.ServiceName
        };
    }

    /// <summary>
    /// Configuration for service mode operation.
    /// </summary>
    public class ServiceModeConfig
    {
        public bool IsServiceMode { get; set; }
        public bool UseFileLogging { get; set; }
        public bool UseConsoleUI { get; set; }
        public int Port { get; set; }
        public string LogFilePath { get; set; } = string.Empty;
        public int HeartbeatIntervalSeconds { get; set; }
        public string ServiceName { get; set; } = string.Empty;
    }
}
