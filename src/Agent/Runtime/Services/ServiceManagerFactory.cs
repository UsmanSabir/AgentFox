using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AgentFox.Runtime.Services;

/// <summary>
/// Factory for creating platform-specific IServiceManager implementations.
/// Detects the operating system and returns the appropriate service manager.
/// </summary>
public static class ServiceManagerFactory
{
    /// <summary>
    /// Creates a platform-specific IServiceManager based on the current OS.
    /// </summary>
    /// <param name="config">Service configuration</param>
    /// <param name="logger">Logger for diagnostic output (optional)</param>
    /// <returns>Platform-specific IServiceManager instance</returns>
    /// <exception cref="NotSupportedException">Thrown if the current platform is not supported</exception>
    public static IServiceManager Create(ServiceConfig config, ILogger? logger = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            logger?.LogInformation("Detected Windows platform; using WindowsServiceManager");
            return new Windows.WindowsServiceManager(config, logger!);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            logger?.LogInformation("Detected Linux platform; using LinuxServiceManager");
            return new Linux.LinuxServiceManager(config, logger!);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            logger?.LogInformation("Detected macOS platform; using MacServiceManager");
            return new Mac.MacServiceManager(config, logger!);
        }
        else
        {
            throw new NotSupportedException(
                $"Operating system '{RuntimeInformation.OSDescription}' is not supported for service management. " +
                "Supported platforms: Windows, Linux, macOS");
        }
    }

    /// <summary>
    /// Gets the current platform name.
    /// </summary>
    public static string GetCurrentPlatformName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "Linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "macOS";
        return "Unknown";
    }

    /// <summary>
    /// Checks if service management is supported on the current platform.
    /// </summary>
    public static bool IsSupported()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
               RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
               RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    }
}
