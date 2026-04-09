using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using Spectre.Console;

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
                var isAdministrator = IsAdministrator();

                // Second attempt: use pre-stored credentials (set by wizard before spinner),
                // or fall back to an interactive prompt (safe only outside a spinner context).
                string? user     = null;
                string? password = null;
                string? domain   = null;

                bool hasCredentials;
                if (!string.IsNullOrWhiteSpace(_config.InstallUserName))
                {
                    user           = _config.InstallUserName;
                    password       = _config.InstallPassword ?? string.Empty;
                    domain         = _config.InstallDomain;
                    hasCredentials = true;
                }
                else
                {
                    if (!isAdministrator)
                    {
                        result = await RunElevatedScCommandsAsync(_config.ServiceName, serviceExePath,
                            _config.GetEffectiveDisplayName(), _config.GetEffectiveDescription());
                        if (result.success)
                        {
                            var serviceExists = await ServiceExistsAsync();
                            var success = serviceExists;
                            return new ServiceResult(success,
                                $"Invoked command to install service '{_config.ServiceName}' as admin",
                                result.output + (!isAdministrator
                                    ? "\n\nTip: Run this wizard as Administrator to skip the credential prompt."
                                    : ""));
                        }
                        else
                        {
                            return new ServiceResult(false,
                                        $"Service '{_config.ServiceName}' not installed", (!isAdministrator
                                            ? "\n\nTip: Run this wizard as Administrator."
                                            : ""));
                        }
                    }
                    hasCredentials = false;
                }

                if (hasCredentials)
                {
                    result = await RunCommandWithCredentialsAsync("sc.exe",
                        $"create \"{_config.ServiceName}\" binPath= \"{serviceExePath}\" start= {(_config.AutoStart ? "auto" : "demand")} DisplayName= \"{_config.GetEffectiveDisplayName()}\"",
                        user!, password!, domain);
                }

                if (!result.success)
                    return new ServiceResult(false,
                        $"Failed to install service '{_config.ServiceName}'",
                        result.output + (!isAdministrator
                            ? "\n\nTip: Run this wizard as Administrator to skip the credential prompt."
                            : ""));
            }

            _logger?.LogInformation($"Service '{_config.ServiceName}' installed successfully.");

            // Set description
            await RunCommandAsync("sc.exe",
                $"description \"{_config.ServiceName}\" \"{_config.GetEffectiveDescription()}\"");

            // Auto-recovery: restart on failure — 3 attempts, 5-second delay each
            await RunCommandAsync("sc.exe",
                $"failure \"{_config.ServiceName}\" reset= 0 actions= restart/5000/restart/5000/restart/5000");

            // Trigger recovery on non-zero exit codes, not only on crashes
            await RunCommandAsync("sc.exe",
                $"failureflag \"{_config.ServiceName}\" 1");

            return new ServiceResult(true,
                $"Service '{_config.ServiceName}' installed successfully on port {_config.Port}",
                "Auto-recovery enabled: restarts automatically (up to 3×, 5 s delay) on any failure.");
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
                FileName               = command,
                Arguments              = arguments,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return (false, $"Failed to start process '{command}'");

            string output = await process.StandardOutput.ReadToEndAsync();
            string error  = await process.StandardError.ReadToEndAsync();
            process.WaitForExit();

            return process.ExitCode == 0 ? (true, output) : (false, error.Length > 0 ? error : output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Run an sc.exe command under explicitly supplied administrator credentials.
    /// </summary>
    private static async Task<(bool success, string output)> RunCommandWithCredentialsAsync(
        string command, string arguments, string user, string password, string? domain)
    {
        try
        {
            var securePassword = new SecureString();
            foreach (char c in password) securePassword.AppendChar(c);
            securePassword.MakeReadOnly();

            var psi = new ProcessStartInfo
            {
                FileName               = command,
                Arguments              = arguments,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                UserName               = user,
                Password               = securePassword,
                LoadUserProfile        = true,
            };

            // Null domain → local machine account; non-empty → ActiveDirectory domain
            if (!string.IsNullOrWhiteSpace(domain))
                psi.Domain = domain;

            using var process = Process.Start(psi);
            if (process == null) return (false, $"Failed to start process '{command}' as '{user}'");

            string output = await process.StandardOutput.ReadToEndAsync();
            string error  = await process.StandardError.ReadToEndAsync();
            process.WaitForExit();

            return process.ExitCode == 0 ? (true, output) : (false, error.Length > 0 ? error : output);
        }
        catch (Exception ex)
        {
            return (false, $"Could not run as '{user}': {ex.Message}");
        }
    }

    /// <summary>
    /// Run an sc.exe command as administrator.
    /// </summary>
    private static async Task<(bool success, string output)> RunCommandAsAdministratorAsync(
        string command, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = true,
                CreateNoWindow = true,
                Verb = "runas" // This will prompt for elevation
            };

            using var process = Process.Start(psi);
            if (process == null) return (false, $"Failed to start process '{command}' as administrator");

            await process.WaitForExitAsync();

            return process.ExitCode == 0 ? (true, string.Empty) : (false, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"Could not run as administrator: {ex.Message}");
        }
    }

    private static async Task<(bool success, string output)> RunElevatedScCommandsAsync(
        string serviceName,
        string serviceExePath,
        string displayName,
        string description)
    {
        var commands = new[]
        {
            $"sc.exe create \"{serviceName}\" binPath= \"{serviceExePath}\" start= auto DisplayName= \"{displayName}\"",
            $"sc.exe description \"{serviceName}\" \"{description}\"",
            $"sc.exe failure \"{serviceName}\" reset= 0 actions= restart/5000/restart/5000/restart/5000",
            $"sc.exe failureflag \"{serviceName}\" 1"
        };

        var tempScript = Path.Combine(Path.GetTempPath(), $"InstallService_{Guid.NewGuid():N}.ps1");
        File.WriteAllLines(tempScript, commands);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (false, "Failed to start elevated PowerShell");

            await process.WaitForExitAsync();
            return (process.ExitCode == 0, $"ExitCode={process.ExitCode}");
        }
        catch
        {
            File.Move(tempScript, tempScript+".bat");
            tempScript = tempScript+".bat";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{tempScript}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (false, "Failed to start elevated PowerShell");

            await process.WaitForExitAsync();
            return (process.ExitCode == 0, $"ExitCode={process.ExitCode}");
        }
        finally
        {
            try { File.Delete(tempScript); } catch { }
        }
    }

    public static bool IsAdministrator()
    {
        try
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        catch { }
        return false;
    }


    public static bool TryGetUserCredentials(out string? user, out string? password, out string? domain)
    {
        try
        {
            AnsiConsole.MarkupLine("  [yellow]⚠[/]  Administrator privileges are required to install the service.");
            AnsiConsole.MarkupLine("  [blue]│[/]   Enter credentials for an account with Administrator rights.");
            AnsiConsole.MarkupLine("  [blue]│[/]");

            var enteredUser = AnsiConsole.Prompt(
                new TextPrompt<string>("  [green]◇[/] Username:")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(enteredUser))
            {
                user = password = domain = null;
                return false;
            }

            var enteredPassword = AnsiConsole.Prompt(
                new TextPrompt<string>("  [green]◇[/] Password:")
                    .Secret()
                    .AllowEmpty());

            var enteredDomain = AnsiConsole.Prompt(
                new TextPrompt<string>("  [green]◇[/] Domain  (leave blank for local machine):")
                    .AllowEmpty());

            user     = enteredUser;
            password = enteredPassword ?? string.Empty;
            domain   = string.IsNullOrWhiteSpace(enteredDomain) ? null : enteredDomain;
            return true;
        }
        catch
        {
            user = password = domain = null;
            return false;
        }
    }
}
