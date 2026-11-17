using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFox.Plugins.Channels;
using Microsoft.Extensions.Configuration;

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
    // The release-owned defaults FILE, and nothing else. Never the host's effective IConfiguration:
    // that also carries environment variables, command-line switches and hosted secrets, and the
    // first write copies the initial list into the user file, so a token supplied only through the
    // environment would land on disk in plain text.
    private readonly string? _defaultsFilePath;
    private readonly object _gate = new();

    // Shared by the HTTP and agent-tool surfaces, including across awaits to a transport.
    internal SemaphoreSlim ManagementGate { get; } = new(1, 1);

    public ChannelConfigStore(string configFilePath, string? defaultsFilePath = null)
    {
        _configFilePath = configFilePath;
        _defaultsFilePath = defaultsFilePath;
    }

    public string ConfigFilePath => _configFilePath;

    public IReadOnlyList<ChannelConfigurationEntry> Read()
    {
        lock (_gate)
        {
            var root = ReadRoot();
            if (root == null) throw new InvalidOperationException("Invalid channel configuration root.");
            if (root["Channels"] == null) return [];
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()));
            var config = new ConfigurationBuilder().AddJsonStream(stream).Build();
            try { return ChannelConfiguration.ReadEntries(config, includeDisabled: true); }
            finally { (config as IDisposable)?.Dispose(); }
        }
    }

    public string? SetEnabled(string id, bool enabled) => Update(id, entry => entry["Enabled"] = enabled);
    public string? SetSubscription(string id, TopicSubscription subscription) =>
        Update(id, entry => entry[ChannelConfigurationEntry.SubscribeKey] = subscription.ToString());

    public string? Remove(string id) => Update(id, entry => entry.Parent!.AsArray().Remove(entry));

    private string? Update(string id, Action<JsonObject> update)
    {
        lock (_gate)
        {
            try
            {
                var root = ReadRoot();
                if (root == null) return "Cannot read channel configuration.";
                var entries = ChannelConfiguration.GetOrNormalizeCanonicalArray(root);
                var matches = entries.OfType<JsonObject>()
                    .Where(e => string.Equals(e["Name"]?.GetValue<string>()?.Trim(), id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count != 1) return "No unique saved channel matches this id.";
                update(matches[0]);
                WriteRoot(root);
                return null;
            }
            catch { return "Could not save channel configuration. Check the agent's configuration file permissions."; }
        }
    }

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
                if (config.TryGetValue("Name", out var id) && channels.OfType<JsonObject>().Any(e =>
                    string.Equals(e["Name"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase)))
                    return "A saved channel already uses this name.";
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
        var unnamedByType = new List<JsonNode>();

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
            if ((string.IsNullOrWhiteSpace(name) || name.StartsWith("channel-", StringComparison.Ordinal)) && type != null &&
                type.Equals(channel.Type, StringComparison.OrdinalIgnoreCase))
            {
                unnamedByType.Add(node);
            }
        }

        // Compatibility for callers that still address one old, unnamed entry by its type.
        // Never let a stale named id edit another bot of the same type.
        return channel.ChannelId.Equals(channel.Type, StringComparison.OrdinalIgnoreCase)
            && unnamedByType.Count == 1 ? unnamedByType[0] : null;
    }

    private JsonObject? ReadRoot()
    {
        var root = File.Exists(_configFilePath)
            ? JsonNode.Parse(File.ReadAllText(_configFilePath), documentOptions: new()
                { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            : new JsonObject();
        if (root != null && root["Channels"] == null)
        {
            // An explicit user Channels array is authoritative, including []. IConfiguration's
            // per-index merging otherwise resurrects deleted defaults after the next restart.
            var defaults = ReadDefaultEntries();
            if (defaults != null)
                root["Channels"] = new JsonArray(defaults
                    .Select(e => (JsonNode?)JsonSerializer.SerializeToNode(e.Config)).ToArray());
        }
        return root;
    }

    private List<ChannelConfigurationEntry>? ReadDefaultEntries()
    {
        if (string.IsNullOrWhiteSpace(_defaultsFilePath) || !File.Exists(_defaultsFilePath)) return null;
        var config = new ConfigurationBuilder().AddJsonFile(_defaultsFilePath, optional: true, reloadOnChange: false).Build();
        try { return ChannelConfiguration.ReadEntries(config, includeDisabled: true); }
        finally { (config as IDisposable)?.Dispose(); }
    }

    private void WriteRoot(JsonObject root)
    {
        var staging = _configFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(staging, root.ToJsonString(JsonWriteOpts));
            File.Move(staging, _configFilePath, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
