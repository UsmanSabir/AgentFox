using AgentFox.Plugins.Channels;
using AgentFox.Tools;
using Microsoft.Extensions.Logging;

namespace AgentFox.Channels;

public sealed class ChannelProviderCatalog
{
    private readonly Dictionary<string, IChannelProvider> _providers;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;
    private readonly WorkspaceManager _workspaceManager;

    public ChannelProviderCatalog(
        IEnumerable<IChannelProvider> providers,
        ILoggerFactory loggerFactory,
        IServiceProvider services,
        WorkspaceManager workspaceManager)
    {
        _providers = providers.ToDictionary(
            p => p.ChannelType,
            StringComparer.OrdinalIgnoreCase);
        _loggerFactory = loggerFactory;
        _services = services;
        _workspaceManager = workspaceManager;
    }

    public IReadOnlyList<string> SupportedTypes =>
        _providers.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyCollection<IChannelProvider> Providers =>
        _providers.Values.OrderBy(x => x.ChannelType, StringComparer.OrdinalIgnoreCase).ToList();

    public bool TryGetProvider(string channelType, out IChannelProvider? provider) =>
        _providers.TryGetValue(channelType, out provider);

    public IReadOnlyDictionary<string, ChannelConfigField>? GetConfigSchema(string channelType) =>
        _providers.TryGetValue(channelType, out var provider)
            ? provider.GetConfigSchema()
            : null;

    public (Channel? Channel, string? Error) Create(
        string channelType,
        Dictionary<string, string> config,
        string? workspacePath = null)
    {
        if (!_providers.TryGetValue(channelType, out var provider))
            return (null, $"Unknown channel type '{channelType}'. Supported: {string.Join(", ", SupportedTypes)}");

        var context = new ChannelCreationContext
        {
            LoggerFactory = _loggerFactory,
            Services = _services,
            WorkspacePath = string.IsNullOrWhiteSpace(workspacePath)
                ? _workspaceManager.ResolvePath("")
                : workspacePath
        };

        return provider.Create(config, context);
    }
}
