namespace AgentFox.Plugins;

/// <summary>Supplies UI-safe, typed runtime configuration metadata for a plugin.</summary>
public interface IPluginConfigDefinitionProvider
{
    IEnumerable<PluginConfigDefinition> GetDefinitions();
}

public sealed class PluginConfigDefinition
{
    public required string PluginName { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<PluginConfigFieldDefinition> Fields { get; init; } = [];
}

public sealed class PluginConfigFieldDefinition
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Type { get; init; } = "string";
    public object? DefaultValue { get; init; }
    public IReadOnlyList<string> Options { get; init; } = [];
    public bool Sensitive { get; init; }
    public bool RuntimeEditable { get; init; } = true;
}
