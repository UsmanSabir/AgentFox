using AgentFox.Plugins.Interfaces;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentFox.BraveSearch;

/// <summary>
/// AgentFox plugin module that exposes the <see cref="BraveSearchTool"/> (brave_search).
///
/// Brave Search is a key-only API (no free tier), and <see cref="BraveSearchTool"/> throws
/// from its constructor when no key is present. So this module registers the tool only when a
/// "BRAVE_SEARCH_API_KEY" or "Plugins:BraveSearch:ApiKey" is configured; otherwise it stays inert, which lets the
/// plugin ship enabled-by-default without crashing installs that have not set a key.
/// </summary>
public sealed class BraveSearchModule : IAgentAwareModule
{
    private IServiceProvider? _services;

    public string Name => "brave-search";

    public void RegisterServices(IServiceCollection services, IConfiguration config) { }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }

    public Task StartAsync(IServiceProvider services)
    {
        _services = services;
        return Task.CompletedTask;
    }

    public Task OnAgentReadyAsync(IPluginContext context)
    {
        var config = _services!.GetRequiredService<IConfiguration>();
        var logger = _services!.GetRequiredService<ILoggerFactory>().CreateLogger<BraveSearchModule>();

        if (string.IsNullOrWhiteSpace(BraveSearchTool.ResolveApiKey(config)))
        {
            logger.LogInformation(
                "[BraveSearch] brave_search tool not registered — set BRAVE_SEARCH_API_KEY or " +
                "\"Plugins:BraveSearch:ApiKey\" to enable it.");
            return Task.CompletedTask;
        }

        context.RegisterTool(new BraveSearchTool(config));
        context.ContributeToSystemPrompt(
            contributorId: "brave-search",
            fragmentProvider: () =>
                "You have a 'brave_search' tool that queries the Brave Search API for current web results.");

        logger.LogInformation("[BraveSearch] brave_search tool registered.");
        return Task.CompletedTask;
    }
}
