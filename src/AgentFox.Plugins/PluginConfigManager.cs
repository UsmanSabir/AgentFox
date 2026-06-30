using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AgentFox.Plugins;

/// <summary>
/// Manages per-plugin configuration with runtime updates.
/// Persists settings to disk and notifies system prompt contributors of config changes.
/// </summary>
public class PluginConfigManager
{
    private readonly string _configDirectory;
    private readonly ILogger<PluginConfigManager> _logger;
    private readonly ConcurrentDictionary<string, PluginConfigData> _configs = new();
    private readonly ConcurrentDictionary<string, List<Func<Task>>> _configChangeListeners = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public PluginConfigManager(string configDirectory, ILogger<PluginConfigManager> logger)
    {
        _configDirectory = configDirectory;
        _logger = logger;

        // Ensure config directory exists
        Directory.CreateDirectory(configDirectory);

        // Load any existing configs from disk
        LoadAllConfigs();
    }

    /// <summary>Load all plugin configs from disk.</summary>
    private void LoadAllConfigs()
    {
        try
        {
            if (!Directory.Exists(_configDirectory))
                return;

            var configFiles = Directory.GetFiles(_configDirectory, "*.plugin-config.json");
            foreach (var file in configFiles)
            {
                try
                {
                    var content = File.ReadAllText(file);
                    var data = JsonSerializer.Deserialize<PluginConfigData>(content, JsonOptions);
                    if (data?.PluginName != null)
                    {
                        // Deserialized values arrive as JsonElement; collapse to CLR primitives
                        // so consumers can pattern-match (e.g. `is bool`/`is string`).
                        data.Config = NormalizeConfig(data.Config);
                        _configs.TryAdd(data.PluginName, data);
                        _logger.LogDebug("Loaded plugin config: {Plugin}", data.PluginName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load plugin config from {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load plugin configs");
        }
    }

    /// <summary>
    /// System.Text.Json deserialization (from disk) and ASP.NET request binding both produce
    /// <see cref="JsonElement"/> values inside a <c>Dictionary&lt;string, object?&gt;</c>, never
    /// primitive CLR types. Consumers pattern-match on bool/string/number, so collapse each
    /// JsonElement to its underlying primitive before the config is stored or handed out.
    /// </summary>
    private static Dictionary<string, object?> NormalizeConfig(Dictionary<string, object?> config)
    {
        var result = new Dictionary<string, object?>(config.Count);
        foreach (var kv in config)
            result[kv.Key] = NormalizeValue(kv.Value);
        return result;
    }

    private static object? NormalizeValue(object? value)
    {
        if (value is not JsonElement je)
            return value;

        return je.ValueKind switch
        {
            JsonValueKind.True   => true,
            JsonValueKind.False  => false,
            JsonValueKind.String => je.GetString(),
            JsonValueKind.Null   => null,
            JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
            JsonValueKind.Array  => je.EnumerateArray().Select(e => NormalizeValue(e)).ToList(),
            JsonValueKind.Object => je.EnumerateObject().ToDictionary(p => p.Name, p => NormalizeValue(p.Value)),
            _                    => je.ToString()
        };
    }

    /// <summary>Get current configuration for a plugin.</summary>
    public Dictionary<string, object?> GetConfig(string pluginName)
    {
        if (_configs.TryGetValue(pluginName, out var data))
            return data.Config;

        return new();
    }

    /// <summary>Get configuration with schema (includes default values and descriptions).</summary>
    public PluginConfigResponse GetConfigWithSchema(string pluginName)
    {
        var config = GetConfig(pluginName);
        return new PluginConfigResponse
        {
            PluginName = pluginName,
            Config = config,
            LastUpdatedAt = _configs.TryGetValue(pluginName, out var data) ? data.LastUpdatedAt : DateTimeOffset.UtcNow,
            IsDefault = !_configs.ContainsKey(pluginName)
        };
    }

    /// <summary>Update plugin configuration and persist to disk. Triggers change listeners.</summary>
    public async Task<bool> SaveConfigAsync(string pluginName, Dictionary<string, object?> config)
    {
        try
        {
            var data = new PluginConfigData
            {
                PluginName = pluginName,
                // ASP.NET request binding also yields JsonElement values; normalize so the
                // in-memory copy (and the next GetConfig) exposes plain CLR primitives.
                Config = NormalizeConfig(config),
                LastUpdatedAt = DateTimeOffset.UtcNow
            };

            _configs.AddOrUpdate(pluginName, data, (_, _) => data);

            var filePath = Path.Combine(_configDirectory, $"{pluginName}.plugin-config.json");
            var json = JsonSerializer.Serialize(data, JsonOptions);
            await File.WriteAllTextAsync(filePath, json);

            _logger.LogInformation("[{Plugin}] Configuration updated and persisted", pluginName);

            // Notify listeners of config change
            await TriggerConfigChangeListenersAsync(pluginName);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save plugin config for {Plugin}", pluginName);
            return false;
        }
    }

    /// <summary>Merge updates into existing config and persist.</summary>
    public async Task<bool> MergeConfigAsync(string pluginName, Dictionary<string, object?> updates)
    {
        var current = GetConfig(pluginName);
        foreach (var kv in updates)
            current[kv.Key] = kv.Value;

        return await SaveConfigAsync(pluginName, current);
    }

    /// <summary>Register a callback to invoke when config for a plugin changes.</summary>
    public void OnConfigChanged(string pluginName, Func<Task> callback)
    {
        var listeners = _configChangeListeners.GetOrAdd(pluginName, _ => new());
        listeners.Add(callback);
        _logger.LogDebug("[{Plugin}] Registered config change listener", pluginName);
    }

    /// <summary>Remove a plugin's configuration and delete from disk.</summary>
    public bool DeleteConfig(string pluginName)
    {
        try
        {
            _configs.TryRemove(pluginName, out _);
            var filePath = Path.Combine(_configDirectory, $"{pluginName}.plugin-config.json");
            if (File.Exists(filePath))
                File.Delete(filePath);

            _logger.LogInformation("[{Plugin}] Configuration deleted", pluginName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete plugin config for {Plugin}", pluginName);
            return false;
        }
    }

    /// <summary>Invoke all registered config change listeners for a plugin.</summary>
    private async Task TriggerConfigChangeListenersAsync(string pluginName)
    {
        if (!_configChangeListeners.TryGetValue(pluginName, out var listeners))
            return;

        foreach (var listener in listeners)
        {
            try
            {
                await listener();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Config change listener failed for {Plugin}", pluginName);
            }
        }
    }

    /// <summary>Get all plugin configs.</summary>
    public IEnumerable<PluginConfigResponse> GetAllConfigs()
    {
        return _configs.Values.Select(data => new PluginConfigResponse
        {
            PluginName = data.PluginName,
            Config = data.Config,
            LastUpdatedAt = data.LastUpdatedAt,
            IsDefault = false
        });
    }
}

/// <summary>Persisted plugin config data.</summary>
public class PluginConfigData
{
    [JsonPropertyName("pluginName")]
    public string PluginName { get; set; } = "";

    [JsonPropertyName("config")]
    public Dictionary<string, object?> Config { get; set; } = new();

    [JsonPropertyName("lastUpdatedAt")]
    public DateTimeOffset LastUpdatedAt { get; set; }
}

/// <summary>Response model for config endpoints.</summary>
public class PluginConfigResponse
{
    public string PluginName { get; set; } = "";
    public Dictionary<string, object?> Config { get; set; } = new();
    public DateTimeOffset LastUpdatedAt { get; set; }
    public bool IsDefault { get; set; }
}

/// <summary>Request model for config updates.</summary>
public class PluginConfigUpdateRequest
{
    public Dictionary<string, object?> Config { get; set; } = new();
    public bool Merge { get; set; } = true;
}
