using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentFox.MCP;

// ─────────────────────────────────────────────────────────────────────────────
// Config types  (bound from appsettings.json MCP:Servers[])
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Transport type for an MCP server.</summary>
public enum McpTransportType
{
    /// <summary>HTTP — tries Streamable HTTP first, falls back to SSE automatically.</summary>
    Http,
    /// <summary>Force legacy SSE mode.</summary>
    Sse,
    /// <summary>Launch a local process and communicate over stdin/stdout.</summary>
    Stdio
}

/// <summary>
/// Transport-specific settings for a single MCP server.
/// Only the fields relevant to the chosen <see cref="McpTransportType"/> are required.
/// </summary>
public class McpTransportConfig
{
    /// <summary>Defaults to <see cref="McpTransportType.Http"/>.</summary>
    public McpTransportType Type { get; set; } = McpTransportType.Http;

    // ── HTTP / SSE ────────────────────────────────────────────────────────────
    /// <summary>Server endpoint URL. Required for Http and Sse transports.</summary>
    public string? Url { get; set; }
    /// <summary>Additional HTTP headers sent with every request.</summary>
    public Dictionary<string, string>? Headers { get; set; }
    /// <summary>Max SSE reconnection attempts (Sse mode only, default 5).</summary>
    public int MaxReconnectionAttempts { get; set; } = 5;

    // ── Stdio ─────────────────────────────────────────────────────────────────
    // Command, Arguments, WorkingDirectory and Env values may use {workspace} for the install
    // directory. A Command containing a path separator is resolved against the install directory,
    // never the process CWD (System32 under a Windows service); a bare name ("npx") stays a PATH
    // lookup. See McpLaunch.
    /// <summary>Executable to launch. Required for Stdio transport.</summary>
    public string? Command { get; set; }
    /// <summary>Command-line arguments passed to the process.</summary>
    public string[]? Arguments { get; set; }
    /// <summary>Working directory for the launched process.</summary>
    public string? WorkingDirectory { get; set; }
    /// <summary>Environment variables merged into the process environment. Null value removes a key.</summary>
    public Dictionary<string, string?>? Env { get; set; }
    /// <summary>Graceful shutdown timeout for the process (default 5 s).</summary>
    public int ShutdownTimeoutSeconds { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 30;

    internal McpTransportConfig Copy() => (McpTransportConfig)MemberwiseClone();
}

/// <summary>
/// Configuration for a single MCP server entry, bound from <c>MCP:Servers[]</c>.
/// <para>
/// <b>New format</b> — use the nested <see cref="Transport"/> object:
/// <code>
/// { "Name": "my-server", "Transport": { "Type": "Http", "Url": "http://localhost:3000/mcp" } }
/// </code>
/// </para>
/// <para>
/// <b>Legacy format</b> — flat <c>Url</c> / <c>Headers</c> fields still work (treated as Http/AutoDetect):
/// <code>
/// { "Name": "my-server", "Url": "http://localhost:3000", "Headers": { "Authorization": "Bearer …" } }
/// </code>
/// </para>
/// </summary>
public class McpServerConfig
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Controls whether this server is used during initialisation.
    /// <c>null</c> (key absent) and <c>true</c> both mean enabled.
    /// Only an explicit <c>false</c> disables the server.
    /// </summary>
    public bool? Enabled
    {
        get; 
        set;
    } = null;

    /// <summary>Returns true unless Enabled is explicitly set to false.</summary>
    public bool IsEnabled => Enabled != false;

    /// <summary>Structured transport config (new format).</summary>
    public McpTransportConfig? Transport { get; set; }

    /// <summary>Directories created before the server starts ({workspace} allowed). A server that
    /// refuses to start when a folder it is pointed at is missing (anymd's --allow-dir) needs this.</summary>
    public string[]? CreateDirectories { get; set; }

    /// <summary>A folder the server writes to on its own ({workspace} allowed), swept of files older
    /// than <see cref="CacheRetentionDays"/> at startup and daily after. Ignored unless both are set.</summary>
    public string? CacheDirectory { get; set; }
    public int CacheRetentionDays { get; set; }

    /// <summary>True for an entry from <c>MCP:BundledServers</c>, i.e. one this release ships.
    /// Not bindable: only <see cref="McpLaunch.Compose"/> sets it.</summary>
    internal bool Bundled { get; set; }

