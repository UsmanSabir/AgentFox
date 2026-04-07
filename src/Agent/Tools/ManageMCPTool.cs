using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFox.MCP;
using Microsoft.Extensions.Logging;

namespace AgentFox.Tools;

/// <summary>
/// Tool that adds or removes MCP servers at runtime without restarting.
///
/// When an MCP server is added:
///   1. The MCPClient connects to the server.
///   2. MCP tools from the server are registered in the live ToolRegistry.
///   3. The server config is persisted to appsettings.json so it survives restarts.
///
/// When an MCP server is removed the reverse happens.
/// </summary>
public class ManageMCPTool : BaseTool
{
    private readonly MCPClient _mcpClient;
    private readonly string _configFilePath;
    private readonly ILogger? _logger;

    private static readonly JsonSerializerOptions _jsonWriteOpts = new() { WriteIndented = true };

    public ManageMCPTool(MCPClient mcpClient, string configFilePath, ILogger? logger = null)
    {
        _mcpClient = mcpClient;
        _configFilePath = configFilePath;
        _logger = logger;
    }

    public override string Name => "manage_mcp_server";

    public override string Description =>
        "Add or remove an external MCP server at runtime without restarting. " +
        "Changes are persisted to appsettings.json under MCP:Servers and take effect immediately. " +
        "For 'add': provide server_name and url. Optionally provide timeout_seconds and headers_json. " +
        "For 'remove': provide server_name.";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["action"] = new()
        {
            Type = "string",
            Description = "'add' to configure a new MCP server, 'remove' to disconnect an existing one.",
            Required = true,
            EnumValues = ["add", "remove"]
        },
        ["server_name"] = new()
        {
            Type = "string",
            Description = "Unique name of the MCP server.",
            Required = true
        },
        ["url"] = new()
        {
            Type = "string",
            Description = "The MCP server URL. Required for 'add'.",
            Required = false
        },
        ["timeout_seconds"] = new()
        {
            Type = "number",
            Description = "Optional connection timeout in seconds for the MCP server. Defaults to 30.",
            Required = false
        },
        ["headers_json"] = new()
        {
            Type = "string",
            Description = "Optional JSON object of additional headers to send to the MCP server.",
            Required = false
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var action = arguments.GetValueOrDefault("action")?.ToString()?.ToLowerInvariant();
        return action switch
        {
            "add" => await AddServerAsync(arguments),
            "remove" => await RemoveServerAsync(arguments),
            _ => ToolResult.Fail("action must be 'add' or 'remove'")
        };
    }

    private async Task<ToolResult> AddServerAsync(Dictionary<string, object?> arguments)
    {
        var serverName = arguments.GetValueOrDefault("server_name")?.ToString();
        var url = arguments.GetValueOrDefault("url")?.ToString();
        var timeoutSeconds = arguments.GetValueOrDefault("timeout_seconds");
        var headersJson = arguments.GetValueOrDefault("headers_json")?.ToString();

        if (string.IsNullOrWhiteSpace(serverName))
            return ToolResult.Fail("server_name is required for 'add'");
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Fail("url is required for 'add'");

        if (_mcpClient.Servers.Keys.Any(k => k.Equals(serverName, StringComparison.OrdinalIgnoreCase)))
            return ToolResult.Fail($"An MCP server named '{serverName}' is already configured. Remove it first with action='remove'.");

        var timeout = 30;
        if (timeoutSeconds != null)
        {
            if (timeoutSeconds is int intTimeout)
                timeout = intTimeout;
            else if (timeoutSeconds is long longTimeout)
                timeout = (int)longTimeout;
            else if (timeoutSeconds is double doubleTimeout)
                timeout = (int)doubleTimeout;
            else if (!int.TryParse(timeoutSeconds.ToString(), out timeout))
                return ToolResult.Fail("timeout_seconds must be a number.");
        }

        var headers = ParseHeaders(headersJson, out var headerError);
        if (headerError != null)
            return ToolResult.Fail(headerError);

        var connected = await _mcpClient.AddServerAsync(serverName, url, timeout <= 0 ? 30 : timeout, headers);
        if (!connected)
            return ToolResult.Fail($"MCP server '{serverName}' was created but failed to connect. Check the URL and server availability.");

        var persistError = PersistServerAdd(serverName, url, timeout <= 0 ? 30 : timeout, headers);
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

        var actualName = _mcpClient.Servers.Keys
            .FirstOrDefault(k => k.Equals(serverName, StringComparison.OrdinalIgnoreCase));
        if (actualName == null)
        {
            var registered = string.Join(", ", _mcpClient.Servers.Keys);
            return ToolResult.Fail($"MCP server '{serverName}' is not registered. Registered: {(registered.Length > 0 ? registered : "none")}");
        }

        await _mcpClient.RemoveServerAsync(actualName);

        var persistError = PersistServerRemove(serverName);
        if (persistError != null)
            _logger?.LogWarning("manage_mcp_server remove: disconnected but could not update config — {Error}", persistError);

        var saveNote = persistError == null
            ? "removed from appsettings.json"
            : $"NOT removed from appsettings.json ({persistError})";

        return ToolResult.Ok($"MCP server '{serverName}' disconnected and {saveNote}.");
    }

    private Dictionary<string, string>? ParseHeaders(string? headersJson, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(headersJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson)
                ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            error = $"headers_json is not valid JSON: {ex.Message}";
            return null;
        }
    }

    private string? PersistServerAdd(string serverName, string url, int timeoutSeconds, Dictionary<string, string>? headers)
    {
        try
        {
            var root = ReadRoot();
            if (root == null) return "Cannot read appsettings.json";

            if (root["MCP"] is not JsonObject mcp)
            {
                mcp = new JsonObject();
                root["MCP"] = mcp;
            }

            if (mcp["Servers"] is not JsonArray servers)
            {
                servers = new JsonArray();
                mcp["Servers"] = servers;
            }

            var existing = servers.OfType<JsonObject>()
                .FirstOrDefault(obj => obj["Name"]?.ToString()?.Equals(serverName, StringComparison.OrdinalIgnoreCase) == true);
            if (existing != null)
            {
                servers.Remove(existing);
            }

            var serverEntry = new JsonObject
            {
                ["Name"] = serverName,
                ["Url"] = url,
                ["TimeoutSeconds"] = timeoutSeconds
            };

            if (headers != null && headers.Count > 0)
            {
                var headersObj = new JsonObject();
                foreach (var (key, value) in headers)
                    headersObj[key] = value;
                serverEntry["Headers"] = headersObj;
            }

            servers.Add(serverEntry);
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

            if (root["MCP"] is not JsonObject mcp) return null;
            if (mcp["Servers"] is not JsonArray servers) return null;

            var entry = servers.OfType<JsonObject>()
                .FirstOrDefault(obj => obj["Name"]?.ToString()?.Equals(serverName, StringComparison.OrdinalIgnoreCase) == true);

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

    private JsonObject? ReadRoot()
    {
        var json = File.ReadAllText(_configFilePath);
        return JsonNode.Parse(json) as JsonObject;
    }

    private void WriteRoot(JsonObject root)
    {
        File.WriteAllText(_configFilePath, root.ToJsonString(_jsonWriteOpts));
    }
}
