using System.Text.Json;
using AgentFox.MCP;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentFox.Tools;

/// <summary>
/// Tool that adds or removes MCP servers at runtime without restarting.
///
/// When an MCP server is added:
///   1. McpManager connects to the server using the official SDK.
///   2. MCP tools are surfaced directly in ChatOptions.Tools via DynamicAgentMiddleware.
///   3. The server config is persisted to appsettings.json so it survives restarts.
///
/// When an MCP server is removed the reverse happens.
///
/// Stdio transport is startup-only (requires a Command path); this tool supports
/// Http (AutoDetect) and Sse transports for runtime adds.
///
/// OAuth is on by default (auth = none opts out). A server that asks for a sign-in is saved at once and connects
/// when the user approves the sign-in page this tool returns; 'sign_in' starts that again for a configured server whose sign-in lapsed, and
/// 'complete_sign_in' takes the address a browser on another device landed on.
/// </summary>
public class ManageMCPTool : BaseTool
{
    private readonly McpManager _mcpManager;
    private readonly string _configFilePath;
    private readonly ILogger? _logger;

    public ManageMCPTool(McpManager mcpManager, string configFilePath, ILogger? logger = null)
    {
        _mcpManager = mcpManager;
        _configFilePath = configFilePath;
        _logger = logger;
    }

    public override string Name => "manage_mcp_server";

    public override string Description =>
        "Add or remove an external MCP server at runtime without restarting. " +
        "Changes are persisted to appsettings.json under MCP:Servers and take effect immediately. " +
        "For 'add': provide server_name, url, and optionally transport_mode and headers_json. A server that signs in " +
        "with OAuth (hosted Stockji agents do) answers with a sign-in link to give the user; auth='none' turns that off. " +
        "For 'sign_in': server_name of a configured OAuth server that needs signing in again. " +
        "For 'complete_sign_in': server_name and redirect_url, the full address the user's browser landed on after " +
        "approving, when that browser was on another device. " +
        "For 'remove': provide server_name only (an OAuth server is also signed out).";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["action"] = new()
        {
            Type        = "string",
            Description = "'add' to configure a new MCP server, 'remove' to disconnect an existing one, " +
                          "'sign_in' to sign in to a configured OAuth server, 'complete_sign_in' to finish one " +
                          "with the address the browser landed on.",
            Required    = true,
            EnumValues  = ["add", "remove", "sign_in", "complete_sign_in"]
        },
        ["server_name"] = new()
        {
            Type        = "string",
            Description = "Unique name of the MCP server.",
            Required    = true
        },
        ["url"] = new()
        {
            Type        = "string",
            Description = "The MCP server endpoint URL. Required for 'add'.",
            Required    = false
        },
        ["transport_mode"] = new()
        {
            Type        = "string",
            Description = "Transport mode for 'add'. 'http' uses Streamable HTTP (AutoDetect, default). " +
                          "'sse' forces legacy SSE mode.",
            Required    = false,
            EnumValues  = ["http", "sse"]
        },
        ["headers_json"] = new()
        {
            Type        = "string",
            Description = "Optional JSON object of additional HTTP headers (e.g. '{\"Authorization\":\"Bearer …\"}').",
            Required    = false
        },
        ["auth"] = new()
        {
            Type        = "string",
            Description = "For 'add': 'oauth' (default) signs in when the server asks for it; 'none' never offers a sign-in.",
            Required    = false,
            EnumValues  = ["oauth", "none"]
        },
        ["redirect_url"] = new()
        {
            Type        = "string",
            Description = "For 'complete_sign_in': the full address shown in the browser after approving the sign-in.",
            Required    = false
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var action = arguments.GetValueOrDefault("action")?.ToString()?.ToLowerInvariant();
        return action switch
        {
            "add"              => await AddServerAsync(arguments),
            "remove"           => await RemoveServerAsync(arguments),
            "sign_in"          => await SignInAsync(arguments),
            "complete_sign_in" => CompleteSignIn(arguments),
            _                  => ToolResult.Fail("action must be 'add', 'remove', 'sign_in' or 'complete_sign_in'")
        };
    }

