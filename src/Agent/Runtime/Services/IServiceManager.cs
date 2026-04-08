namespace AgentFox.Runtime.Services;

/// <summary>
/// Abstraction for platform-specific service management (Windows Service, systemd, launchd, etc.).
/// Provides methods for installing, uninstalling, and controlling FoxAgent as a system service.
/// </summary>
public interface IServiceManager
{
    /// <summary>
    /// Installs FoxAgent as a service on the current platform.
    /// </summary>
    /// <returns>ServiceResult indicating success or failure with detailed message.</returns>
    Task<ServiceResult> InstallAsync();

    /// <summary>
    /// Uninstalls the FoxAgent service from the current platform.
    /// </summary>
    /// <returns>ServiceResult indicating success or failure with detailed message.</returns>
    Task<ServiceResult> UninstallAsync();

    /// <summary>
    /// Starts the FoxAgent service.
    /// </summary>
    /// <returns>ServiceResult indicating success or failure with detailed message.</returns>
    Task<ServiceResult> StartAsync();

    /// <summary>
    /// Stops the running FoxAgent service.
    /// </summary>
    /// <returns>ServiceResult indicating success or failure with detailed message.</returns>
    Task<ServiceResult> StopAsync();

    /// <summary>
    /// Restarts the FoxAgent service.
    /// </summary>
    /// <returns>ServiceResult indicating success or failure with detailed message.</returns>
    Task<ServiceResult> RestartAsync();

    /// <summary>
    /// Gets the current status of the FoxAgent service.
    /// </summary>
    /// <returns>ServiceResult with status information (e.g., "Running", "Stopped", "Not Installed").</returns>
    Task<ServiceResult> GetStatusAsync();

    /// <summary>
    /// Gets the platform name this manager supports (e.g., "Windows", "Linux", "macOS").
    /// </summary>
    string PlatformName { get; }
}

/// <summary>
/// Result of a service management operation.
/// </summary>
public class ServiceResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;

    public ServiceResult(bool success, string message, string details = "")
    {
        Success = success;
        Message = message;
        Details = details;
    }

    public override string ToString()
    {
        if (Details.Length > 0)
            return $"{(Success ? "✓" : "✗")} {Message}\n{Details}";
        return $"{(Success ? "✓" : "✗")} {Message}";
    }
}
