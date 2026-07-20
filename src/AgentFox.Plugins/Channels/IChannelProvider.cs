using Microsoft.Extensions.Logging;

namespace AgentFox.Plugins.Channels;

public interface IChannelProvider
{
    string ChannelType { get; }
    string DisplayName { get; }

    IReadOnlyDictionary<string, ChannelConfigField> GetConfigSchema();

    (Channel? Channel, string? Error) Create(
        Dictionary<string, string> config,
        ChannelCreationContext context);
}

public sealed class ChannelConfigField
{
    public required string Description { get; init; }
    public bool Required { get; init; }
}

public sealed class ChannelCreationContext
{
    public required ILoggerFactory LoggerFactory { get; init; }
    public required IServiceProvider Services { get; init; }
    public required string WorkspacePath { get; init; }
}