    // ── Legacy flat fields ────────────────────────────────────────────────────
    public string? Url { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public Dictionary<string, string>? Headers { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// McpManager
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Manages one or more MCP server connections via the official
/// <c>ModelContextProtocol</c> SDK (v1.2.0+).
///
/// <list type="bullet">
/// <item>Supports Http (Streamable HTTP / AutoDetect), Sse (legacy), and Stdio transports.</item>
/// <item>Exposes all server tools as <see cref="AITool"/> instances via <see cref="GetAllTools"/>,
///       ready for direct assignment to <see cref="ChatOptions.Tools"/>.</item>
/// <item>Handles <c>notifications/tools/list_changed</c> push notifications — bumps
///       <see cref="Version"/> and raises <see cref="ToolsChanged"/> so middleware can
///       rebuild <see cref="ChatOptions.Tools"/> without polling.</item>
/// <item>Thread-safe for concurrent add/remove calls.</item>
/// </list>
/// </summary>
public sealed class McpManager : IAsyncDisposable
{
    private sealed record ServerEntry(
        McpClient Client,
        List<McpClientTool> Tools,
        McpTransportType TransportType,
        string DisplayUrl);

    private readonly ConcurrentDictionary<string, ServerEntry> _servers
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, string> _failures
        = new(StringComparer.OrdinalIgnoreCase);

    private volatile int _version;

    // ── Public surface ────────────────────────────────────────────────────────

    /// <summary>
    /// Incremented each time the aggregate tool list changes (server added/removed,
    /// tool-list-changed notification received). Used by <c>DynamicAgentMiddleware</c>
    /// to detect when to rebuild <see cref="ChatOptions.Tools"/>.
    /// </summary>
    public int Version => _version;

    /// <summary>
    /// Connected servers — keyed by name, value is (display URL, tool count).
    /// Does NOT include servers that failed to connect; see <see cref="Failures"/>.
    /// </summary>
    public IReadOnlyDictionary<string, (string DisplayUrl, int ToolCount)> Servers =>
        _servers.ToDictionary(
            kv => kv.Key,
            kv => (kv.Value.DisplayUrl, kv.Value.Tools.Count),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Servers that were configured but failed to connect, keyed by name → error message.
    /// Reported by <see cref="Doctor.Checks.McpHealthCheck"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Failures => _failures;

    /// <summary>
    /// Raised after any server addition, removal, or tool-list refresh.
    /// <c>DynamicAgentMiddleware</c> subscribes to this to trigger a
    /// <see cref="ChatOptions.Tools"/> rebuild on the next LLM call.
    /// </summary>
    public event Func<Task>? ToolsChanged;

    // ── Add / Remove ──────────────────────────────────────────────────────────

    /// <summary>
    /// Connects a server from a full <see cref="McpServerConfig"/> entry.
    /// Handles both new structured and legacy flat config automatically.
    /// Used during startup initialisation.
    /// </summary>
    public Task<bool> AddServerAsync(McpServerConfig config, CancellationToken ct = default)
    {
        // Normalise: if no Transport block, treat legacy flat Url as Http/AutoDetect
        var transport = config.Transport ?? new McpTransportConfig
        {
            Type = McpTransportType.Http,
            Url = config.Url,
            Headers = config.Headers,
            TimeoutSeconds = config.TimeoutSeconds
        };
        try
        {
            transport = McpLaunch.Prepare(config, transport, AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            _failures[config.Name] = ex.Message;
            return Task.FromResult(false);
        }
        return AddServerCoreAsync(config.Name, transport, ct);
    }

    // ── Cache retention ───────────────────────────────────────────────────────

    private Timer? _cacheSweep;

    /// <summary>
    /// Sweeps every configured <see cref="McpServerConfig.CacheDirectory"/> now and once a day
    /// after, for the life of the manager. A server's cache is data this host causes to be written,
    /// so it gets an expiry and something that enforces it; a restart-only sweep would let a host
    /// that runs for weeks grow it without bound.
    /// </summary>
    public void StartCacheSweeps(IEnumerable<McpServerConfig> servers, Action<string, int>? onSwept = null)
    {
        var caches = servers
            .Where(s => !string.IsNullOrWhiteSpace(s.CacheDirectory) && s.CacheRetentionDays > 0)
            .Select(s => (s.Name,
                          Dir: McpLaunch.Expand(s.CacheDirectory!, AppContext.BaseDirectory),
                          Days: s.CacheRetentionDays))
            .ToList();
        if (caches.Count == 0) return;

        _cacheSweep?.Dispose();
        _cacheSweep = new Timer(_ =>
        {
            foreach (var (name, dir, days) in caches)
            {
                var deleted = McpLaunch.SweepCache(dir, days, DateTime.UtcNow);
                if (deleted > 0) onSwept?.Invoke(name, deleted);
            }
        }, null, TimeSpan.Zero, TimeSpan.FromDays(1));
    }

    /// <summary>
    /// Connects a remote HTTP or SSE server at runtime (used by <c>ManageMCPTool</c>).
    /// Defaults to Http/AutoDetect when <paramref name="transportType"/> is omitted.
    /// </summary>
    public Task<bool> AddServerAsync(
        string name,
        string url,
        McpTransportType transportType = McpTransportType.Http,
        Dictionary<string, string>? headers = null,
        CancellationToken ct = default)
        => AddServerCoreAsync(name, new McpTransportConfig
        {
            Type = transportType,
            Url = url,
            Headers = headers
        }, ct);

    /// <summary>Disconnects a server and removes all its tools.</summary>
    public async Task RemoveServerAsync(string name, CancellationToken ct = default)
    {
        if (_servers.TryRemove(name, out var entry))
        {
            await entry.Client.DisposeAsync();
            _failures.TryRemove(name, out _);
            BumpVersion();
            await RaiseToolsChanged();
        }
    }

    // ── Tool access ───────────────────────────────────────────────────────────

    /// <summary>
    /// Aggregates <see cref="McpClientTool"/> instances from all connected servers
    /// as <see cref="AITool"/>s, ready for direct use in <see cref="ChatOptions.Tools"/>.
    /// No wrapping or ToolRegistry registration needed.
    /// </summary>
    public List<AITool> GetAllTools()
        => _servers.Values.SelectMany(e => e.Tools).Cast<AITool>().ToList();

    /// <summary>
    /// Per-server summary used by <c>MCPServerContributor</c> (prompt injection)
    /// and <c>McpHealthCheck</c>.
    /// </summary>
    public List<(string Name, int ToolCount, IReadOnlyList<string> ToolNames)> GetConnectedServers()
        => _servers.Select(kv => (
                kv.Key,
                kv.Value.Tools.Count,
                (IReadOnlyList<string>)kv.Value.Tools.Select(t => t.Name).ToList()))
            .ToList();

    // ── Core implementation ───────────────────────────────────────────────────

    private async Task<bool> AddServerCoreAsync(
        string name, McpTransportConfig config, CancellationToken ct)
    {
        try
        {
            IClientTransport transport = CreateTransport(config);
            var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            var tools = (await client.ListToolsAsync(cancellationToken: ct)).ToList();

            var displayUrl = config.Type == McpTransportType.Stdio
                ? config.Command ?? "(stdio)"
                : config.Url ?? "(unknown)";

            // Subscribe to server-pushed tool-list change notifications
            client.RegisterNotificationHandler(
                NotificationMethods.ToolListChangedNotification,
                async (_, innerCt) =>
                {
                    var updated = (await client.ListToolsAsync(cancellationToken: innerCt)).ToList();
                    _servers[name] = new ServerEntry(client, updated, config.Type, displayUrl);
                    BumpVersion();
                    await RaiseToolsChanged();
                });

            _servers[name] = new ServerEntry(client, tools, config.Type, displayUrl);
            _failures.TryRemove(name, out _);
            BumpVersion();
            await RaiseToolsChanged();
            return true;
        }
        catch (Exception ex)
        {
            _failures[name] = ex.Message;
            return false;
        }
    }

    private static IClientTransport CreateTransport(McpTransportConfig config) =>
        config.Type switch
        {
            McpTransportType.Stdio => new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = config.Command
                                   ?? throw new InvalidOperationException(
                                       "Stdio transport requires 'Command' to be set"),
                Arguments = config.Arguments,
                WorkingDirectory = config.WorkingDirectory,
                EnvironmentVariables = config.Env,
                ShutdownTimeout = TimeSpan.FromSeconds(
                    config.ShutdownTimeoutSeconds > 0 ? config.ShutdownTimeoutSeconds : 5)
            }),

            McpTransportType.Sse => new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(config.Url
                                        ?? throw new InvalidOperationException(
                                            "SSE transport requires 'Url' to be set")),
                TransportMode = HttpTransportMode.Sse,
                AdditionalHeaders = config.Headers,
                MaxReconnectionAttempts = config.MaxReconnectionAttempts,
                //ConnectionTimeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            }),

            // Http (default) — AutoDetect tries Streamable HTTP first, falls back to SSE
            _ => new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(config.Url
                                    ?? throw new InvalidOperationException(
                                        "Http transport requires 'Url' to be set")),
                TransportMode = HttpTransportMode.AutoDetect,
                AdditionalHeaders = config.Headers,
                //ConnectionTimeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            })
        };

    private void BumpVersion() => Interlocked.Increment(ref _version);

    private async Task RaiseToolsChanged()
    {
        if (ToolsChanged is { } handler)
            await handler.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _cacheSweep?.Dispose();
        foreach (var entry in _servers.Values)
            await entry.Client.DisposeAsync();
        _servers.Clear();
        _failures.Clear();
    }
}
