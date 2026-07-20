using AgentFox.Plugins.Interfaces;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentFox.Plugins.Research;

namespace AgentFox.TavilySearch;

/// <summary>
/// AgentFox plugin module that exposes the <see cref="TavilySearchTool"/> (tavily_search).
///
/// Registers the shared REST provider and tool only when a Tavily key is configured — either the
/// "TAVILY_API_KEY" environment variable or a "Plugins:Tavily:ApiKey" config value. Without a key
/// it logs a hint and stays inert, letting the plugin ship without breaking unconfigured installs.
/// </summary>
public sealed class TavilySearchModule : IAgentAwareModule
{
    private IServiceProvider? _services;

    public string Name => "tavily-search";

    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        var apiKey = TavilyWebSearchProvider.ResolveApiKey(config);
        if (!string.IsNullOrWhiteSpace(apiKey))
            services.AddSingleton<IWebSearchProvider, TavilyWebSearchProvider>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }

    public Task StartAsync(IServiceProvider services)
    {
        _services = services;
        return Task.CompletedTask;
    }

    public Task OnAgentReadyAsync(IPluginContext context)
    {
        var logger = _services!.GetRequiredService<ILoggerFactory>().CreateLogger<TavilySearchModule>();

        var provider = _services!.GetService<IWebSearchProvider>();
        if (provider is null)
        {
            logger.LogInformation(
                "[TavilySearch] tavily_search tool not registered — set TAVILY_API_KEY or " +
                "\"Plugins:Tavily:ApiKey\" to enable it.");
            return Task.CompletedTask;
        }

        context.RegisterTool(new TavilySearchTool(provider));
        context.ContributeToSystemPrompt(
            contributorId: "tavily-search",
            fragmentProvider: () =>
                "You have a 'tavily_search' tool that queries the Tavily Search API for current web results.");

        logger.LogInformation("[TavilySearch] tavily_search tool registered.");
        return Task.CompletedTask;
    }
}
