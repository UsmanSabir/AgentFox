using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentFox.Channels;

public sealed class ChannelConfigurationEntry
{
    /// <summary>Config key holding an operator-chosen, stable channel id.</summary>
    public const string NameKey = "Name";

    /// <summary>Config key holding the topic filters this channel listens on.</summary>
    public const string SubscribeKey = "Subscribe";

    /// <summary>Accepted spellings of <see cref="SubscribeKey"/>, in precedence order.</summary>
    internal static readonly string[] SubscribeKeys = [SubscribeKey, "Subscriptions"];

    public required string Key { get; init; }
    public required string Type { get; init; }
    public required Dictionary<string, string> Config { get; init; }

    /// <summary>
    /// The operator-assigned id, if any. Without it a channel's id is whatever its provider minted
    /// — <c>"telegram"</c> for every Telegram bot, a token prefix for Slack — which is fine until
    /// subscriptions have to be stored against it.
    /// </summary>
    public string? Name =>
        Config.TryGetValue(NameKey, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : null;

    /// <summary>
    /// The raw subscription spec, or null when unset. Null means catch-all; see
    /// <see cref="AgentFox.Plugins.Channels.TopicSubscription"/>.
    /// </summary>
    public string? SubscribeSpec =>
        Config.TryGetValue(SubscribeKey, out var spec) && !string.IsNullOrWhiteSpace(spec)
            ? spec.Trim()
            : null;

    public IEnumerable<ChannelConfigurationValue> GetChildren() =>
        Config.Select(kv => new ChannelConfigurationValue(kv.Key, kv.Value));
}

public sealed record ChannelConfigurationValue(string Key, string? Value);

public static class ChannelConfiguration
{
    public static List<ChannelConfigurationEntry> ReadEntries(IConfiguration configuration, ILogger? logger = null)
    {
        var section = configuration.GetSection("Channels");
        if (!section.Exists())
            return [];

        var children = section.GetChildren().ToList();
        if (children.Count == 0)
            return [];

        var arrayChildren = children.Where(c => int.TryParse(c.Key, out _)).OrderBy(c => int.Parse(c.Key)).ToList();
        if (arrayChildren.Count > 0)
            return arrayChildren.Select(c => ParseArrayEntry(c, logger)).Where(c => c != null).Cast<ChannelConfigurationEntry>().ToList();

        return children.Select(c => ParseLegacyEntry(c, logger)).Where(c => c != null).Cast<ChannelConfigurationEntry>().ToList();
    }

    public static JsonArray GetOrNormalizeCanonicalArray(JsonObject root)
    {
        if (root["Channels"] is JsonArray existingArray)
            return existingArray;

        var canonical = new JsonArray();
        if (root["Channels"] is JsonObject legacyObject)
        {
            foreach (var (key, value) in legacyObject)
            {
                if (value is not JsonObject entry)
                    continue;

                var normalized = new JsonObject();
                var type = entry["Type"]?.GetValue<string>() ?? InferTypeFromLegacyKey(key);
                if (string.IsNullOrWhiteSpace(type))
                    continue;

                normalized["Type"] = type;

                // The legacy key is the only stable id these entries have; keep it as Name so
                // subscriptions written against it survive the move to the array form.
                if (entry[ChannelConfigurationEntry.NameKey] == null)
                    normalized[ChannelConfigurationEntry.NameKey] = key;

                foreach (var (configKey, configValue) in entry)
                {
                    if (configValue == null)
                        continue;
                    normalized[configKey] = configValue.DeepClone();
                }

                canonical.Add(normalized);
            }
        }

        root["Channels"] = canonical;
        return canonical;
    }

    /// <summary>
    /// Folds an array-shaped <c>Subscribe</c> into the flat string config the rest of the pipeline
    /// speaks.
    /// <para>
    /// This is not optional tidying. <see cref="IConfiguration"/> flattens
    /// <c>"Subscribe": ["a.&gt;", "b.&gt;"]</c> into a section whose own <c>Value</c> is null and
    /// whose filters are numerically-keyed children — so the <c>Value != null</c> pass that builds
    /// the config dictionary drops it on the floor, with no warning and no filters. The array form
    /// is the one an operator will reach for first, so it has to work rather than silently mean
    /// "catch-all".
    /// </para>
    /// </summary>
    private static void NormalizeSubscriptions(IConfigurationSection entry, Dictionary<string, string> config)
    {
        foreach (var key in ChannelConfigurationEntry.SubscribeKeys)
        {
            // Scalar form — "Subscribe": "trading.>, hitl.>" — is already in the dictionary.
            if (config.TryGetValue(key, out var scalar) && !string.IsNullOrWhiteSpace(scalar))
            {
                config[ChannelConfigurationEntry.SubscribeKey] = scalar.Trim();
                return;
            }

            var items = entry.GetSection(key).GetChildren()
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim())
                .ToList();

            if (items.Count == 0)
                continue;

            config[ChannelConfigurationEntry.SubscribeKey] = string.Join(", ", items);
            return;
        }
    }

    private static ChannelConfigurationEntry? ParseArrayEntry(IConfigurationSection entry, ILogger? logger)
    {
        var config = entry.GetChildren()
            .Where(c => c.Value != null)
            .ToDictionary(c => c.Key, c => c.Value!, StringComparer.OrdinalIgnoreCase);

        NormalizeSubscriptions(entry, config);

        if (!config.TryGetValue("Type", out var type) || string.IsNullOrWhiteSpace(type))
        {
            logger?.LogWarning("Channels[{Index}]: missing 'Type' - skipping.", entry.Key);
            return null;
        }

        if (config.TryGetValue("Enabled", out var enabledStr)
            && bool.TryParse(enabledStr, out var enabled)
            && !enabled)
        {
            return null;
        }

        return new ChannelConfigurationEntry
        {
            Key = entry.Key,
            Type = type.Trim().ToLowerInvariant(),
            Config = config
        };
    }

    private static ChannelConfigurationEntry? ParseLegacyEntry(IConfigurationSection entry, ILogger? logger)
    {
        var config = entry.GetChildren()
            .Where(c => c.Value != null)
            .ToDictionary(c => c.Key, c => c.Value!, StringComparer.OrdinalIgnoreCase);

        NormalizeSubscriptions(entry, config);

        if (config.TryGetValue("Enabled", out var enabledStr)
            && bool.TryParse(enabledStr, out var enabled)
            && !enabled)
        {
            return null;
        }

        // The legacy shape keys entries by name ("telegram_main"), which is exactly the stable id
        // the canonical array form has to spell out — carry it over rather than losing it.
        if (!config.ContainsKey(ChannelConfigurationEntry.NameKey) && !string.IsNullOrWhiteSpace(entry.Key))
            config[ChannelConfigurationEntry.NameKey] = entry.Key;

        var type = config.TryGetValue("Type", out var explicitType) && !string.IsNullOrWhiteSpace(explicitType)
            ? explicitType
            : InferTypeFromLegacyKey(entry.Key);

        if (string.IsNullOrWhiteSpace(type))
        {
            logger?.LogWarning("Channels:{Key}: could not infer channel type - skipping.", entry.Key);
            return null;
        }

        config["Type"] = type;
        return new ChannelConfigurationEntry
        {
            Key = entry.Key,
            Type = type.Trim().ToLowerInvariant(),
            Config = config
        };
    }

    public static string InferTypeFromLegacyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        var chars = key.TakeWhile(char.IsLetter).ToArray();
        if (chars.Length == 0)
            return string.Empty;

        return new string(chars);
    }
}
