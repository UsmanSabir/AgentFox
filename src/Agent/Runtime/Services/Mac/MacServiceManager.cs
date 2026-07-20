using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace AgentFox.Runtime.Services.Mac;

/// <summary>
/// macOS service manager for FoxAgent using launchd.
/// Handles installation, uninstallation, and control of the service using launchctl.
/// Can install both per-user (~/Library/LaunchAgents) and system-wide (/Library/LaunchDaemons).
/// </summary>
public class MacServiceManager : IServiceManager
{
    private readonly ServiceConfig _config;
    private readonly ILogger? _logger;

    public string PlatformName => "macOS";

    private bool IsSystemWide => _config.RunAsAdmin;
    
    private string LaunchPlistPath => IsSystemWide
        ? $"/Library/LaunchDaemons/com.agentfox.{_config.ServiceName}.plist"
        : $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/Library/LaunchAgents/com.agentfox.{_config.ServiceName}.plist";

    private string LogPath => _config.LogPath
        .Replace("{workspace}", Directory.GetCurrentDirectory());

    private string PlistIdentifier => $"com.agentfox.{_config.ServiceName}";

    public MacServiceManager(ServiceConfig config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    public async Task<ServiceResult> InstallAsync()
    {
        try
        {
            _logger?.LogInformation($"Installing launchd service '{_config.ServiceName}'...");

            // Check if already installed
            if (await ServiceExistsAsync())
            {
                return new ServiceResult(false, 
                    $"Service '{_config.ServiceName}' is already installed. Uninstall first with --uninstall-service.");
            }

            // Create log directory
            string? logDir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            // Generate launchd plist file
            string plistContent = GenerateLaunchPlist();

            // Write plist file
            var writeResult = await WriteFileAsync(LaunchPlistPath, plistContent);
            if (!writeResult.success)
            {
                return new ServiceResult(false, 
                    $"Failed to create launchd plist at '{LaunchPlistPath}'",
                    $"Error: {writeResult.error}\n\n" +
                    (IsSystemWide ? "Note: System-wide service installation requires admin/sudo privileges.\n" +
                    "Run: sudo dotnet run -- --install-service" 
                    : "Note: Per-user service installation requires write access to ~/Library/LaunchAgents"));
            }

            // Set proper file permissions
            if (IsSystemWide)
            {
                await RunCommandAsync("sudo", $"chmod 644 {LaunchPlistPath}");
                await RunCommandAsync("sudo", $"chown root:wheel {LaunchPlistPath}");
            }
            else
            {
                await RunCommandAsync("chmod", $"644 {LaunchPlistPath}");
            }

            // Load the service
            var loadResult = await RunCommandAsync("launchctl", $"load {LaunchPlistPath}");
            if (!loadResult.success && !loadResult.output.Contains("already loaded"))
            {
                return new ServiceResult(false, 
                    $"Failed to load launchd service",
                    loadResult.output);
            }

            _logger?.LogInformation($"Service '{_config.ServiceName}' installed successfully.");

            return new ServiceResult(true, 
                $"Service '{_config.ServiceName}' installed successfully on port {_config.Port}",
                $"Plist file: {LaunchPlistPath}\n" +
                $"Service type: {(IsSystemWide ? "System-wide (requires admin)" : "Per-user")}\n" +
                $"To check status: launchctl list | grep {PlistIdentifier}\n" +
                $"To view logs: log stream --predicate 'process=={_config.ServiceName}'");
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
            _logger?.LogInformation($"Uninstalling launchd service '{_config.ServiceName}'...");

            if (!await ServiceExistsAsync())
            {
                return new ServiceResult(false, 
                    $"Service '{_config.ServiceName}' is not installed.");
            }

            // Unload the service
            var unloadResult = await RunCommandAsync("launchctl", $"unload {LaunchPlistPath}");
            if (!unloadResult.success)
            {
                _logger?.LogWarning($"Warning: Failed to unload service, continuing with removal: {unloadResult.output}");
            }

            // Remove plist file
            var removeResult = await RunCommandAsync("rm", $"-f {LaunchPlistPath}");
            if (!removeResult.success)
            {
                return new ServiceResult(false, 
                    $"Failed to remove launchd plist file",
                    removeResult.output);
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
            _logger?.LogInformation($"Starting launchd service '{_config.ServiceName}'...");

            if (!await ServiceExistsAsync())
            {
                return new ServiceResult(false, 
                    $"Service '{_config.ServiceName}' is not installed.");
            }

            var result = await RunCommandAsync("launchctl", $"start {PlistIdentifier}");
            if (!result.success && !result.output.Contains("already running"))
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
            _logger?.LogInformation($"Stopping launchd service '{_config.ServiceName}'...");

            if (!await ServiceExistsAsync())
            {
                return new ServiceResult(false, 
                    $"Service '{_config.ServiceName}' is not installed.");
            }

            var result = await RunCommandAsync("launchctl", $"stop {PlistIdentifier}");
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
            _logger?.LogInformation($"Restarting launchd service '{_config.ServiceName}'...");

            if (!await ServiceExistsAsync())
            {
                return new ServiceResult(false, 
                    $"Service '{_config.ServiceName}' is not installed.");
            }

            await StopAsync();
            await Task.Delay(1000);
            
            var result = await StartAsync();
            return result;
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

            var listResult = await RunCommandAsync("launchctl", $"list | grep {PlistIdentifier}");
            
            var infoResult = await RunCommandAsync("launchctl", $"info {PlistIdentifier}");

            return new ServiceResult(true, 
                $"Status of '{_config.ServiceName}'",
                listResult.output.Length > 0 ? listResult.output : infoResult.output);
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
            // Check if plist file exists
            if (File.Exists(LaunchPlistPath))
                return true;

            // Also check via launchctl
            var result = await RunCommandAsync("launchctl", $"list | grep {PlistIdentifier}");
            return result.success && result.output.Contains(PlistIdentifier);
        }
        catch
        {
            return false;
        }
    }

    private string GenerateLaunchPlist()
    {
        string dllPath = Path.Combine(AppContext.BaseDirectory, "AgentFox.dll");
        
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>{PlistIdentifier}</string>
    
    <key>ProgramArguments</key>
    <array>
        <string>/usr/bin/dotnet</string>
        <string>{dllPath}</string>
        <string>--service-mode</string>
        <string>--modules</string>
        <string>web</string>
    </array>
    
    <key>WorkingDirectory</key>
    <string>{Directory.GetCurrentDirectory()}</string>
    
    <key>StandardOutPath</key>
    <string>{LogPath}</string>
    
    <key>StandardErrorPath</key>
    <string>{LogPath}</string>
    
    <key>KeepAlive</key>
    <true/>
    
    <key>RunAtLoad</key>
    <{(_config.AutoStart ? "true" : "false")}/>
    
    <key>StartInterval</key>
    <integer>60</integer>
    
    <key>TimeOut</key>
    <integer>30</integer>
    
    <key>ProcessType</key>
    <string>Standard</string>
    
    <key>EnvironmentVariables</key>
    <dict>
        <key>DOTNET_ENVIRONMENT</key>
        <string>Production</string>
        <key>ASPNETCORE_URLS</key>
        <string>http://localhost:{_config.Port}</string>
        {string.Join("\n        ", _config.EnvironmentVariables.Select(kvp => $"<key>{kvp.Key}</key>\n        <string>{kvp.Value}</string>"))}
    </dict>
</dict>
</plist>
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
            // Ensure directory exists
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Write file directly or with sudo if needed
            if (IsSystemWide)
            {
                // Use sudo tee to write as root
                var psi = new ProcessStartInfo
                {
                    FileName = "sh",
                    Arguments = $"-c \"echo '{EscapeForShell(content)}' | sudo tee {path} > /dev/null\"",
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
            else
            {
                // Write directly as user
                await File.WriteAllTextAsync(path, content);
                return (true, "");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private string EscapeForShell(string text)
    {
        // Basic shell escaping
        return text.Replace("'", "'\\''");
    }
}
