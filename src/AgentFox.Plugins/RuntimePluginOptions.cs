using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentFox.Plugins;

/// <summary>
/// Live view of a plugin's options: the appsettings baseline (<see cref="IOptions{T}"/>) with the
/// <see cref="PluginConfigManager"/> runtime overlay applied on top. Unlike <c>IOptions&lt;T&gt;.Value</c>,
/// <see cref="Current"/> reflects browser-made config changes immediately — read it at use time,
/// never capture it at construction.
/// </summary>
public interface IRuntimePluginOptions<out T> where T : class
{
    T Current { get; }
}

/// <summary>
/// Overlay keys are matched to writable public properties of <typeparamref name="T"/>
/// case-insensitively; keys that don't match (e.g. fields belonging to another options class that
/// shares the same plugin config) are ignored. Null and empty-string overlay values are also
/// ignored, so clearing a field in the browser falls back to the appsettings baseline.
/// The built value is cached and invalidated via <see cref="PluginConfigManager.OnConfigChanged"/>.
/// </summary>
public sealed class RuntimePluginOptions<T> : IRuntimePluginOptions<T> where T : class
{
    private static readonly JsonSerializerOptions OverlayJsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly string _pluginName;
    private readonly IOptions<T> _baseline;
    private readonly PluginConfigManager _configManager;
    private readonly ILogger? _logger;
    private volatile T? _cached;

    public RuntimePluginOptions(
        string pluginName,
        IOptions<T> baseline,
        PluginConfigManager configManager,
        ILogger<RuntimePluginOptions<T>>? logger = null)
    {
        _pluginName = pluginName;
        _baseline = baseline;
        _configManager = configManager;
        _logger = logger;

        _configManager.OnConfigChanged(pluginName, () =>
        {
            _cached = null;
            return Task.CompletedTask;
        });
    }

    public T Current => _cached ??= Build();

    private T Build()
    {
        // JSON round-trip clone so overlay values never mutate the shared IOptions singleton.
        var clone = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(_baseline.Value))
            ?? throw new InvalidOperationException($"Failed to clone baseline options {typeof(T).Name}.");

        var overlay = _configManager.GetConfig(_pluginName);
        if (overlay.Count == 0)
            return clone;

        var properties = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in overlay)
        {
            if (value is null || (value is string s && string.IsNullOrWhiteSpace(s)))
                continue;
            if (!properties.TryGetValue(key, out var property))
                continue;

            try
            {
                var element = JsonSerializer.SerializeToElement(value);
                property.SetValue(clone, element.Deserialize(property.PropertyType, OverlayJsonOptions));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "[{Plugin}] Ignoring runtime config value '{Key}': cannot convert to {Type}",
                    _pluginName, key, property.PropertyType.Name);
            }
        }

        return clone;
    }
}

public static class RuntimePluginOptionsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IRuntimePluginOptions{T}"/> overlaying the runtime config stored under
    /// <paramref name="pluginName"/> onto the already-configured <see cref="IOptions{T}"/> baseline.
    /// </summary>
    public static IServiceCollection AddRuntimePluginOptions<T>(
        this IServiceCollection services, string pluginName) where T : class
    {
        services.AddSingleton<IRuntimePluginOptions<T>>(sp => new RuntimePluginOptions<T>(
            pluginName,
            sp.GetRequiredService<IOptions<T>>(),
            sp.GetRequiredService<PluginConfigManager>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<RuntimePluginOptions<T>>()));
        return services;
    }
}
