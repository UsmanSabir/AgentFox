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

    // {workspace} resolves to the install directory, NOT the current directory: the CWD belongs
    // to whoever happened to run the installer and is meaningless to a boot-time daemon.
    private string LogPath => _config.LogPath
        .Replace("{workspace}", ServiceLauncher.InstallDirectory);

    private string PlistIdentifier => $"com.agentfox.{_config.ServiceName}";

    private static bool IsRoot => ServiceLauncher.IsRootUser;

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
                    "Run: sudo agentfox --install-service"
                    : "Note: Per-user service installation requires write access to ~/Library/LaunchAgents"));
            }

            // Set proper file permissions
            if (IsSystemWide)
            {
                await RunPrivilegedAsync("chmod", "644", LaunchPlistPath);
                await RunPrivilegedAsync("chown", "root:wheel", LaunchPlistPath);
            }
            else
            {
                await RunCommandAsync("chmod", "644", LaunchPlistPath);
            }

            // Load the service. A system-wide daemon must be loaded as root, or launchctl
            // reports success while silently doing nothing for the LaunchDaemons domain.
            var loadResult = IsSystemWide
                ? await RunPrivilegedAsync("launchctl", "load", "-w", LaunchPlistPath)
                : await RunCommandAsync("launchctl", "load", "-w", LaunchPlistPath);
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
            var unloadResult = IsSystemWide
                ? await RunPrivilegedAsync("launchctl", "unload", "-w", LaunchPlistPath)
                : await RunCommandAsync("launchctl", "unload", "-w", LaunchPlistPath);
            if (!unloadResult.success)
            {
                _logger?.LogWarning($"Warning: Failed to unload service, continuing with removal: {unloadResult.output}");
            }

            // Remove plist file
            var removeResult = IsSystemWide
                ? await RunPrivilegedAsync("rm", "-f", LaunchPlistPath)
                : await RunCommandAsync("rm", "-f", LaunchPlistPath);
            if (!removeResult.success && File.Exists(LaunchPlistPath))
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

            var result = IsSystemWide
                ? await RunPrivilegedAsync("launchctl", "start", PlistIdentifier)
                : await RunCommandAsync("launchctl", "start", PlistIdentifier);
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

            var result = IsSystemWide
                ? await RunPrivilegedAsync("launchctl", "stop", PlistIdentifier)
                : await RunCommandAsync("launchctl", "stop", PlistIdentifier);
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

            // No shell here (UseShellExecute = false), so a pipe cannot be used: the old
            // "list | grep X" was passed to launchctl as literal arguments and always failed.
            var listResult = await RunCommandAsync("launchctl", "list", PlistIdentifier);

            return new ServiceResult(true, 
                $"Status of '{_config.ServiceName}'",
                listResult.output.Length > 0 ? listResult.output : "Not currently loaded.");
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
            // `launchctl list <label>` exits non-zero when the job is unknown - no pipe needed.
            var result = await RunCommandAsync("launchctl", "list", PlistIdentifier);
            return result.success;
        }
        catch
        {
            return false;
        }
    }

    private string GenerateLaunchPlist()
    {
        // Built from the resolved launcher rather than a hardcoded /usr/bin/dotnet: SIP means
        // nothing can live in /usr/bin on macOS (the real host is /usr/local/share/dotnet/dotnet),
        // and install.sh provisions .NET into ~/.dotnet anyway. A published apphost wins outright.
        string programArguments = string.Join("\n        ",
            ServiceLauncher.BuildProgramArguments()
                .Select(a => $"<string>{ServiceLauncher.EscapeXml(a)}</string>"));

        var environment = new List<string>
        {
            "<key>DOTNET_ENVIRONMENT</key>",
            "<string>Production</string>",
            "<key>ASPNETCORE_URLS</key>",
            $"<string>http://localhost:{_config.Port}</string>",
        };
        foreach (var kvp in _config.EnvironmentVariables)
        {
            environment.Add($"<key>{ServiceLauncher.EscapeXml(kvp.Key)}</key>");
            environment.Add($"<string>{ServiceLauncher.EscapeXml(kvp.Value)}</string>");
        }

        // NOTE: no StartInterval. Combined with KeepAlive it told launchd to relaunch the job on
        // a 60-second timer on top of keeping it alive, which is not what an always-on agent wants.
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>{ServiceLauncher.EscapeXml(PlistIdentifier)}</string>

    <key>ProgramArguments</key>
    <array>
        {programArguments}
    </array>

    <key>WorkingDirectory</key>
    <string>{ServiceLauncher.EscapeXml(ServiceLauncher.InstallDirectory)}</string>

    <key>StandardOutPath</key>
    <string>{ServiceLauncher.EscapeXml(LogPath)}</string>

    <key>StandardErrorPath</key>
    <string>{ServiceLauncher.EscapeXml(LogPath)}</string>

    <key>KeepAlive</key>
    <true/>

    <key>RunAtLoad</key>
    <{(_config.AutoStart ? "true" : "false")}/>

    <key>ProcessType</key>
    <string>Standard</string>

    <key>EnvironmentVariables</key>
    <dict>
        {string.Join("\n        ", environment)}
    </dict>
</dict>
</plist>
";
    }

    /// <summary>
    /// Runs a command with an explicit argument vector. Using ArgumentList rather than a single
    /// Arguments string matters: .NET parses that string with Windows CRT quoting rules even on
    /// macOS, so any embedded quote is silently eaten before the target process sees it.
    /// </summary>
    private static async Task<(bool success, string output)> RunCommandAsync(string command, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process == null)
                return (false, $"Failed to start process '{command}'");

            string output = await process.StandardOutput.ReadToEndAsync();
            string error  = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return process.ExitCode == 0 ? (true, output) : (false, error.Length > 0 ? error : output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Runs a command that needs root, via <c>sudo -n</c> when this process is not already root.
    /// </summary>
    /// <remarks>
    /// <c>-n</c> is essential: stdio is redirected and there is no TTY, so an interactive sudo
    /// password prompt would block forever instead of asking. Failing fast lets the caller tell
    /// the user to re-run under sudo.
    /// </remarks>
    private static async Task<(bool success, string output)> RunPrivilegedAsync(string command, params string[] arguments)
    {
        if (IsRoot)
            return await RunCommandAsync(command, arguments);

        var sudoArgs = new List<string> { "-n", command };
        sudoArgs.AddRange(arguments);
        var result = await RunCommandAsync("sudo", sudoArgs.ToArray());
        if (!result.success && result.output.Contains("password", StringComparison.OrdinalIgnoreCase))
            return (false, $"{result.output.Trim()}\nRe-run as root: sudo agentfox --install-service");
        return result;
    }

    /// <summary>
    /// Writes the plist, streaming content into <c>tee</c> over stdin for the root-owned case.
    /// </summary>
    /// <remarks>
    /// The previous implementation interpolated the plist into
    /// <c>sh -c "echo '…' | sudo tee path"</c>. Because .NET parses ProcessStartInfo.Arguments
    /// itself, the quote-dense XML was split apart before any shell ran — the command was
    /// truncated at the DOCTYPE line and the resulting file was not valid XML, so launchd
    /// rejected it. Content on stdin removes the quoting round-trip entirely.
    /// </remarks>
    private async Task<(bool success, string error)> WriteFileAsync(string path, string content)
    {
        try
        {
            // Ensure directory exists
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir) && !IsSystemWide)
            {
                Directory.CreateDirectory(dir);
            }

            if (!IsSystemWide || IsRoot)
            {
                await File.WriteAllTextAsync(path, content);
                return (true, "");
            }

            var psi = new ProcessStartInfo
            {
                FileName = "sudo",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("tee");
            psi.ArgumentList.Add(path);

            using var process = Process.Start(psi);
            if (process == null)
                return (false, "Failed to start 'sudo tee'");

            await process.StandardInput.WriteAsync(content);
            process.StandardInput.Close();

            string error = await process.StandardError.ReadToEndAsync();
            _ = await process.StandardOutput.ReadToEndAsync();   // tee echoes the content; discard it
            await process.WaitForExitAsync();

            return process.ExitCode == 0 ? (true, "") : (false, error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
