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

    // {workspace} resolves to the install directory, NOT the current directory: the CWD belongs
    // to whoever happened to run the installer and is meaningless to a boot-time service.
    private string LogPath => _config.LogPath
        .Replace("{workspace}", ServiceLauncher.InstallDirectory);

    private static bool IsRoot => ServiceLauncher.IsRootUser;

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

            // The log directory has to exist before systemd opens the append: targets, or the
            // unit fails to start with a permission/ENOENT error on StandardOutput.
            string? logDir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(logDir))
                await RunPrivilegedAsync("mkdir", "-p", logDir);

            // Write to /etc/systemd/system (requires root)
            var writeResult = await WriteFileAsync(SystemdUnitPath, unitFile);
            if (!writeResult.success)
            {
                return new ServiceResult(false,
                    $"Failed to create systemd unit file at '{SystemdUnitPath}'",
                    $"Error: {writeResult.error}\n\nNote: Service installation requires root/sudo privileges.\n" +
                    "Run: sudo agentfox --install-service");
            }

            // Set file permissions
            await RunPrivilegedAsync("chmod", "644", SystemdUnitPath);

            // Reload systemd daemon
            var reloadResult = await RunPrivilegedAsync("systemctl", "daemon-reload");
            if (!reloadResult.success)
            {
                return new ServiceResult(false,
                    $"Failed to reload systemd daemon",
                    reloadResult.output);
            }

            // Enable service (auto-start on boot)
            if (_config.AutoStart)
            {
                var enableResult = await RunPrivilegedAsync("systemctl", "enable", _config.ServiceName);
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
            await RunPrivilegedAsync("systemctl", "stop", _config.ServiceName);

            // Disable auto-start
            await RunPrivilegedAsync("systemctl", "disable", _config.ServiceName);

            // Remove unit file
            var removeResult = await RunPrivilegedAsync("rm", "-f", SystemdUnitPath);
            if (!removeResult.success && File.Exists(SystemdUnitPath))
            {
                return new ServiceResult(false,
                    $"Failed to remove systemd unit file",
                    removeResult.output);
            }

            // Reload systemd daemon
            await RunPrivilegedAsync("systemctl", "daemon-reload");

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

            var result = await RunPrivilegedAsync("systemctl", "start", _config.ServiceName);
            if (!result.success)
            {
                return new ServiceResult(false, 
                    $"Failed to start service",
                    result.output);
            }

            // Verify it's running
            await Task.Delay(1000);
            var statusResult = await RunCommandAsync("systemctl", "is-active", _config.ServiceName);
            
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

            var result = await RunPrivilegedAsync("systemctl", "stop", _config.ServiceName);
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

            var result = await RunPrivilegedAsync("systemctl", "restart", _config.ServiceName);
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

            var result = await RunCommandAsync("systemctl", "status", _config.ServiceName);
            
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

            // Also check via systemctl. NOTE: no shell is involved (UseShellExecute = false), so
            // this must not contain a pipe — the old "list-units --all | grep X" was handed to
            // systemctl as literal arguments and always failed.
            var result = await RunCommandAsync("systemctl", "list-unit-files", $"{_config.ServiceName}.service");
            return result.success && result.output.Contains(_config.ServiceName);
        }
        catch
        {
            return false;
        }
    }

    private string GenerateSystemdUnitFile()
    {
        // ExecStart is built from the resolved launcher, not a hardcoded /usr/bin/dotnet: the
        // installer provisions .NET into ~/.dotnet, so that path usually does not exist, and a
        // framework-dependent publish ships an apphost that should be preferred anyway.
        string execStart = string.Join(' ',
            ServiceLauncher.BuildProgramArguments().Select(ServiceLauncher.QuoteForSystemd));

        // RunAsAdmin=false previously emitted User=agentfox — an account nothing ever creates,
        // which made the unit fail to start. Fall back to the invoking user instead.
        string user = _config.RunAsAdmin
            ? "root"
            : (Environment.GetEnvironmentVariable("SUDO_USER")
               ?? Environment.GetEnvironmentVariable("USER")
               ?? Environment.UserName);

        // Every assignment goes through QuoteForSystemd: a user-supplied value containing a quote
        // or backslash would otherwise terminate the Environment= directive early and leave an
        // unparseable unit behind.
        var environment = new List<string>
        {
            "Environment=" + ServiceLauncher.QuoteForSystemd("DOTNET_ENVIRONMENT=Production"),
            "Environment=" + ServiceLauncher.QuoteForSystemd($"ASPNETCORE_URLS=http://localhost:{_config.Port}"),
        };
        environment.AddRange(_config.EnvironmentVariables.Select(kvp =>
            "Environment=" + ServiceLauncher.QuoteForSystemd($"{kvp.Key}={kvp.Value}")));

        // A systemd directive is a single line; a newline in the display name would corrupt the unit.
        string description = _config.GetEffectiveDisplayName().ReplaceLineEndings(" ");

        return $@"[Unit]
Description={description}
After=network.target

[Service]
Type=simple
User={user}
WorkingDirectory={ServiceLauncher.InstallDirectory}
ExecStart={execStart}
Restart=always
RestartSec=10
StandardOutput=append:{LogPath}
StandardError=append:{LogPath}
SyslogIdentifier={_config.ServiceName}
{string.Join("\n", environment)}

[Install]
WantedBy=multi-user.target
";
    }

    /// <summary>
    /// Runs a command with an explicit argument vector. Using ArgumentList rather than a single
    /// Arguments string matters: .NET parses that string with Windows CRT quoting rules even on
    /// Unix, so any embedded quote is silently eaten before the target process sees it.
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
    /// Runs a command that needs root, re-invoking it under <c>sudo -n</c> when this process is
    /// not root. Installing previously did no elevation at all, so writing the unit file into
    /// /etc/systemd/system simply failed for every non-root caller.
    /// </summary>
    private static async Task<(bool success, string output)> RunPrivilegedAsync(string command, params string[] arguments)
    {
        if (IsRoot)
            return await RunCommandAsync(command, arguments);

        // -n: never prompt. There is no TTY here (stdio is redirected), so an interactive sudo
        // password prompt would hang rather than ask. Fail fast with a message the caller can act on.
        var sudoArgs = new List<string> { "-n", command };
        sudoArgs.AddRange(arguments);
        var result = await RunCommandAsync("sudo", sudoArgs.ToArray());
        if (!result.success && result.output.Contains("password", StringComparison.OrdinalIgnoreCase))
            return (false, $"{result.output.Trim()}\nRe-run as root: sudo agentfox --install-service");
        return result;
    }

    /// <summary>
    /// Writes a root-owned file by streaming the content into <c>tee</c> over stdin.
    /// </summary>
    /// <remarks>
    /// The previous implementation interpolated the file content into
    /// <c>sh -c "echo '…' | tee path"</c>. Because .NET parses ProcessStartInfo.Arguments itself,
    /// every double quote in the unit file was consumed as a grouping character before the shell
    /// ran — the generated unit reached disk with its ExecStart quoting stripped. Passing content
    /// on stdin removes the quoting round-trip completely.
    /// </remarks>
    private static async Task<(bool success, string error)> WriteFileAsync(string path, string content)
    {
        try
        {
            if (IsRoot)
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
