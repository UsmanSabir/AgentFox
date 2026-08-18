using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFox.Plugins.Channels;

namespace AgentFox.Channels;

/// <summary>
/// Reads and rewrites the <c>Channels</c> array in appsettings.json.
///
/// <para>
/// Extracted from <c>ManageChannelTool</c> once the web UI grew a subscription editor: two surfaces
/// mutating the same file through two copies of the same read-modify-write is how a channel entry
/// gets clobbered. Every write goes through one lock here, so a save from the browser and a
/// <c>manage_channel</c> call from an agent turn serialize instead of racing.
/// </para>
/// <para>
/// Methods return <c>null</c> on success and an error string otherwise, matching the convention the
/// tool already reported to the model. Persistence failing is never fatal to the caller: the live
/// channel has already been changed, and losing the save costs a restart, not the operation.
/// </para>
/// </summary>
public sealed class ChannelConfigStore
{
    private static readonly JsonSerializerOptions JsonWriteOpts = new() { WriteIndented = true };

    private readonly string _configFilePath;
    private readonly object _gate = new();

    public ChannelConfigStore(string configFilePath) => _configFilePath = configFilePath;

    public string ConfigFilePath => _configFilePath;

    /// <summary>Appends an entry for a newly added channel.</summary>
    public string? Add(string channelType, Dictionary<string, string> config)
    {
        lock (_gate)
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
    }

    /// <summary>Drops the entry backing <paramref name="channel"/>, if it has one.</summary>
    public string? Remove(Channel channel)
    {
        lock (_gate)
        {
            try
            {
                var root = ReadRoot();
                if (root == null)
                    return "Cannot read appsettings.json";

                var channels = ChannelConfiguration.GetOrNormalizeCanonicalArray(root);
                var toRemove = FindEntry(channels, channel);

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
    }

    /// <summary>
    /// Writes a channel's topic filters back to its entry. A channel added at runtime and never
    /// saved has no entry to update — reported rather than silently succeeding, because the caller
    /// needs to say the change will not survive a restart.
    /// </summary>
    public string? SetSubscription(Channel channel, TopicSubscription subscription)
    {
        lock (_gate)
        {
            try
            {
                var root = ReadRoot();
                if (root == null)
                    return "Cannot read appsettings.json";

                var channels = ChannelConfiguration.GetOrNormalizeCanonicalArray(root);
                if (FindEntry(channels, channel) is not JsonObject entry)
                    return "No matching entry in the Channels array — the channel was added at runtime and never saved";

                entry[ChannelConfigurationEntry.SubscribeKey] = subscription.ToString();
                WriteRoot(root);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    /// <summary>
    /// Locates the config entry backing a live channel. Name is tried first because it is the only
    /// unique key: matching on type alone would rewrite or delete an arbitrary one of two Telegram
    /// entries, which is the same identity bug that made subscriptions unstorable in the first place.
    /// </summary>
    private static JsonNode? FindEntry(JsonArray channels, Channel channel)
    {
        JsonNode? byType = null;

        foreach (var node in channels)
        {
            if (node is not JsonObject entry)
                continue;

            var name = entry[ChannelConfigurationEntry.NameKey]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name) &&
                name.Equals(channel.ChannelId, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            var type = entry["Type"]?.GetValue<string>();
            if (byType == null && type != null &&
                type.Equals(channel.Type, StringComparison.OrdinalIgnoreCase))
            {
                byType = node;
            }
        }

        return byType;
    }

    private JsonObject? ReadRoot() =>
        JsonNode.Parse(File.ReadAllText(_configFilePath)) as JsonObject;

    private void WriteRoot(JsonObject root) =>
        File.WriteAllText(_configFilePath, root.ToJsonString(JsonWriteOpts));
}
