using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFox.Channels;
using Microsoft.Extensions.Logging;

namespace AgentFox.Tools;

/// <summary>
/// Tool that adds or removes messaging channels at runtime — no restart needed.
///
/// When a channel is added:
///   1. A Channel instance is created via ChannelFactory.
///   2. It is connected and registered in the live ChannelManager.
///   3. The config is persisted to appsettings.json so it survives restarts.
///   4. SendToChannelTool immediately reflects the new channel (its Parameters are computed live).
///
/// When a channel is removed the reverse happens.
/// </summary>
public class ManageChannelTool : BaseTool
{
    private readonly ChannelManager _channelManager;
    private readonly string _configFilePath;
    private readonly ILogger? _logger;

    private static readonly JsonSerializerOptions _jsonWriteOpts = new() { WriteIndented = true };

    public ManageChannelTool(ChannelManager channelManager, string configFilePath, ILogger? logger = null)
    {
        _channelManager = channelManager;
        _configFilePath = configFilePath;
        _logger = logger;
    }

    public override string Name => "manage_channel";

    public override string Description =>
        "Add or remove a messaging channel at runtime without restarting. " +
        "Changes are persisted to appsettings.json and take effect immediately. " +
        "Supported types: telegram, slack, discord, teams, whatsapp. " +
        "For 'add': provide channel_type and config_json. " +
        "For 'remove': provide channel_name (e.g. 'telegram'). " +
        "Config shapes — " +
        "Telegram: {\"BotToken\":\"...\",\"PollingTimeoutSeconds\":30}; " +
        "Slack: {\"BotToken\":\"xoxb-...\",\"SigningSecret\":\"...\"}; " +
        "Discord: {\"BotToken\":\"...\",\"GuildId\":\"123\",\"DefaultChannelId\":\"456\"}; " +
        "Teams: {\"TenantId\":\"...\",\"ClientId\":\"...\",\"ClientSecret\":\"...\",\"ServiceUrl\":\"...\"}; " +
        "WhatsApp: {\"PhoneNumberId\":\"...\",\"AccessToken\":\"...\",\"BusinessAccountId\":\"...\"}";

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["action"] = new()
        {
            Type = "string",
            Description = "'add' to add a new channel, 'remove' to remove an existing one.",
            Required = true,
            EnumValues = ["add", "remove"]
        },
        ["channel_type"] = new()
        {
            Type = "string",
            Description = "Channel type to add. Required for 'add'. One of: telegram, slack, discord, teams, whatsapp.",
            Required = false,
            EnumValues = [.. ChannelFactory.SupportedTypes]
        },
        ["channel_name"] = new()
        {
            Type = "string",
            Description = "Name of the channel to remove. Required for 'remove'. E.g., 'telegram'.",
            Required = false
        },
        ["config_json"] = new()
        {
            Type = "string",
            Description = "JSON object with channel-specific config fields. Required for 'add'.",
            Required = false
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var action = arguments.GetValueOrDefault("action")?.ToString()?.ToLowerInvariant();

        return action switch
        {
            "add"    => await AddChannelAsync(arguments),
            "remove" => await RemoveChannelAsync(arguments),
            _ => ToolResult.Fail("action must be 'add' or 'remove'")
        };
    }

    // ── Add ──────────────────────────────────────────────────────────────────

    private async Task<ToolResult> AddChannelAsync(Dictionary<string, object?> arguments)
    {
        var channelType = arguments.GetValueOrDefault("channel_type")?.ToString();
        var configJson  = arguments.GetValueOrDefault("config_json")?.ToString();

        if (string.IsNullOrWhiteSpace(channelType))
            return ToolResult.Fail("channel_type is required for 'add'");
        if (string.IsNullOrWhiteSpace(configJson))
            return ToolResult.Fail("config_json is required for 'add'");

        // Parse the config JSON into a flat string dictionary
        Dictionary<string, string> config;
        try
        {
            config = JsonSerializer.Deserialize<Dictionary<string, string>>(configJson)
                ?? throw new Exception("Parsed to null");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"config_json is not valid JSON: {ex.Message}");
        }

