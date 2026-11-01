using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentFox.Plugins;
using ModelContextProtocol.Authentication;

namespace AgentFox.MCP;

/// <summary>
/// OAuth sign-in for one HTTP MCP server, as the MCP authorization spec defines it (OAuth 2.1 with PKCE, dynamic
/// client registration). On by default for every HTTP server: it engages only when the server answers 401 with OAuth
/// metadata — a hosted Stockji agent does — and the SDK discovers the rest from that answer. A server that must never
/// be offered a sign-in sets <c>"OAuth": { "Enabled": false }</c>.
/// </summary>
public class McpOAuthConfig
{
    public const int DefaultRedirectPort = 33418;

    public bool Enabled { get; set; } = true;

    /// <summary>A client id issued in advance. Left empty, AgentFox registers itself (RFC 7591).</summary>
    public string? ClientId { get; set; }

    /// <summary>The name the sign-in page shows. Defaults to "AgentFox".</summary>
    public string? ClientName { get; set; }

    /// <summary>Scopes to ask for when the server advertises none.</summary>
    public string[]? Scopes { get; set; }

    /// <summary>The local port the browser comes back to. It is part of the client's registration, so it stays fixed;
    /// change it only if something else on this machine holds it.</summary>
    public int RedirectPort { get; set; } = DefaultRedirectPort;

    internal Uri RedirectUri => new($"http://127.0.0.1:{RedirectPort}/callback");
}

/// <summary>Raised instead of opening a browser when nobody asked to sign in — at startup, or when a stored sign-in
/// is no longer accepted.</summary>
internal sealed class McpSignInRequiredException(string server)
    : Exception($"Not signed in. Ask AgentFox to sign in to '{server}' (manage_mcp_server, action sign_in).");

/// <summary>
/// The tokens each OAuth server issued: one encrypted file per server under <c>{workspace}/mcp-auth</c>, so a restart
/// refreshes quietly instead of asking the user to sign in again.
/// <para>
/// Retention: a file is deleted when its server is removed or signed out, when the server is no longer configured,
/// and <see cref="RetentionDays"/> after it was last written (a refresh rewrites it) — past the longest session a
/// server is known to honour. Swept at startup and daily with the MCP caches (<see cref="McpManager.StartCacheSweeps"/>).
/// </para>
/// </summary>
internal sealed class McpTokenStore
{
    public const int RetentionDays = 180;

    private readonly string _directory;
    private readonly IPluginSecretProtector _protector;
    private readonly object _write = new();

    public McpTokenStore(string directory, IPluginSecretProtector protector)
    {
        _directory = directory;
        _protector = protector;
    }

    public ITokenCache CacheFor(string server, string url) => new FileTokenCache(this, server, url);

    public bool Has(string server, string url) => Read(server, url) is not null;