    private async Task<ToolResult> AddServerAsync(Dictionary<string, object?> arguments)
    {
        var serverName = arguments.GetValueOrDefault("server_name")?.ToString();
        var url        = arguments.GetValueOrDefault("url")?.ToString();
        var modeStr    = arguments.GetValueOrDefault("transport_mode")?.ToString()?.ToLowerInvariant();
        var headersJson = arguments.GetValueOrDefault("headers_json")?.ToString();

        if (string.IsNullOrWhiteSpace(serverName))
            return ToolResult.Fail("server_name is required for 'add'");
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Fail("url is required for 'add'");

        if (_mcpManager.Servers.Keys.Any(k => k.Equals(serverName, StringComparison.OrdinalIgnoreCase))
            || _mcpManager.IsOAuthServer(serverName))
            return ToolResult.Fail(
                $"An MCP server named '{serverName}' is already configured. Remove it first with action='remove'.");

        var transportType = modeStr == "sse" ? McpTransportType.Sse : McpTransportType.Http;

        var headers = ParseHeaders(headersJson, out var headerError);
        if (headerError != null)
            return ToolResult.Fail(headerError);

        if (!string.Equals(arguments.GetValueOrDefault("auth")?.ToString(), "none", StringComparison.OrdinalIgnoreCase))
            return await AddOAuthServerAsync(serverName, url, transportType, headers);

        var connected = await _mcpManager.AddServerAsync(serverName, url, transportType, headers,
            oauth: new McpOAuthConfig { Enabled = false });
        if (!connected)
            return ToolResult.Fail(
                $"MCP server '{serverName}' failed to connect. Check the URL and credentials, " +
                $"or review the server log. Error: {_mcpManager.Failures.GetValueOrDefault(serverName, "unknown")}");

        var persistError = PersistServerAdd(serverName, url, transportType, headers, oauth: false);
        if (persistError != null)
            _logger?.LogWarning("manage_mcp_server add: connected but could not save config — {Error}", persistError);

        var saveNote = persistError == null
            ? "saved to appsettings.json"
            : $"NOT saved to appsettings.json ({persistError})";

        return ToolResult.Ok($"MCP server '{serverName}' added and connected. Config {saveNote}.");
    }

    private async Task<ToolResult> RemoveServerAsync(Dictionary<string, object?> arguments)
    {
        var serverName = arguments.GetValueOrDefault("server_name")?.ToString();
        if (string.IsNullOrWhiteSpace(serverName))
            return ToolResult.Fail("server_name is required for 'remove'");

        var actualName = _mcpManager.Servers.Keys.Concat(_mcpManager.AwaitingSignIn)
            .FirstOrDefault(k => k.Equals(serverName, StringComparison.OrdinalIgnoreCase))
            ?? (_mcpManager.IsOAuthServer(serverName) ? serverName : null);
        if (actualName == null)
        {
            var registered = string.Join(", ", _mcpManager.Servers.Keys);
            return ToolResult.Fail(
                $"MCP server '{serverName}' is not registered. " +
                $"Registered: {(registered.Length > 0 ? registered : "none")}");
        }

        await _mcpManager.RemoveServerAsync(actualName);

        var persistError = PersistServerRemove(serverName);
        if (persistError != null)
            _logger?.LogWarning("manage_mcp_server remove: disconnected but could not update config — {Error}", persistError);

        var saveNote = persistError == null
            ? "removed from appsettings.json"
            : $"NOT removed from appsettings.json ({persistError})";

        return ToolResult.Ok($"MCP server '{serverName}' disconnected and {saveNote}.");
    }

    // ── OAuth ─────────────────────────────────────────────────────────────────

    /// <summary>Connects at once when the server needs no sign-in; otherwise saved before the user approves, so a
    /// restart remembers the server and reports it as waiting for a sign-in rather than forgetting it was asked for.</summary>
    private async Task<ToolResult> AddOAuthServerAsync(
        string serverName, string url, McpTransportType transportType, Dictionary<string, string>? headers)
    {
        var config = new McpTransportConfig
        {
            Type = transportType, Url = url, Headers = headers, OAuth = new McpOAuthConfig { Enabled = true }
        };
        var started = await _mcpManager.BeginSignInAsync(serverName, config);
        if (started.Error is { } error)
        {
            await _mcpManager.RemoveServerAsync(serverName);
            return ToolResult.Fail($"MCP server '{serverName}' could not start a sign-in: {error}");
        }

        var persistError = PersistServerAdd(serverName, url, transportType, headers, oauth: true);
        if (persistError != null)
            _logger?.LogWarning("manage_mcp_server add: could not save config — {Error}", persistError);
        var saveNote = persistError == null ? "Saved to appsettings.json." : $"NOT saved to appsettings.json ({persistError}).";

        return ToolResult.Ok(SignInMessage(serverName, started) + " " + saveNote);
    }

