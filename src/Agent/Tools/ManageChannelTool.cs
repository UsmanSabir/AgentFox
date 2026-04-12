using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFox.Channels;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgentFox.Tools;

/// <summary>
/// Tool that adds or removes messaging channels at runtime without restarting.
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
    private readonly ChannelProviderCatalog _channelProviderCatalog;
    private readonly string _configFilePath;
    private readonly ILogger? _logger;

    private static readonly JsonSerializerOptions _jsonWriteOpts = new() { WriteIndented = true };

    public ManageChannelTool(
        ChannelManager channelManager,
        ChannelProviderCatalog channelProviderCatalog,
        string configFilePath,
        ILogger? logger = null)
    {
        _channelManager = channelManager;
        _channelProviderCatalog = channelProviderCatalog;
        _configFilePath = configFilePath;
        _logger = logger;
    }

    public override string Name => "manage_channel";

    public override string Description
    {
        get
        {
            var parts = _channelProviderCatalog.Providers.Select(provider =>
            {
                var schema = string.Join(", ", provider.GetConfigSchema().Select(field =>
                    $"{field.Key}{(field.Value.Required ? "*" : "")}"));
                return $"{provider.ChannelType}: {{{schema}}}";
            });

            return
                "Add or remove a messaging channel at runtime without restarting. " +
                "Changes are persisted to appsettings.json and take effect immediately. " +
                $"Supported types: {string.Join(", ", _channelProviderCatalog.SupportedTypes)}. " +
                "For 'add': provide channel_type and config_json. " +
                "For 'remove': provide channel_name (for example 'telegram'). " +
                "Config shapes by provider: " +
                string.Join("; ", parts);
        }
    }

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
            Description = "Channel type to add. Required for 'add'.",
            Required = false,
            EnumValues = [.. _channelProviderCatalog.SupportedTypes]
        },
        ["channel_name"] = new()
        {
            Type = "string",
            Description = "Name or type of the channel to remove. Required for 'remove'.",
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
            "add" => await AddChannelAsync(arguments),
            "remove" => await RemoveChannelAsync(arguments),
            _ => ToolResult.Fail("action must be 'add' or 'remove'")
        };
    }

    private async Task<ToolResult> AddChannelAsync(Dictionary<string, object?> arguments)
    {
        var channelType = arguments.GetValueOrDefault("channel_type")?.ToString()?.Trim().ToLowerInvariant();
        var configJson = arguments.GetValueOrDefault("config_json")?.ToString();

        if (string.IsNullOrWhiteSpace(channelType))
            return ToolResult.Fail("channel_type is required for 'add'");
        if (string.IsNullOrWhiteSpace(configJson))
            return ToolResult.Fail("config_json is required for 'add'");

        Dictionary<string, string> config;
        try
        {
            config = JsonSerializer.Deserialize<Dictionary<string, string>>(configJson)
                ?? throw new InvalidOperationException("Parsed to null");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"config_json is not valid JSON: {ex.Message}");
        }

        if (_channelManager.Channels.Values.Any(c => c.Type.Equals(channelType, StringComparison.OrdinalIgnoreCase)))
        {
            return ToolResult.Fail(
                $"A '{channelType}' channel is already registered. Remove it first with action='remove'.");
        }

        var (channel, factoryError) = _channelProviderCatalog.Create(channelType, config);
        if (channel == null)
            return ToolResult.Fail(factoryError ?? "Failed to create channel");

        var connected = await _channelManager.AddAndConnectAsync(channel);
        if (!connected)
        {
            return ToolResult.Fail(
                $"'{channelType}' channel was created but failed to connect. " +
                "Check that credentials are valid and the service is reachable.");
        }

        var persistError = PersistChannelAdd(channelType, config);
        if (persistError != null)
            _logger?.LogWarning("manage_channel add: connected but could not save config - {Error}", persistError);

        var saveNote = persistError == null
            ? "saved to appsettings.json"
            : $"NOT saved to appsettings.json ({persistError})";

        return ToolResult.Ok(
            $"Channel '{channel.Name}' added and connected. Config {saveNote}. " +
            "send_to_channel now includes this channel.");
    }

    private async Task<ToolResult> RemoveChannelAsync(Dictionary<string, object?> arguments)
    {
        var channelName = arguments.GetValueOrDefault("channel_name")?.ToString();
        if (string.IsNullOrWhiteSpace(channelName))
            return ToolResult.Fail("channel_name is required for 'remove'");

        var channel = _channelManager.GetChannelByName(channelName);
        if (channel == null)
        {
            var registered = string.Join(", ",
                _channelManager.Channels.Values.Select(c => c.Type).Distinct(StringComparer.OrdinalIgnoreCase));
            return ToolResult.Fail(
                $"Channel '{channelName}' is not registered. " +
                $"Registered: {(registered.Length > 0 ? registered : "none")}");
        }

        await _channelManager.RemoveChannelAsync(channel.ChannelId);

        var persistError = PersistChannelRemove(channel.Type);
        if (persistError != null)
            _logger?.LogWarning("manage_channel remove: disconnected but could not update config - {Error}", persistError);

        var saveNote = persistError == null
            ? "removed from appsettings.json"
            : $"NOT removed from appsettings.json ({persistError})";

        return ToolResult.Ok($"Channel '{channel.Type}' disconnected and {saveNote}.");
    }

    private string? PersistChannelAdd(string channelType, Dictionary<string, string> config)
    {
        try
        {
            var root = ReadRoot();
            if (root == null)
                return "Cannot read appsettings.json";

            var channels = ChannelConfiguration.GetOrNormalizeCanonicalArray(root);
            var entry = new JsonObject
            {
                ["Type"] = channelType,
                ["Enabled"] = true
            };

            foreach (var (k, v) in config)
                entry[k] = v;

            channels.Add(entry);
            WriteRoot(root);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private string? PersistChannelRemove(string channelType)
    {
        try
        {
            var root = ReadRoot();
            if (root == null)
                return "Cannot read appsettings.json";

            var channels = ChannelConfiguration.GetOrNormalizeCanonicalArray(root);
            JsonNode? toRemove = null;
            foreach (var node in channels)
            {
                if (node is not JsonObject channel)
                    continue;

                var type = channel["Type"]?.GetValue<string>();
                if (type != null && type.Equals(channelType, StringComparison.OrdinalIgnoreCase))
                {
                    toRemove = node;
                    break;
                }
            }

            if (toRemove != null)
            {
                channels.Remove(toRemove);
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
