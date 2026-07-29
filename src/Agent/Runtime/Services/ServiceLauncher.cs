using System.Runtime.InteropServices;

namespace AgentFox.Runtime.Services;

/// <summary>
/// Resolves the command a service manager must register to launch AgentFox in the background,
/// and the arguments that put it into service mode.
/// </summary>
/// <remarks>
/// The unit/plist generators used to hardcode <c>/usr/bin/dotnet</c>. That path is wrong for the
/// way AgentFox actually installs itself: install.sh provisions .NET into <c>$HOME/.dotnet</c>,
/// and on macOS SIP prevents anything from living in <c>/usr/bin</c> at all (the real host is
/// <c>/usr/local/share/dotnet/dotnet</c>). A framework-dependent publish also produces a native
/// apphost next to the dll, which should be preferred over invoking the host by hand.
/// </remarks>
public static class ServiceLauncher
{
    /// <summary>
    /// Switches that put the process into background-service mode.
    /// </summary>
    /// <remarks>
    /// Both use the <c>--key=value</c> form. The configuration command-line provider reads a bare
    /// <c>--key</c> as a key whose value is the NEXT token, so the previous
    /// <c>--service-mode --modules web</c> parsed as <c>service-mode=--modules</c> and discarded
    /// <c>web</c> outright — every module then loaded, including the interactive CLI.
    ///
    /// Modules are opted OUT rather than in: an allow-list of <c>web</c> would also disable every
    /// plugin module, while disabling <c>cli</c> alone leaves the web API and plugins running.
    /// The port is intentionally absent — it comes from <c>Services.Port</c> in configuration, so
    /// changing it does not require re-registering the service.
    /// </remarks>
    public static readonly string[] ServiceArguments = ["--service-mode=true", "--DisabledModules=cli"];

    /// <summary>The directory AgentFox is installed in.</summary>
    public static string InstallDirectory => AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// The full argv a service manager should execute: either the apphost alone, or the dotnet
    /// host followed by AgentFox.dll, plus <see cref="ServiceArguments"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Neither an apphost nor a resolvable dotnet host was found.</exception>
    public static IReadOnlyList<string> BuildProgramArguments()
    {
        var argv = new List<string>();

        string? appHost = FindAppHost();
        if (appHost != null)
        {
            argv.Add(appHost);
        }
        else
        {
            string dll = Path.Combine(InstallDirectory, "AgentFox.dll");
            if (!File.Exists(dll))
                throw new InvalidOperationException(
                    $"Neither an AgentFox executable nor AgentFox.dll was found in '{InstallDirectory}'.");

            string host = FindDotnetHost()
                ?? throw new InvalidOperationException(
                    "Could not locate the 'dotnet' host needed to run AgentFox.dll. Install the .NET 10 " +
                    "runtime, or set DOTNET_ROOT, then re-run the service install.");

            argv.Add(host);
            argv.Add(dll);
        }

        argv.AddRange(ServiceArguments);
        return argv;
    }

    /// <summary>The published native apphost (AgentFox / AgentFox.exe), if one exists.</summary>
    private static string? FindAppHost()
    {
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string hostName = windows ? "AgentFox.exe" : "AgentFox";

        // Environment.ProcessPath is the real running image and survives single-file publish,
        // where Assembly.Location is empty. It is the dotnet host itself under `dotnet X.dll`.
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) && !IsDotnetHost(processPath) && File.Exists(processPath))
            return processPath;

        string candidate = Path.Combine(InstallDirectory, hostName);
        return File.Exists(candidate) ? candidate : null;
    }

    private static bool IsDotnetHost(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Locates the dotnet host, preferring the one currently running this process, then the
    /// documented environment/layout locations, then PATH.
    /// </summary>
    private static string? FindDotnetHost()
    {
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string exe = windows ? "dotnet.exe" : "dotnet";

        // 1. This process, when started as `dotnet AgentFox.dll`.
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) && IsDotnetHost(processPath) && File.Exists(processPath))
            return processPath;

        var candidates = new List<string>();

        // 2. DOTNET_ROOT — set by install.sh / install.ps1 when they provision .NET themselves.
        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
            candidates.Add(Path.Combine(dotnetRoot, exe));

        // 3. The private install location both installers use.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            candidates.Add(Path.Combine(home, ".dotnet", exe));

        // 4. Platform defaults.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.Add("/usr/local/share/dotnet/dotnet");
            candidates.Add("/opt/homebrew/bin/dotnet");
            candidates.Add("/usr/local/bin/dotnet");
        }
        else if (!windows)
        {
            candidates.Add("/usr/share/dotnet/dotnet");
            candidates.Add("/usr/lib/dotnet/dotnet");
            candidates.Add("/usr/bin/dotnet");
            candidates.Add("/usr/local/bin/dotnet");
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        // 5. PATH.
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVar))
        {
            char separator = windows ? ';' : ':';
            foreach (var dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* malformed PATH entry */ }
            }
        }

        return null;
    }

    /// <summary>True when this process is running as root (uid 0).</summary>
    /// <remarks>
    /// The environment check comes first so the common case never touches libc. The P/Invoke is
    /// wrapped because a failure to resolve it must not abort a service install — falling back to
    /// "not root" only means the caller routes through sudo, which is the safe direction.
    /// </remarks>
    public static bool IsRootUser
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
            if (Environment.GetEnvironmentVariable("USER") == "root") return true;
            try { return geteuid() == 0; }
            catch { return false; }
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();

    /// <summary>Quotes a value for embedding in a systemd ExecStart line.</summary>
    public static string QuoteForSystemd(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Escapes a value for use as XML character data in a plist.</summary>
    public static string EscapeXml(string value)
        => value.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
}
