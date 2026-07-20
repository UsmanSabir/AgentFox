using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentFox.Channels;

public sealed class ChannelConfigurationEntry
{
    public required string Key { get; init; }
    public required string Type { get; init; }
    public required Dictionary<string, string> Config { get; init; }

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

    private static ChannelConfigurationEntry? ParseArrayEntry(IConfigurationSection entry, ILogger? logger)
    {
        var config = entry.GetChildren()
            .Where(c => c.Value != null)
            .ToDictionary(c => c.Key, c => c.Value!, StringComparer.OrdinalIgnoreCase);

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

        if (config.TryGetValue("Enabled", out var enabledStr)
            && bool.TryParse(enabledStr, out var enabled)
            && !enabled)
        {
            return null;
        }

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