    private async Task<ToolResult> SignInAsync(Dictionary<string, object?> arguments)
    {
        var serverName = arguments.GetValueOrDefault("server_name")?.ToString();
        if (string.IsNullOrWhiteSpace(serverName))
            return ToolResult.Fail("server_name is required for 'sign_in'");

        var started = await _mcpManager.BeginSignInAsync(serverName);
        return started.Error is { } error
            ? ToolResult.Fail($"Could not sign in to '{serverName}': {error}")
            : ToolResult.Ok(SignInMessage(serverName, started));
    }

    private ToolResult CompleteSignIn(Dictionary<string, object?> arguments)
    {
        var serverName = arguments.GetValueOrDefault("server_name")?.ToString();
        var redirect = arguments.GetValueOrDefault("redirect_url")?.ToString();
        if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(redirect))
            return ToolResult.Fail("server_name and redirect_url are required for 'complete_sign_in'");

        return _mcpManager.CompleteSignIn(serverName, redirect, out var error)
            ? ToolResult.Ok($"Sign-in to '{serverName}' handed over; its tools appear once the server accepts it.")
            : ToolResult.Fail(error ?? "The sign-in could not be completed.");
    }

    internal static string SignInMessage(string serverName, McpManager.SignInStart started)
    {
        if (started.Connected)
            return $"MCP server '{serverName}' added and connected.";

        var how = started.BrowserOpened
            ? "It has been opened in this computer's browser."
            : started.CallbackProblem is { } problem
                ? $"This computer cannot receive the browser's answer ({problem}), so after approving, copy the full " +
                  "address the browser lands on and give it back (complete_sign_in)."
                : "Open it in a browser.";
        return $"Sign in to '{serverName}' at: {started.SignInUrl} — {how} The server connects by itself once the user " +
               $"approves (within {McpManager.SignInTimeout.TotalMinutes:0} minutes). If the browser is on another device, " +
               "the page it lands on will not load: copy that page's full address and give it back (complete_sign_in).";
    }

    private static Dictionary<string, string>? ParseHeaders(string? headersJson, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(headersJson))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson)
                   ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            error = $"headers_json is not valid JSON: {ex.Message}";
            return null;
        }
    }

    // ── Config persistence ────────────────────────────────────────────────────

    private string? PersistServerAdd(
        string serverName, string url,
        McpTransportType transportType,
        Dictionary<string, string>? headers,
        bool oauth)
    {
        try
        {
            var root = ReadRoot();
            if (root == null) return "Cannot read appsettings.json";

            if (root["MCP"] is not JObject mcp)
            {
                mcp = new JObject();
                root["MCP"] = mcp;
            }

            if (mcp["Servers"] is not JArray servers)
            {
                servers = new JArray();
                mcp["Servers"] = servers;
            }

            // Remove duplicate if present
            var existing = servers.OfType<JObject>()
                .FirstOrDefault(obj => string.Equals(
                    obj["Name"]?.ToString(), serverName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                servers.Remove(existing);

            // Write new structured transport format
            var transportObj = new JObject
            {
                ["Type"] = transportType.ToString(),
                ["Url"]  = url
            };

            if (headers is { Count: > 0 })
            {
                var headersObj = new JObject();
                foreach (var (key, value) in headers)
                    headersObj[key] = value;
                transportObj["Headers"] = headersObj;
            }

            transportObj["OAuth"] = new JObject { ["Enabled"] = oauth };

            servers.Add(new JObject
            {
                ["Name"]      = serverName,
                ["Transport"] = transportObj
            });

            WriteRoot(root);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private string? PersistServerRemove(string serverName)
    {
        try
        {
            var root = ReadRoot();
            if (root == null) return "Cannot read appsettings.json";

            if (root["MCP"] is not JObject mcp) return null;
            if (mcp["Servers"] is not JArray servers) return null;

            var entry = servers.OfType<JObject>()
                .FirstOrDefault(obj => string.Equals(
                    obj["Name"]?.ToString(), serverName, StringComparison.OrdinalIgnoreCase));

            if (entry != null)
            {
                servers.Remove(entry);
                WriteRoot(root);
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private JObject? ReadRoot()
    {
        var json = File.ReadAllText(_configFilePath);
        return JObject.Parse(json, new JsonLoadSettings
        {
            CommentHandling  = CommentHandling.Ignore,
            LineInfoHandling = LineInfoHandling.Ignore
        });
    }

    private void WriteRoot(JObject root)
        => File.WriteAllText(_configFilePath, root.ToString(Formatting.Indented));
}
