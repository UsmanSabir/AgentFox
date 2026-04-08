using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentFox.Runtime.Services.Windows;

/// <summary>
/// Windows service manager for FoxAgent.
/// Handles installation, uninstallation, and control of the service using Windows Service APIs.
/// </summary>
public class WindowsServiceManager : IServiceManager
{
    private readonly ServiceConfig _config;
    private readonly ILogger? _logger;

    public string PlatformName => "Windows";

    public WindowsServiceManager(ServiceConfig config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    public async Task<ServiceResult> InstallAsync()
    {
        try
        {
            _logger?.LogInformation($"Installing Windows service '{_config.ServiceName}'...");

            // Check if service already exists
            if (await ServiceExistsAsync())
            {
                return new ServiceResult(false, 
                    $"Service '{_config.ServiceName}' is already installed. Uninstall it first with --uninstall-service.");
            }

            // Build command line to launch FoxAgent in service mode
            string exePath = GetExecutablePath();
            string serviceExePath = $"\"{exePath}\" --service-mode --modules web";

            // Use sc.exe to install service
            var result = await RunCommandAsync("sc.exe", 
                $"create \"{_config.ServiceName}\" binPath= \"{serviceExePath}\" start= {(_config.AutoStart ? "auto" : "demand")} DisplayName= \"{_config.GetEffectiveDisplayName()}\"");

            if (!result.success)
            {
                return new ServiceResult(false, 
                    $"Failed to install service '{_config.ServiceName}'",
                    result.output);
            }

            _logger?.LogInformation($"Service '{_config.ServiceName}' installed successfully.");

            // Set description
            await RunCommandAsync("sc.exe", 
                $"description \"{_config.ServiceName}\" \"{_config.GetEffectiveDescription()}\"");

            return new ServiceResult(true, 
                $"Service '{_config.ServiceName}' installed successfully on port {_config.Port}");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error installing service");
            return new ServiceResult(false, 
                $"Error installing service: {ex.Message}", 
                ex.StackTrace ?? "");
        }
    }

    public async Task<ServiceResult> UninstallAsync()
    {
        try
        {
            _logger?.LogInformation($"Uninstalling Windows service '{_config.ServiceName}'...");

            if (!await ServiceExistsAsync())
            {
                return new ServiceResult(false, 
                    $"Service '{_config.ServiceName}' is not installed.");
            }

            // Try to stop the service first
            await RunCommandAsync("sc.exe", $"stop \"{_config.ServiceName}\"");
            
            // Wait a bit for service to stop
            await Task.Delay(1000);

            // Delete using sc.exe
            var result = await RunCommandAsync("sc.exe", $"delete \"{_config.ServiceName}\"");

            if (!result.success)
            {
                return new ServiceResult(false, 
                    $"Failed to uninstall service '{_config.ServiceName}'",
                    result.output);
            }

            _logger?.LogInformation($"Service '{_config.ServiceName}' uninstalled successfully.");

            return new ServiceResult(true, 
                $"Service '{_config.ServiceName}' uninstalled successfully.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error uninstalling service");
            return new ServiceResult(false, 
                $"Error uninstalling service: {ex.Message}",
                ex.StackTrace ?? "");
        }
    }

    public async Task<ServiceResult> StartAsync()
    {
        try
        {
            _logger?.LogInformation($"Starting Windows service '{_config.ServiceName}'...");

            if (!await ServiceExistsAsync())
            {
                return new ServiceResult(false, 
                    $"Service '{_config.ServiceName}' is not installed.");
            }

            var result = await RunCommandAsync("sc.exe", $"start \"{_config.ServiceName}\"");
            if (!result.success)
            {
                return new ServiceResult(false, 
                    $"Failed to start service",
                    result.output);
            }

            _logger?.LogInformation($"Service '{_config.ServiceName}' started successfully.");

            return new ServiceResult(true, 
                $"Service '{_config.ServiceName}' started successfully.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error starting service");
            return new ServiceResult(false, 
                $"Error starting service: {ex.Message}",
                ex.StackTrace ?? "");
        }
    }

    public async Task<ServiceResult> StopAsync()
    {
        try
        {
            _logger?.LogInformation($"Stopping Windows service '{_config.ServiceName}'...");

            if (!await ServiceExistsAsync())
            {
                return new ServiceResult(false, 
                    $"Service '{_config.ServiceName}' is not installed.");
            }

            var result = await RunCommandAsync("sc.exe", $"stop \"{_config.ServiceName}\"");
            if (!result.success)
            {
                return new ServiceResult(false, 
                    $"Failed to stop service",
                    result.output);
            }

            _logger?.LogInformation($"Service '{_config.ServiceName}' stopped successfully.");

            return new ServiceResult(true, 
                $"Service '{_config.ServiceName}' stopped successfully.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error stopping service");
            return new ServiceResult(false, 
                $"Error stopping service: {ex.Message}",
                ex.StackTrace ?? "");
        }
    }

    public async Task<ServiceResult> RestartAsync()
    {
        try
        {
            _logger?.LogInformation($"Restarting Windows service '{_config.ServiceName}'...");

            var stopResult = await StopAsync();
            if (!stopResult.Success)
                return stopResult;

            await Task.Delay(1000); // Brief delay between stop and start

            var startResult = await StartAsync();
            return startResult;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error restarting service");
            return new ServiceResult(false, 
                $"Error restarting service: {ex.Message}",
                ex.StackTrace ?? "");
        }
    }

    public async Task<ServiceResult> GetStatusAsync()
    {
        try
        {
            if (!await ServiceExistsAsync())
            {
                return new ServiceResult(true, 
                    $"Service '{_config.ServiceName}' is not installed.");
            }

            var result = await RunCommandAsync("sc.exe", $"query \"{_config.ServiceName}\"");
            
            string status = "Unknown";
            if (result.output.Contains("RUNNING"))
                status = "Running";
            else if (result.output.Contains("STOPPED"))
                status = "Stopped";
            else if (result.output.Contains("START_PENDING"))
                status = "Starting";
            else if (result.output.Contains("STOP_PENDING"))
                status = "Stopping";

            return new ServiceResult(true, 
                $"Service '{_config.ServiceName}' status: {status}",
                $"Port: {_config.Port}\nOutput: {result.output}");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting service status");
            return new ServiceResult(false, 
                $"Error getting service status: {ex.Message}");
        }
    }

    // ── Helper Methods ─────────────────────────────────────────────────────

    private async Task<bool> ServiceExistsAsync()
    {
        try
        {
            var result = await RunCommandAsync("sc.exe", $"query \"{_config.ServiceName}\"");
            // sc.exe returns exit code 0 for existing service, non-zero for non-existing
            return result.success;
        }
        catch
        {
            return false;
        }
    }

    private string GetExecutablePath()
    {
        // Get the path to the currently running assembly
        string? assemblyPath = Path.GetDirectoryName(typeof(Program).Assembly.Location);
        if (string.IsNullOrEmpty(assemblyPath))
            throw new InvalidOperationException("Cannot determine executable path");

        string exePath = Path.Combine(assemblyPath, "AgentFox.exe");
        
        // On .NET, the executable might be named differently or use dotnet run
        if (!File.Exists(exePath))
        {
            // Fallback: use dotnet command with the full assembly path
            exePath = Path.Combine(assemblyPath, "AgentFox.dll");
        }

        return exePath;
    }

    private async Task<(bool success, string output)> RunCommandAsync(string command, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    return (false, $"Failed to start process '{command}'");

                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();

                process.WaitForExit();

                if (process.ExitCode == 0)
                    return (true, output);

                return (false, error.Length > 0 ? error : output);
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
