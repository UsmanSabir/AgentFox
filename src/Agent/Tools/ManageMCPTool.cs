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
        "For 'add': provide server_name, url, and optionally transport_mode and headers_json. " +
        "For 'remove': provide server_name only.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["action"] = new()
        {
            Type        = "string",
            Description = "'add' to configure a new MCP server, 'remove' to disconnect an existing one.",
            Required    = true,
            EnumValues  = ["add", "remove"]
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
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var action = arguments.GetValueOrDefault("action")?.ToString()?.ToLowerInvariant();
        return action switch
        {
            "add"    => await AddServerAsync(arguments),
            "remove" => await RemoveServerAsync(arguments),
            _        => ToolResult.Fail("action must be 'add' or 'remove'")
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

        if (_mcpManager.Servers.Keys.Any(k => k.Equals(serverName, StringComparison.OrdinalIgnoreCase)))
            return ToolResult.Fail(
                $"An MCP server named '{serverName}' is already configured. Remove it first with action='remove'.");

        var transportType = modeStr == "sse" ? McpTransportType.Sse : McpTransportType.Http;

        var headers = ParseHeaders(headersJson, out var headerError);
        if (headerError != null)
            return ToolResult.Fail(headerError);

        var connected = await _mcpManager.AddServerAsync(serverName, url, transportType, headers);
        if (!connected)
            return ToolResult.Fail(
                $"MCP server '{serverName}' failed to connect. Check the URL and credentials, " +
                $"or review the server log. Error: {_mcpManager.Failures.GetValueOrDefault(serverName, "unknown")}");

        var persistError = PersistServerAdd(serverName, url, transportType, headers);
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

        var actualName = _mcpManager.Servers.Keys
            .FirstOrDefault(k => k.Equals(serverName, StringComparison.OrdinalIgnoreCase));
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
        Dictionary<string, string>? headers)
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
