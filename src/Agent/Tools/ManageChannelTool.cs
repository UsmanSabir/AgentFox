using System.Text.Json;
using AgentFox.Channels;
using AgentFox.Plugins.Channels;
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
    private readonly ChannelConfigStore _configStore;
    private readonly ILogger? _logger;

    public ManageChannelTool(
        ChannelManager channelManager,
        ChannelProviderCatalog channelProviderCatalog,
        ChannelConfigStore configStore,
        ILogger? logger = null)
    {
        _channelManager = channelManager;
        _channelProviderCatalog = channelProviderCatalog;
        _configStore = configStore;
        _logger = logger;
    }

    public override string Name => "manage_channel";

    public override string Description
    {
        get
        {
            var parts = _channelProviderCatalog.Providers.Select(provider =>
            {
                var schema = string.Join(", ", provider.GetConfigSchema().Select(f =>
                    $"{f.Key}{(f.Value.Required ? "*" : "")}"));
                return $"{provider.ChannelType}: {{{schema}}}";
            });

            return
                "Add or remove a messaging channel at runtime, or change which notification topics " +
                "a channel receives. Changes are persisted to appsettings.json and take effect " +
                "immediately. " +
                $"Supported types: {string.Join(", ", _channelProviderCatalog.SupportedTypes)}. " +
                "For 'add': provide channel_type and config_json. " +
                "For 'remove': provide channel_name (for example 'telegram'). " +
                "For 'subscribe': provide channel_name and subscribe. " +
                "Config shapes by provider: " +
                string.Join("; ", parts);
        }
    }

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["action"] = new()
        {
            Type = "string",
            Description =
                "'add' to add a new channel, 'remove' to remove an existing one, " +
                "'subscribe' to change which topics an existing channel receives.",
            Required = true,
            EnumValues = ["add", "remove", "subscribe"]
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
            Description =
                "Id, name or type of the channel. Required for 'remove' and 'subscribe'.",
            Required = false
        },
        ["config_json"] = new()
        {
            Type = "string",
            Description =
                "JSON object with channel-specific config fields. Required for 'add'. " +
                "May include \"Name\" to pin a stable id — needed when more than one channel of " +
                "the same type exists.",
            Required = false
        },
        ["subscribe"] = new()
        {
            Type = "string",
            Description = SubscribeParameterDescription,
            Required = false
        }
    };

    /// <summary>
    /// Shared by 'add' and 'subscribe'. Spells out the wildcard rules because getting them wrong is
    /// silent: a filter that matches nothing looks identical in config to one that matches
    /// everything, and the only symptom is notifications that never arrive.
    /// </summary>
    private static string SubscribeParameterDescription
    {
        get
        {
            var known = NotificationTopics.Known.Select(t => t.Name).ToList();

            return
                "Comma-separated topic filters this channel receives, e.g. \"trading.order.>, hitl.>\". " +
                "'*' matches exactly one segment (trading.* matches trading.order but not " +
                "trading.order.accepted); '>' matches one or more trailing segments and must come " +
                "last (trading.> matches both). Omit, or use \">\", for everything. " +
                (known.Count > 0 ? $"Topics currently published: {string.Join(", ", known)}." : string.Empty);
        }
    }

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var action = arguments.GetValueOrDefault("action")?.ToString()?.ToLowerInvariant();

        return action switch
        {
            "add" => await AddChannelAsync(arguments),
            "remove" => await RemoveChannelAsync(arguments),
            "subscribe" => SubscribeChannel(arguments),
            _ => ToolResult.Fail("action must be 'add', 'remove' or 'subscribe'")
        };
    }

    /// <summary>
    /// Repoints an existing channel's subscriptions. Applied live first, then persisted — the
    /// running channel is what actually routes, and a config file the process has not re-read
    /// changes nothing until restart.
    /// </summary>
    private ToolResult SubscribeChannel(Dictionary<string, object?> arguments)
    {
        var channelName = arguments.GetValueOrDefault("channel_name")?.ToString();
        if (string.IsNullOrWhiteSpace(channelName))
            return ToolResult.Fail("channel_name is required for 'subscribe'");

        var channel = _channelManager.GetChannelByName(channelName);
        if (channel == null)
        {
            var registered = string.Join(", ", _channelManager.Channels.Values.Select(c => c.ChannelId));
            return ToolResult.Fail(
                $"Channel '{channelName}' is not registered. " +
                $"Registered: {(registered.Length > 0 ? registered : "none")}");
        }

        var spec = arguments.GetValueOrDefault("subscribe")?.ToString();
        if (!TopicSubscription.TryParse(spec, out var subscription, out var errors))
            return ToolResult.Fail(
                $"Invalid subscription: {string.Join(" ", errors)} Nothing was changed.");

        var previous = channel.Subscriptions;
        channel.Subscriptions = subscription;

        var persistError = _configStore.SetSubscription(channel, subscription);
        if (persistError != null)
            _logger?.LogWarning(
                "manage_channel subscribe: applied but could not save config - {Error}", persistError);

        var saveNote = persistError == null
            ? "saved to appsettings.json"
            : $"NOT saved to appsettings.json ({persistError}) — it will revert on restart";

        return ToolResult.Ok(
            $"Channel '{channel.ChannelId}' now receives: {subscription} (was: {previous}). {saveNote}.");
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

        // A subscription spec may arrive either as its own argument or inside config_json; the
        // argument wins. Folding it into config is what gets it persisted, since the whole config
        // dictionary is written back to the appsettings entry.
        var subscribeArg = arguments.GetValueOrDefault("subscribe")?.ToString();
        if (!string.IsNullOrWhiteSpace(subscribeArg))
            config[ChannelConfigurationEntry.SubscribeKey] = subscribeArg.Trim();

        config.TryGetValue(ChannelConfigurationEntry.SubscribeKey, out var subscribeSpec);
        if (!TopicSubscription.TryParse(subscribeSpec, out var subscription, out var subscribeErrors))
            return ToolResult.Fail(
                $"Invalid subscription: {string.Join(" ", subscribeErrors)} Nothing was added.");

        // Name is the stable id subscriptions hang off. Two channels of the same type are a normal
        // setup once topics exist — one Telegram chat for order flow, another for everything else —
        // so the old blanket "one per type" rule is now a name collision check.
        var requestedName = config.TryGetValue(ChannelConfigurationEntry.NameKey, out var nameValue)
                            && !string.IsNullOrWhiteSpace(nameValue)
            ? nameValue.Trim()
            : null;

        if (requestedName != null &&
            _channelManager.Channels.Values.Any(c => c.ChannelId.Equals(requestedName, StringComparison.OrdinalIgnoreCase)))
        {
            return ToolResult.Fail(
                $"A channel named '{requestedName}' is already registered. Pick another Name, or " +
                "remove that one first with action='remove'.");
        }

        if (requestedName == null &&
            _channelManager.Channels.Values.Any(c => c.Type.Equals(channelType, StringComparison.OrdinalIgnoreCase)))
        {
            return ToolResult.Fail(
                $"A '{channelType}' channel is already registered. To run a second one, include a " +
                "\"Name\" in config_json so each has a stable id. Otherwise remove the existing one " +
                "with action='remove'.");
        }

        var (channel, factoryError) = _channelProviderCatalog.Create(channelType, config);
        if (channel == null)
            return ToolResult.Fail(factoryError ?? "Failed to create channel");

        if (requestedName != null)
            channel.ChannelId = requestedName;

        channel.Subscriptions = subscription;

        var connected = await _channelManager.AddAndConnectAsync(channel);
        if (!connected)
        {
            return ToolResult.Fail(
                $"'{channelType}' channel was created but failed to connect. " +
                "Check that credentials are valid and the service is reachable.");
        }

        var persistError = _configStore.Add(channelType, config);
        if (persistError != null)
            _logger?.LogWarning("manage_channel add: connected but could not save config - {Error}", persistError);

        var saveNote = persistError == null
            ? "saved to appsettings.json"
            : $"NOT saved to appsettings.json ({persistError})";

        return ToolResult.Ok(
            $"Channel '{channel.Name}' added and connected as '{channel.ChannelId}', receiving: " +
            $"{channel.Subscriptions}. Config {saveNote}. send_to_channel now includes this channel.");
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

        var persistError = _configStore.Remove(channel);
        if (persistError != null)
            _logger?.LogWarning("manage_channel remove: disconnected but could not update config - {Error}", persistError);

        var saveNote = persistError == null
            ? "removed from appsettings.json"
            : $"NOT removed from appsettings.json ({persistError})";

        return ToolResult.Ok($"Channel '{channel.Type}' disconnected and {saveNote}.");
    }

}
