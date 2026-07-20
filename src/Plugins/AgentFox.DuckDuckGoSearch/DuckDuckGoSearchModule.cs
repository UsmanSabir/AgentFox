using AgentFox.Plugins.Interfaces;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentFox.DuckDuckGoSearch;

/// <summary>
/// AgentFox plugin module that exposes the <see cref="DuckDuckGoTool"/> (duckduckgo_search).
///
/// DuckDuckGo's Instant Answer API needs no key. It is useful for sourced abstracts and related
/// topics, but it is not advertised as a full current-web search provider.
/// </summary>
public sealed class DuckDuckGoSearchModule : IAgentAwareModule
{
    private IServiceProvider? _services;

    public string Name => "duckduckgo-search";

    public void RegisterServices(IServiceCollection services, IConfiguration config) { }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }

    public Task StartAsync(IServiceProvider services)
    {
        _services = services;
        return Task.CompletedTask;
    }

    public Task OnAgentReadyAsync(IPluginContext context)
    {
        var logger = _services!.GetRequiredService<ILoggerFactory>().CreateLogger<DuckDuckGoSearchModule>();

        context.RegisterTool(new DuckDuckGoTool());
        context.ContributeToSystemPrompt(
            contributorId: "duckduckgo-search",
            fragmentProvider: () =>
                "You have a 'duckduckgo_search' tool for DuckDuckGo Instant Answers. " +
                "It is not a full current-web search; prefer Tavily, Brave, or browse_web for current information.");

        logger.LogInformation("[DuckDuckGoSearch] duckduckgo_search tool registered.");
        return Task.CompletedTask;
    }
}
