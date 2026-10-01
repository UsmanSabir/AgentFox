using System.Diagnostics;
using AgentFox.Plugins.Security;
using Microsoft.Extensions.Configuration;

namespace AgentFox.MCP;

/// <summary>
/// What has to be true of an MCP server entry before it can be launched the same way on every
/// deployment: where its executable is, which of the host's environment it inherits, and which
/// configured entries are actually in play. Kept free of the manager so the rules are testable
/// without starting a process.
/// </summary>
internal static class McpLaunch
{
    /// <summary>Stands for the install directory, as it already does in <c>Service:LogPath</c>.</summary>
    public const string WorkspaceToken = "{workspace}";

    public static string Expand(string value, string baseDirectory) =>
        value.Replace(WorkspaceToken, baseDirectory.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A bare name ("npx", "uvx") is a PATH lookup and is left alone. Anything with a path
    /// separator is a file, and a relative one is resolved against the install directory: the
    /// process CWD is System32 under a Windows service and / under systemd, so "tools/x" would
    /// otherwise point somewhere different on every host. On Windows a missing extension falls
    /// back to ".exe", so one config line serves every platform's build.
    /// </summary>
    public static string ResolveCommand(
        string command, string baseDirectory, bool isWindows, Func<string, bool> fileExists)
    {
        var expanded = Expand(command, baseDirectory);
        if (expanded.IndexOfAny(['/', '\\']) < 0) return expanded;

        var full = Path.GetFullPath(Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(baseDirectory, expanded));

        if (isWindows && !Path.HasExtension(full) && !fileExists(full) && fileExists(full + ".exe"))
            return full + ".exe";
        return full;
    }

    /// <summary>
    /// Returns the transport as it will actually be launched. HTTP/SSE entries pass through
    /// untouched. For stdio: tokens expanded, command resolved, <see cref="McpServerConfig.CreateDirectories"/>
    /// created, and — for a bundled server only — the host's secret-shaped environment variables
    /// removed from what the child inherits.
    /// </summary>
    public static McpTransportConfig Prepare(McpServerConfig server, McpTransportConfig transport, string baseDirectory)
    {
        if (transport.Type != McpTransportType.Stdio) return transport;

        var prepared = transport.Copy();
        if (transport.Command is { } command)
            prepared.Command = ResolveCommand(command, baseDirectory, OperatingSystem.IsWindows(), File.Exists);
        prepared.Arguments = transport.Arguments?.Select(a => Expand(a, baseDirectory)).ToArray();
        if (transport.WorkingDirectory is { } cwd)
            prepared.WorkingDirectory = Expand(cwd, baseDirectory);
        prepared.Env = transport.Env?.ToDictionary(
            kv => kv.Key,
            kv => kv.Value is null ? null : Expand(kv.Value, baseDirectory));

        foreach (var dir in server.CreateDirectories ?? [])
            Directory.CreateDirectory(Expand(dir, baseDirectory));

        if (server.Bundled)
            prepared.Env = WithoutInheritedSecrets(prepared.Env);

        return prepared;
    }

    /// <summary>
    /// The env overlay that makes a child start without the secret-shaped variables this process
    /// holds (provider API keys and the like), using the same rule the shell tool and code sandbox
    /// already apply. Only for servers this release ships: a document converter has no use for a
    /// model key, while a server the operator configured may genuinely read one from the
    /// environment, so stripping theirs would break it. Keys the entry sets explicitly are kept.
    /// </summary>
    public static Dictionary<string, string?> WithoutInheritedSecrets(Dictionary<string, string?>? env)
    {
        var probe = new ProcessStartInfo();
        var inherited = probe.Environment.Keys.ToList();
        SecretGuard.SanitizeChildEnvironment(probe);

        var result = env is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(env, StringComparer.OrdinalIgnoreCase);
        foreach (var key in inherited)
        {
            if (!probe.Environment.ContainsKey(key) && !result.ContainsKey(key))
                result[key] = null;
        }
        return result;
    }

    /// <summary>
    /// The servers to start: every <c>MCP:Servers[]</c> entry, plus each <c>MCP:BundledServers</c>
    /// entry whose executable is actually in this install.
    /// <para>
    /// Bundled servers are keyed by NAME, not listed, because configuration layers merge arrays by
    /// INDEX: a user's own <c>MCP:Servers[0]</c> in appsettings.user.json would overwrite a shipped
    /// entry's fields one by one and launch a hybrid of the two. Keys merge correctly, so a user
    /// disables one with <c>"BundledServers": { "anymd": { "Enabled": false } }</c>, and a
    /// <c>Servers[]</c> entry of the same name replaces it outright.
    /// </para>
    /// <para>
    /// A bundled server whose binary is absent (a dev checkout, or a platform it has no build for)
    /// is reported in <paramref name="notShipped"/> rather than started and failed: absence is the
    /// expected state there, not a fault.
    /// </para>
    /// </summary>
    public static List<McpServerConfig> Compose(
        IConfiguration configuration, string baseDirectory, Func<string, bool> fileExists,
        out List<string> notShipped)
    {
        var servers = (configuration.GetSection("MCP:Servers").Get<List<McpServerConfig>>() ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .ToList();
        notShipped = [];

        var bundled = configuration.GetSection("MCP:BundledServers").Get<Dictionary<string, McpServerConfig>>() ?? [];
        foreach (var (name, server) in bundled)
        {
            if (servers.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

            server.Name = name;
            server.Bundled = true;

            if (server.Transport is { Type: McpTransportType.Stdio, Command: { } command }
                && server.IsEnabled
                && !fileExists(ResolveCommand(command, baseDirectory, OperatingSystem.IsWindows(), fileExists)))
            {
                notShipped.Add(name);
                continue;
            }
            servers.Add(server);
        }
        return servers;
    }

    /// <summary>Deletes files under <paramref name="directory"/> last written before the cutoff.
    /// Best-effort: a file in use is left for the next sweep. Returns how many were deleted.</summary>
    public static int SweepCache(string directory, int retentionDays, DateTime nowUtc)
    {
        if (retentionDays <= 0 || !Directory.Exists(directory)) return 0;

        var cutoff = nowUtc.AddDays(-retentionDays);
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch { /* best-effort */ }
        }
        return deleted;
    }
}
