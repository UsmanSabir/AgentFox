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

            string binPath      = BuildServiceBinPath();
            string startType    = _config.AutoStart ? "auto" : "demand";
            bool isAdministrator = IsAdministrator();

            // Attempt 1 — direct sc.exe. Only succeeds when this process is already elevated.
            var result = await RunCommandAsync("sc.exe", BuildCreateArguments(binPath, startType));
            var attempts = new List<string>();
            if (!result.success)
                attempts.Add($"sc.exe (current process): {result.output.Trim()}");

            // Attempt 2 — explicit credentials, when the caller supplied them.
            //
            // NOTE: this cannot defeat UAC. Process.Start with UserName/Password goes through
            // CreateProcessWithLogonW, which hands back the *filtered* (standard-user) token for
            // any account subject to UAC — including members of the Administrators group. So this
            // path only works for UAC-exempt accounts (the built-in Administrator, or a machine
            // with UAC disabled). It must therefore never be the last resort: fall through to the
            // consent-prompt elevation below when it fails.
            if (!result.success && !string.IsNullOrWhiteSpace(_config.InstallUserName))
            {
                result = await RunCommandWithCredentialsAsync("sc.exe",
                    BuildCreateArguments(binPath, startType),
                    _config.InstallUserName!, _config.InstallPassword ?? string.Empty, _config.InstallDomain);
                if (!result.success)
                    attempts.Add($"sc.exe as '{_config.InstallUserName}': {result.output.Trim()}");
            }

            // Attempt 3 — re-run the whole install under a UAC consent prompt. This is the only
            // path that actually elevates from a non-elevated process. It sets the description
            // and recovery actions itself, while it still holds the elevated token.
            bool metadataApplied = false;
            if (!result.success && !isAdministrator)
            {
                result = await RunElevatedInstallAsync(binPath);
                metadataApplied = result.success;
                if (!result.success)
                    attempts.Add($"elevated install: {result.output.Trim()}");
            }

            // sc.exe/New-Service exit codes are not fully trustworthy through the elevation
            // hop, so the registry is the source of truth for whether the install happened.
            if (!await ServiceExistsAsync())
            {
                var details = string.Join(Environment.NewLine, attempts);
                if (!isAdministrator)
                    details += $"{Environment.NewLine}{Environment.NewLine}Tip: run this from an Administrator terminal (agentfox --install-service) and accept the UAC prompt.";
                return new ServiceResult(false,
                    $"Failed to install service '{_config.ServiceName}'", details);
            }

            _logger?.LogInformation($"Service '{_config.ServiceName}' installed successfully.");

            if (!metadataApplied)
                await ApplyServiceMetadataAsync();

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

            // Deleting requires elevation. From a non-elevated process, retry stop+delete in a
            // single elevated script so the user sees one consent prompt rather than two.
            if (!result.success && !IsAdministrator())
                result = await RunElevatedUninstallAsync();

            if (!result.success && await ServiceExistsAsync())
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

            var result = await RunScAsync($"start \"{_config.ServiceName}\"");
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

            var result = await RunScAsync($"stop \"{_config.ServiceName}\"");
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

    /// <summary>
    /// The ImagePath registered with the SCM: the quoted executable followed by its arguments.
    /// </summary>
    /// <remarks>
    /// Two things here are load-bearing:
    /// <list type="bullet">
    /// <item>The executable path is quoted. An unquoted ImagePath containing a space (say
    /// <c>C:\Users\Jane Doe\.agentfox\AgentFox.exe</c>) makes the SCM resolve the wrong image and
    /// the service fails to start with "the system cannot find the file specified".</item>
    /// <item>Every switch uses the <c>--key=value</c> form. The configuration command-line
    /// provider reads a bare <c>--key</c> as a key whose value is the NEXT token, so
    /// <c>--service-mode --modules web</c> parsed as <c>service-mode=--modules</c> and dropped
    /// <c>web</c> entirely — the service then started every module, including the interactive CLI.</item>
    /// <item>Modules are opted OUT, not in. The old <c>--modules web</c> was an opt-in allow-list
    /// that would have disabled every plugin module too; disabling only <c>cli</c> leaves the web
    /// API and all plugins running, which is what a background service is for.</item>
    /// </list>
    /// The listening port is deliberately NOT baked in here — it comes from <c>Services.Port</c>
    /// in the configuration file, so changing the port does not require re-registering the service.
    /// </remarks>
    private string BuildServiceBinPath()
        => $"{GetQuotedLauncher()} --service-mode=true --DisabledModules=cli";

    /// <summary>
    /// sc.exe arguments for <c>create</c>. <paramref name="binPath"/> carries its own inner
    /// quotes, so it is escaped for the C runtime's command-line parser (<c>"</c> → <c>\"</c>);
    /// wrapping it in plain quotes would let the parser strip the inner pair and the executable
    /// path would lose its quoting.
    /// </summary>
    private string BuildCreateArguments(string binPath, string startType)
        => $"create \"{_config.ServiceName}\" binPath= {QuoteArgument(binPath)} " +
           $"start= {startType} DisplayName= {QuoteArgument(_config.GetEffectiveDisplayName())}";

    /// <summary>Quotes a value for a Windows command line, escaping embedded quotes and trailing backslashes.</summary>
    private static string QuoteArgument(string value)
    {
        var sb = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); }
            else          { sb.Append('\\', backslashes).Append(c); }
            backslashes = 0;
        }
        // Backslashes immediately before the closing quote must be doubled.
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>Description + auto-recovery. Applied after the service row exists.</summary>
    private async Task ApplyServiceMetadataAsync()
    {
        await RunCommandAsync("sc.exe",
            $"description \"{_config.ServiceName}\" {QuoteArgument(_config.GetEffectiveDescription())}");

        // Auto-recovery: restart on failure — 3 attempts, 5-second delay each
        await RunCommandAsync("sc.exe",
            $"failure \"{_config.ServiceName}\" reset= 0 actions= restart/5000/restart/5000/restart/5000");

        // Trigger recovery on non-zero exit codes, not only on crashes
        await RunCommandAsync("sc.exe", $"failureflag \"{_config.ServiceName}\" 1");
    }

    /// <summary>
    /// The launcher the SCM must run, already quoted: the apphost when one was published, else
    /// <c>dotnet "…\AgentFox.dll"</c>. A bare .dll path is NOT launchable by the SCM, so the
    /// framework-dependent case has to name the dotnet host explicitly.
    /// </summary>
    private static string GetQuotedLauncher()
    {
        // Environment.ProcessPath is the real running image and survives single-file publish,
        // where Assembly.Location is empty.
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) &&
            !Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{processPath}\"";
        }

        string? assemblyPath = Path.GetDirectoryName(typeof(Program).Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyPath))
        {
            string exePath = Path.Combine(assemblyPath, "AgentFox.exe");
            if (File.Exists(exePath))
                return $"\"{exePath}\"";

            string dllPath = Path.Combine(assemblyPath, "AgentFox.dll");
            if (File.Exists(dllPath))
            {
                // Running via `dotnet AgentFox.dll` — the service needs the host plus the dll.
                string host = !string.IsNullOrEmpty(processPath) ? processPath : "dotnet";
                return $"\"{host}\" \"{dllPath}\"";
            }
        }

        throw new InvalidOperationException(
            "Cannot determine the AgentFox executable path to register with the service manager.");
    }

    private string GetExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) &&
            !Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return processPath;

        string? assemblyPath = Path.GetDirectoryName(typeof(Program).Assembly.Location);
        if (string.IsNullOrEmpty(assemblyPath))
            throw new InvalidOperationException("Cannot determine executable path");

        string exePath = Path.Combine(assemblyPath, "AgentFox.exe");
        return File.Exists(exePath) ? exePath : Path.Combine(assemblyPath, "AgentFox.dll");
    }

    /// <summary>
    /// Runs an sc.exe verb, retrying through a UAC consent prompt when the current process lacks
    /// the rights. start/stop/delete all require elevation, so without this a non-elevated
    /// <c>agentfox --start-service</c> just reports "Access is denied".
    /// </summary>
    private async Task<(bool success, string output)> RunScAsync(string arguments)
    {
        var result = await RunCommandAsync("sc.exe", arguments);
        if (result.success || IsAdministrator())
            return result;

        var elevated = await RunElevatedAsync("sc.exe", arguments);
        return elevated.success
            ? elevated
            : (false, $"{result.output.Trim()}{Environment.NewLine}{elevated.output.Trim()}");
    }

    /// <summary>Launches a command through a UAC consent prompt. Output cannot be captured.</summary>
    private static async Task<(bool success, string output)> RunElevatedAsync(string command, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = command,
                Arguments       = arguments,
                UseShellExecute = true,   // required for Verb = runas
                Verb            = "runas",
                WindowStyle     = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(psi);
            if (process == null) return (false, $"Failed to start '{command}' elevated.");

            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? (true, string.Empty)
                : (false, $"Elevated '{command} {arguments}' exited with code {process.ExitCode}.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "The Administrator consent prompt was declined.");
        }
        catch (Exception ex)
        {
            return (false, $"Could not run '{command}' elevated: {ex.Message}");
        }
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

    /// <summary>
    /// Creates the service from an elevated PowerShell instance launched through a UAC consent
    /// prompt. This is the only route that actually elevates a non-elevated process.
    /// </summary>
    /// <remarks>
    /// The script uses <c>New-Service</c> rather than <c>sc.exe create</c> on purpose: the
    /// binPath contains quotes around the executable, and passing that through PowerShell's
    /// native-argument binder strips them (verified: <c>binPath= "C:\…\AgentFox.exe --service-mode"</c>
    /// arrives with the inner quotes gone). Cmdlet parameters are bound directly and are not
    /// re-parsed, so the value survives intact.
    ///
    /// The script also writes its own transcript and exits with a real code, because
    /// <c>powershell.exe -File</c> otherwise exits 0 even when every command inside failed —
    /// which previously made a failed install report success.
    /// </remarks>
    private Task<(bool success, string output)> RunElevatedInstallAsync(string binPath)
    {
        // New-Service spells the start type differently from sc.exe ("auto" / "demand").
        string startupType = _config.AutoStart ? "Automatic" : "Manual";

        return RunElevatedScriptAsync("install", $$"""
            New-Service -Name {{PsLiteral(_config.ServiceName)}} `
                        -BinaryPathName {{PsLiteral(binPath)}} `
                        -DisplayName {{PsLiteral(_config.GetEffectiveDisplayName())}} `
                        -Description {{PsLiteral(_config.GetEffectiveDescription())}} `
                        -StartupType {{startupType}} | Out-Null

            # Auto-recovery: restart up to 3x with a 5 s delay, and treat a non-zero exit
            # code as a failure (not just a crash).
            & sc.exe failure {{PsLiteral(_config.ServiceName)}} reset= 0 actions= restart/5000/restart/5000/restart/5000 | Out-Null
            & sc.exe failureflag {{PsLiteral(_config.ServiceName)}} 1 | Out-Null
            """);
    }

    /// <summary>Stop + delete in ONE elevated script, so the user sees a single UAC prompt.</summary>
    private Task<(bool success, string output)> RunElevatedUninstallAsync()
        => RunElevatedScriptAsync("uninstall", $$"""
            $name = {{PsLiteral(_config.ServiceName)}}
            & sc.exe stop $name | Out-Null
            Start-Sleep -Seconds 2
            & sc.exe delete $name | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE" }
            """);

    /// <summary>Single-quoted PowerShell literal; embedded single quotes are doubled.</summary>
    private static string PsLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// Writes <paramref name="body"/> to a temp .ps1 and runs it through a UAC consent prompt,
    /// returning the transcript so a failure can be reported instead of guessed at.
    /// </summary>
    private static async Task<(bool success, string output)> RunElevatedScriptAsync(string label, string body)
    {
        string stamp      = Guid.NewGuid().ToString("N");
        string tempScript = Path.Combine(Path.GetTempPath(), $"AgentFoxService_{label}_{stamp}.ps1");
        string logFile    = Path.Combine(Path.GetTempPath(), $"AgentFoxService_{label}_{stamp}.log");

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Start-Transcript -Path {{PsLiteral(logFile)}} -Force | Out-Null
            try {
            {{body}}
                Stop-Transcript | Out-Null
                exit 0
            }
            catch {
                Write-Output $_.Exception.Message
                Stop-Transcript | Out-Null
                exit 1
            }
            """;
        await File.WriteAllTextAsync(tempScript, script);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\"",
                UseShellExecute = true,   // required for Verb = runas
                Verb            = "runas",
                WindowStyle     = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (false, $"Failed to start the elevated {label} process.");

            await process.WaitForExitAsync();

            string log = File.Exists(logFile) ? (await File.ReadAllTextAsync(logFile)).Trim() : string.Empty;
            return process.ExitCode == 0
                ? (true, log)
                : (false, log.Length > 0 ? log : $"Elevated {label} exited with code {process.ExitCode}.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — the user dismissed the UAC prompt. Say so plainly instead of
            // silently retrying under cmd.exe and prompting a second time.
            return (false, "The Administrator consent prompt was declined.");
        }
        catch (Exception ex)
        {
            return (false, $"Could not run the elevated {label}: {ex.Message}");
        }
        finally
        {
            try { File.Delete(tempScript); } catch { /* best effort */ }
            try { File.Delete(logFile);    } catch { /* best effort */ }
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