        // Reject duplicates
        if (_channelManager.Channels.Values.Any(c =>
                c.Name.Equals(channelType, StringComparison.OrdinalIgnoreCase)))
        {
            return ToolResult.Fail(
                $"A '{channelType}' channel is already registered. Remove it first with action='remove'.");
        }

        // Create the channel instance
        var (channel, factoryError) = ChannelFactory.Create(channelType, config, _logger);
        if (channel == null)
            return ToolResult.Fail(factoryError ?? "Failed to create channel");

        // Connect and register in the live ChannelManager
        var connected = await _channelManager.AddAndConnectAsync(channel);
        if (!connected)
            return ToolResult.Fail(
                $"'{channelType}' channel was created but failed to connect. " +
                "Check that credentials are valid and the service is reachable.");

        // Persist to appsettings.json (best-effort — channel is already live even if this fails)
        var persistError = PersistChannelAdd(channelType, config);
        if (persistError != null)
            _logger?.LogWarning("manage_channel add: connected but could not save config — {Error}", persistError);

        var saveNote = persistError == null
            ? "saved to appsettings.json"
            : $"NOT saved to appsettings.json ({persistError})";

        return ToolResult.Ok(
            $"Channel '{channel.Name}' added and connected. Config {saveNote}. " +
            "send_to_channel now includes this channel.");
    }

    // ── Remove ───────────────────────────────────────────────────────────────

    private async Task<ToolResult> RemoveChannelAsync(Dictionary<string, object?> arguments)
    {
        var channelName = arguments.GetValueOrDefault("channel_name")?.ToString();
        if (string.IsNullOrWhiteSpace(channelName))
            return ToolResult.Fail("channel_name is required for 'remove'");

        var channel = _channelManager.GetChannelByName(channelName);
        if (channel == null)
        {
            var registered = string.Join(", ",
                _channelManager.Channels.Values.Select(c => c.Name.ToLowerInvariant()));
            return ToolResult.Fail(
                $"Channel '{channelName}' is not registered. " +
                $"Registered: {(registered.Length > 0 ? registered : "none")}");
        }

        await _channelManager.RemoveChannelAsync(channel.ChannelId);

        var persistError = PersistChannelRemove(channelName);
        if (persistError != null)
            _logger?.LogWarning("manage_channel remove: disconnected but could not update config — {Error}", persistError);

        var saveNote = persistError == null
            ? "removed from appsettings.json"
            : $"NOT removed from appsettings.json ({persistError})";

        return ToolResult.Ok($"Channel '{channelName}' disconnected and {saveNote}.");
    }

    // ── appsettings.json persistence ─────────────────────────────────────────

    private string? PersistChannelAdd(string channelType, Dictionary<string, string> config)
    {
        try
        {
            var root = ReadRoot();
            if (root == null) return "Cannot read appsettings.json";

            // Ensure the Channels section exists
            if (root["Channels"] is not JsonObject channels)
            {
                channels = new JsonObject();
                root["Channels"] = channels;
            }

            // Use PascalCase key to match existing conventions (e.g. "Telegram")
            var key = char.ToUpperInvariant(channelType[0]) + channelType[1..].ToLowerInvariant();
            var entry = new JsonObject { ["Enabled"] = true };
            foreach (var (k, v) in config)
                entry[k] = v;

            channels[key] = entry;
            WriteRoot(root);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private string? PersistChannelRemove(string channelName)
    {
        try
        {
            var root = ReadRoot();
            if (root == null) return "Cannot read appsettings.json";

            if (root["Channels"] is not JsonObject channels) return null;

            // Find the key case-insensitively
            var key = channels
                .Select(kv => kv.Key)
                .FirstOrDefault(k => k.Equals(channelName, StringComparison.OrdinalIgnoreCase));

            if (key != null)
            {
                channels.Remove(key);
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
