namespace AgentFox.Runtime.Services;

/// <summary>
/// Configuration for FoxAgent service installation and behavior.
/// Binds from appsettings.json under the "Services" section.
/// </summary>
public class ServiceConfig
{
    /// <summary>
    /// Whether the service feature is enabled. Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Service name (used in registry on Windows, systemd unit name on Linux, identifier on macOS).
    /// Typically matches project name. Default: "AgentFox"
    /// </summary>
    public string ServiceName { get; set; } = "AgentFox";

    /// <summary>
    /// Display name shown in Service managers (Windows Services.msc, systemctl, etc.).
    /// Default: "{ServiceName} - Multi-Agent AI Framework"
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Description of the service shown in service managers.
    /// Default: "FoxAgent multi-agent AI framework running in web mode with scheduling and memory."
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// HTTP port the web API service runs on. Default: 8080
    /// </summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// Whether the service runs with admin/root privileges. Default: true
    /// Windows: Local System account
    /// Linux: root user (requires sudo to install)
    /// macOS: root for system-wide service, or user account for per-user service
    /// </summary>
    public bool RunAsAdmin { get; set; } = true;

    /// <summary>
    /// Whether the service automatically starts on system boot. Default: true
    /// </summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>
    /// Path where service logs are written. Supports {workspace} placeholder.
    /// Default: "{workspace}/logs/service.log"
    /// </summary>
    public string LogPath { get; set; } = string.Empty;

    /// <summary>
    /// Interval in seconds between service heartbeat checks. Default: 300 (5 minutes)
    /// Set to 0 to disable heartbeat.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Additional environment variables to pass to the service process.
    /// Dictionary of key-value pairs.
    /// </summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    /// <summary>
    /// Generates and returns the effective display name with ServiceName substituted if DisplayName is empty.
    /// </summary>
    public string GetEffectiveDisplayName()
    {
        if (!string.IsNullOrWhiteSpace(DisplayName))
            return DisplayName;
        return $"{ServiceName} - Multi-Agent AI Framework";
    }

    /// <summary>
    /// Generates and returns the effective description with ServiceName substituted if Description is empty.
    /// </summary>
    public string GetEffectiveDescription()
    {
        if (!string.IsNullOrWhiteSpace(Description))
            return Description;
        return "Multi-agent AI framework running in web mode with agent processing, memory, scheduling, and MCP support.";
    }
}
