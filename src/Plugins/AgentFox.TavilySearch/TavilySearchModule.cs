using AgentFox.Plugins.Interfaces;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentFox.TavilySearch;

/// <summary>
/// AgentFox plugin module that exposes the <see cref="TavilySearchTool"/> (tavily_search).
///
/// <see cref="TavilySearchTool"/> throws from its constructor when no key is present, so this
/// module registers the tool only when a Tavily key is configured — either the "TAVILY_API_KEY"
/// environment variable or a "Tavily:ApiKey" config value. Without a key it logs a hint and stays
/// inert, letting the plugin ship enabled-by-default without crashing unconfigured installs.
/// </summary>
public sealed class TavilySearchModule : IAgentAwareModule
{
    private IServiceProvider? _services;

    public string Name => "tavily-search";

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
        var logger = _services!.GetRequiredService<ILoggerFactory>().CreateLogger<TavilySearchModule>();

        var apiKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY") ?? config["Tavily:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogInformation(
                "[TavilySearch] tavily_search tool not registered — set TAVILY_API_KEY or \"Tavily:ApiKey\" to enable it.");
            return Task.CompletedTask;
        }

        context.RegisterTool(new TavilySearchTool(config));
        context.ContributeToSystemPrompt(
            contributorId: "tavily-search",
            fragmentProvider: () =>
                "You have a 'tavily_search' tool that queries the Tavily Search API for current web results.");

        logger.LogInformation("[TavilySearch] tavily_search tool registered.");
        return Task.CompletedTask;
    }
}