    public void Delete(string server)
    {
        try { File.Delete(PathFor(server)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Deletes every file not belonging to a configured server, and any older than the retention.
    /// Returns how many were deleted.</summary>
    public int Sweep(IEnumerable<string> configured, DateTime nowUtc)
    {
        if (!Directory.Exists(_directory)) return 0;
        var keep = configured.Select(FileName).ToHashSet(StringComparer.Ordinal);
        var cutoff = nowUtc.AddDays(-RetentionDays);
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                if (keep.Contains(Path.GetFileNameWithoutExtension(file)) && File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                File.Delete(file);
                deleted++;
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }

    /// <summary>The stored tokens, or null when there are none, they cannot be decrypted, or they were issued for a
    /// different address (a server entry re-pointed elsewhere must not send another server's token).</summary>
    internal TokenContainer? Read(string server, string url)
    {
        try
        {
            var path = PathFor(server);
            if (!File.Exists(path) || !_protector.TryUnprotect(File.ReadAllText(path), out var json)) return null;
            var record = JsonSerializer.Deserialize<Record>(json);
            return record?.Url == url ? record.Tokens : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal void Write(string server, string url, TokenContainer tokens)
    {
        lock (_write)
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(server);
            var temp = path + ".tmp";
            File.WriteAllText(temp, _protector.Protect(JsonSerializer.Serialize(new Record(url, tokens))));
            File.Move(temp, path, overwrite: true);
        }
    }

    private string PathFor(string server) => Path.Combine(_directory, FileName(server) + ".json");

    /// <summary>A readable, filesystem-safe name, with a hash so two names that clean up the same stay apart.</summary>
    internal static string FileName(string server)
    {
        var lower = server.ToLowerInvariant();
        var safe = new string(lower.Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' ? c : '_').ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lower)))[..8].ToLowerInvariant();
        return $"{(safe.Length > 40 ? safe[..40] : safe)}-{hash}";
    }

    private sealed record Record(string Url, TokenContainer Tokens);

    /// <summary>The SDK asks for the token on every request, so it is held in memory and read from disk once.</summary>
    private sealed class FileTokenCache(McpTokenStore store, string server, string url) : ITokenCache
    {
        private readonly Lazy<TokenContainer?> _loaded = new(() => store.Read(server, url));
        private volatile TokenContainer? _current;

        public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) =>
            new(_current ?? _loaded.Value);

        public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            _current = tokens;
            store.Write(server, url, tokens);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Where the browser lands after the user approves: a one-request HTTP listener on 127.0.0.1. A raw socket rather than
/// <c>HttpListener</c>, which needs a URL reservation on Windows unless the process is elevated.
/// </summary>
internal sealed class McpLoopbackCallback : IDisposable
{
    private const int MaxRequestBytes = 8192;
    private readonly TcpListener _listener;
    private readonly string _path;

    private McpLoopbackCallback(TcpListener listener, string path)
    {
        _listener = listener;
        _path = path;
    }

    /// <summary>Starts listening, or answers null with the reason when the port is taken.</summary>
    public static McpLoopbackCallback? TryListen(Uri redirectUri, out string? problem)
    {
        problem = null;
        var listener = new TcpListener(IPAddress.Loopback, redirectUri.Port);
        try
        {
            listener.Start();
            return new McpLoopbackCallback(listener, redirectUri.AbsolutePath);
        }
        catch (SocketException ex)
        {
            problem = $"port {redirectUri.Port} is in use ({ex.SocketErrorCode})";
            return null;
        }
    }

    /// <summary>Answers requests until one reaches the callback path, and returns what it carried.</summary>
    public async Task<AuthorizationResult> WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            var target = await ReadTargetAsync(stream, ct);
            if (target is null || !target.StartsWith(_path, StringComparison.Ordinal)
                || (target.Length > _path.Length && target[_path.Length] != '?'))
            {
                await RespondAsync(stream, "404 Not Found", "Not found.", ct);
                continue;
            }
            try
            {
                var result = Parse(new Uri("http://127.0.0.1" + target));
                await RespondAsync(stream, "200 OK", "Signed in. You can close this tab and go back to AgentFox.", ct);
                return result;
            }
            catch (McpSignInDeclinedException ex)
            {
                await RespondAsync(stream, "200 OK", ex.Message, ct);
                throw;
            }
        }
    }

    /// <summary>Reads the code, state and issuer from the address the authorization server sent the browser to — also
    /// what a user pastes when the browser was on another device.</summary>
    public static AuthorizationResult Parse(Uri redirect)
    {
        var query = ParseQuery(redirect.Query);
        if (query.TryGetValue("error", out var error))
            throw new McpSignInDeclinedException(query.GetValueOrDefault("error_description") ?? error);
        if (!query.TryGetValue("code", out var code) || code.Length == 0)
            throw new McpSignInDeclinedException("The address has no sign-in code. Copy the full address the browser landed on.");
        return new AuthorizationResult
        {
            Code = code,
            State = query.GetValueOrDefault("state"),
            Iss = query.GetValueOrDefault("iss")
        };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((at < 0 ? pair : pair[..at]).Replace('+', ' '));
            var value = at < 0 ? "" : Uri.UnescapeDataString(pair[(at + 1)..].Replace('+', ' '));
            values.TryAdd(key, value);
        }
        return values;
    }

    private static async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
            if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n", StringComparison.Ordinal)) break;
        }
        var line = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0].Split(' ');
        return line.Length == 3 && line[0] == "GET" ? line[1] : null;
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><meta charset=\"utf-8\"><title>AgentFox</title><p style=\"font:16px system-ui;margin:40px\">{WebUtility.HtmlEncode(message)}</p>");
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
    }

    public void Dispose() => _listener.Stop();
}

/// <summary>The user declined, or the address given back carried no code.</summary>
internal sealed class McpSignInDeclinedException(string message) : Exception(message);

internal static class McpBrowser
{
    /// <summary>Best effort: a service or a headless host has no browser, and the link is reported either way.</summary>
    public static bool TryOpen(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
