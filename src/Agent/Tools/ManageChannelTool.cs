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
///   1. A Channel instance is validated by its provider.
///   2. Its configuration is saved to the user configuration.
///   3. It is registered and connected; a connection failure stays visible and retryable.
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
                "Add, remove, activate or deactivate a messaging channel at runtime, or change which notification topics " +
                "a channel receives. Changes are persisted to user configuration and take effect " +
                "immediately. " +
                $"Supported types: {string.Join(", ", _channelProviderCatalog.SupportedTypes)}. " +
                "For 'add': provide channel_type and config_json. " +
                "For 'remove', 'activate' or 'deactivate': provide channel_name (prefer its unique id). " +
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
                "'subscribe' to change topics; 'activate' or 'deactivate' to switch delivery without deleting configuration.",
            Required = true,
            EnumValues = ["add", "remove", "subscribe", "activate", "deactivate"]
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
                "Unique id, or an unambiguous type. Required for remove, subscribe, activate and deactivate.",
            Required = false
        },
        ["config_json"] = new()
        {
            Type = "string",
            Description =
                "JSON object with channel-specific config fields. Required for 'add'. " +
                "May include \"Name\" to choose the stable id; otherwise a unique name is generated.",
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
        var management = new ChannelManagement(_channelManager, _channelProviderCatalog, _configStore);
        ChannelManagementResult result;
        if (action == "add")
        {
            Dictionary<string, string> config;
            try
            {
                config = new(JsonSerializer.Deserialize<Dictionary<string, string>>(
                    arguments.GetValueOrDefault("config_json")?.ToString() ?? "{}") ?? [], StringComparer.OrdinalIgnoreCase);
            }
            catch { return ToolResult.Fail("config_json must be a JSON object of string values."); }
            config.Remove("Name", out var name);
            config.Remove("Subscribe", out var subscribe);
            config.Remove("Subscriptions", out var subscriptions);
            config.Remove("Enabled");
            config.Remove("Type");
            result = await management.AddAsync(arguments.GetValueOrDefault("channel_type")?.ToString(), name, config,
                arguments.GetValueOrDefault("subscribe")?.ToString() ?? subscribe ?? subscriptions);
        }
        else
        {
            if (action is not ("remove" or "subscribe" or "activate" or "deactivate"))
                return ToolResult.Fail("action must be add, remove, subscribe, activate or deactivate.");
            var requested = arguments.GetValueOrDefault("channel_name")?.ToString();
            if (string.IsNullOrWhiteSpace(requested)) return ToolResult.Fail("channel_name is required.");
            string id;
            try
            {
                var saved = _configStore.Read();
                var ids = saved.Select(e => (Id: e.Name!, Type: e.Type))
                    .Concat(_channelManager.Channels.Values.Select(c => (Id: c.ChannelId, Type: c.Type)))
                    .DistinctBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToList();
                var exact = ids.Where(c => c.Id.Equals(requested, StringComparison.OrdinalIgnoreCase)).ToList();
                var matches = exact.Count > 0 ? exact : ids.Where(c => c.Type.Equals(requested, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1) return ToolResult.Fail("Use the unique channel id; that name is missing or ambiguous.");
                id = matches[0].Id;
            }
            catch { return ToolResult.Fail("Could not read channel configuration."); }
            result = action switch
            {
                "remove" => await management.RemoveAsync(id),
                "subscribe" => await management.SubscribeAsync(id, arguments.GetValueOrDefault("subscribe")?.ToString()),
                _ => await management.SetEnabledAsync(id, action == "activate")
            };
        }
        if (!result.Ok) return ToolResult.Fail(result.Error ?? "Channel operation failed.");
        return ToolResult.Ok($"{result.Id}: {result.Message} {result.Warning}" + (result.Persisted ? " Saved." :
            $" NOT saved: {result.PersistError}. The live change will revert on restart."));
    }
}
