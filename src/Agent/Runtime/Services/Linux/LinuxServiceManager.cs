using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentFox.Runtime.Services.Linux;

/// <summary>
/// Linux service manager for FoxAgent using systemd.
/// Handles installation, uninstallation, and control of the service using systemctl.
/// Requires root privileges to install/uninstall.
/// </summary>
public class LinuxServiceManager : IServiceManager
{
    private readonly ServiceConfig _config;
    private readonly ILogger? _logger;

    public string PlatformName => "Linux";

    private string SystemdUnitPath => $"/etc/systemd/system/{_config.ServiceName}.service";
    private string LogPath => _config.LogPath
        .Replace("{workspace}", Directory.GetCurrentDirectory());

    public LinuxServiceManager(ServiceConfig config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    public async Task<ServiceResult> InstallAsync()
    {
        try
        {
            _logger?.LogInformation($"Installing systemd service '{_config.ServiceName}'...");

            // Check if already installed
            if (await ServiceExistsAsync())
            {
                return new ServiceResult(false, 
                    $"Service '{_config.ServiceName}' is already installed. Uninstall first with --uninstall-service.");
            }

            // Generate systemd unit file
            string unitFile = GenerateSystemdUnitFile();

            // Write to /etc/systemd/system (requires root)
            var writeResult = await WriteFileAsync(SystemdUnitPath, unitFile);
            if (!writeResult.success)
            {
                return new ServiceResult(false, 
                    $"Failed to create systemd unit file at '{SystemdUnitPath}'",
                    $"Error: {writeResult.error}\n\nNote: Service installation requires root/sudo privileges.\n" +
                    "Run: sudo dotnet run -- --install-service");
            }

            // Set file permissions
            await RunCommandAsync("chmod", $"644 {SystemdUnitPath}");

            // Reload systemd daemon
            var reloadResult = await RunCommandAsync("systemctl", "daemon-reload");
            if (!reloadResult.success)
            {
                return new ServiceResult(false, 
                    $"Failed to reload systemd daemon",
                    reloadResult.output);
            }

            // Enable service (auto-start on boot)
            if (_config.AutoStart)
            {
                var enableResult = await RunCommandAsync("systemctl", $"enable {_config.ServiceName}");
                if (!enableResult.success)
                {
                    return new ServiceResult(false, 
                        $"Failed to enable service for auto-start",
                        enableResult.output);
                }
            }

            _logger?.LogInformation($"Service '{_config.ServiceName}' installed successfully.");

            return new ServiceResult(true, 
                $"Service '{_config.ServiceName}' installed successfully on port {_config.Port}",
                $"Unit file: {SystemdUnitPath}\n" +
                $"To start the service: systemctl start {_config.ServiceName}\n" +
                $"To check status: systemctl status {_config.ServiceName}");
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
            _logger?.LogInformation($"Uninstalling systemd service '{_config.ServiceName}'...");

            if (!await ServiceExistsAsync())
            {
                return new ServiceResult(false, 
                    $"Service '{_config.ServiceName}' is not installed.");
            }

            // Stop the service first
            await RunCommandAsync("systemctl", $"stop {_config.ServiceName}");

            // Disable auto-start
            await RunCommandAsync("systemctl", $"disable {_config.ServiceName}");

            // Remove unit file
            var removeResult = await RunCommandAsync("rm", $"-f {SystemdUnitPath}");
            if (!removeResult.success)
            {
                return new ServiceResult(false, 
                    $"Failed to remove systemd unit file",
                    removeResult.output);
            }

            // Reload systemd daemon
            await RunCommandAsync("systemctl", "daemon-reload");

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
            _logger?.LogInformation($"Starting systemd service '{_config.ServiceName}'...");

            if (!await ServiceExistsAsync())
            {
                return new ServiceResult(false, 
                    $"Service '{_config.ServiceName}' is not installed.");
            }

            var result = await RunCommandAsync("systemctl", $"start {_config.ServiceName}");
            if (!result.success)
            {
                return new ServiceResult(false, 
                    $"Failed to start service",
                    result.output);
            }

            // Verify it's running
            await Task.Delay(1000);
            var statusResult = await RunCommandAsync("systemctl", $"is-active {_config.ServiceName}");
            
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
            _logger?.LogInformation($"Stopping systemd service '{_config.ServiceName}'...");

            if (!await ServiceExistsAsync())
            {
                return new ServiceResult(false, 
                    $"Service '{_config.ServiceName}' is not installed.");
            }

            var result = await RunCommandAsync("systemctl", $"stop {_config.ServiceName}");
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
            _logger?.LogInformation($"Restarting systemd service '{_config.ServiceName}'...");

            if (!await ServiceExistsAsync())
            {
                return new ServiceResult(false, 
                    $"Service '{_config.ServiceName}' is not installed.");
            }

            var result = await RunCommandAsync("systemctl", $"restart {_config.ServiceName}");
            if (!result.success)
            {
                return new ServiceResult(false, 
                    $"Failed to restart service",
                    result.output);
            }

            _logger?.LogInformation($"Service '{_config.ServiceName}' restarted successfully.");

            return new ServiceResult(true, 
                $"Service '{_config.ServiceName}' restarted successfully.");
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

            var result = await RunCommandAsync("systemctl", $"status {_config.ServiceName}");
            
            return new ServiceResult(true, 
                $"Status of '{_config.ServiceName}'",
                result.output);
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
            // Check if systemd unit file exists
            if (File.Exists(SystemdUnitPath))
                return true;

            // Also check via systemctl
            var result = await RunCommandAsync("systemctl", $"list-units --all | grep {_config.ServiceName}");
            return result.success && result.output.Contains(_config.ServiceName);
        }
        catch
        {
            return false;
        }
    }

    private string GenerateSystemdUnitFile()
    {
        // Ensure log directory exists
        string logDir = Path.GetDirectoryName(LogPath) ?? "/var/log";
        
        return $@"[Unit]
Description={_config.GetEffectiveDisplayName()}
After=network.target

[Service]
Type=simple
User={(_config.RunAsAdmin ? "root" : "agentfox")}
WorkingDirectory={Directory.GetCurrentDirectory()}
ExecStart=/usr/bin/dotnet ""{Path.Combine(AppContext.BaseDirectory, "AgentFox.dll")}"" --service-mode --modules web
Restart=always
RestartSec=10
StandardOutput=append:{LogPath}
StandardError=append:{LogPath}
SyslogIdentifier={_config.ServiceName}
Environment=""DOTNET_ENVIRONMENT=Production""
Environment=""ASPNETCORE_URLS=http://localhost:{_config.Port}""
{string.Join("\n", _config.EnvironmentVariables.Select(kvp => $"Environment=\"{kvp.Key}={kvp.Value}\""))}

[Install]
WantedBy=multi-user.target
";
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

    private async Task<(bool success, string error)> WriteFileAsync(string path, string content)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sh",
                Arguments = $"-c \"echo '{EscapeForShell(content)}' | tee {path} > /dev/null\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    return (false, "Failed to start shell process");

                string error = await process.StandardError.ReadToEndAsync();
                process.WaitForExit();

                if (process.ExitCode == 0)
                    return (true, "");

                return (false, error);
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private string EscapeForShell(string text)
    {
        // Basic shell escaping - replace single quotes and escape for single-quoted string
        return text.Replace("'", "'\\''");
    }
}
