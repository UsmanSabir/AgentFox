using AgentFox.Plugins.Interfaces;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PageAgent.Config;
using PageAgent.Core;
using PageAgent.Tools;

namespace PageAgent;

/// <summary>
/// AgentFox plugin module that registers the autonomous web-browsing agent.
///
/// What this module does:
///   1. Binds <see cref="BrowserAgentOptions"/> from appsettings.json → "PageAgent" section.
///   2. After the FoxAgent is built, registers the <see cref="BrowseWebTool"/> into its
///      tool registry so the LLM can call it.
///   3. Contributes a one-line fragment to the system prompt advertising the tool.
///
/// Minimal appsettings.json configuration (all fields optional, defaults shown):
/// <code>
/// "PageAgent": {
///   "Headless": true,
///   "MaxSteps": 15,
///   "MaxExtractLength": 3000,
///   "MaxLinks": 20,
///   "MaxHeadings": 10,
///   "NavigationTimeoutMs": 30000,
///   "RetryAttempts": 2,
///   "RunTimeoutMinutes": 5,
///   "SearchEngineUrl": "https://www.google.com/search?q=",
///   "ViewportWidth": 1280,
///   "ViewportHeight": 900
/// }
/// </code>
/// </summary>
public sealed class BrowserAgentModule : IAgentAwareModule
{
    private IServiceProvider? _services;

    public string Name => "page-agent";

    // ── IAppModule ────────────────────────────────────────────────────────────

    public void RegisterServices(IServiceCollection services, IConfiguration config)
    {
        services.Configure<BrowserAgentOptions>(
            config.GetSection($"Plugins:{BrowserAgentOptions.SectionName}"));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No HTTP endpoints for this module
    }

    public Task StartAsync(IServiceProvider services)
    {
        _services = services;
        return Task.CompletedTask;
    }

    // ── IAgentAwareModule ─────────────────────────────────────────────────────

    public Task OnAgentReadyAsync(IPluginContext context)
    {
        var chatClient  = _services!.GetRequiredService<IChatClient>();
        var options     = _services!.GetRequiredService<IOptions<BrowserAgentOptions>>();
        var loggers     = _services!.GetRequiredService<ILoggerFactory>();

        // Register the tool into the agent's tool registry
        var tool = new BrowseWebTool(chatClient, options, loggers);
        context.RegisterTool(tool);

        // Advertise the tool capability in the system prompt
        context.ContributeToSystemPrompt(
            contributorId: "page-agent",
            fragmentProvider: () =>
                "You have a 'browse_web' tool that autonomously navigates the internet " +
                "to research topics, read documentation, and extract current information. " +
                "Use it whenever the user needs up-to-date web content.");

        var logger = loggers.CreateLogger<BrowserAgentModule>();
        logger.LogInformation(
            "[PageAgent] browse_web tool registered. Browser: {Browser}, MaxSteps: {MaxSteps}",
            BrowserSystemDetector.ExecutablePath is { } path
                ? System.IO.Path.GetFileName(path)
                : "Chromium (will download)",
            options.Value.MaxSteps);

        return Task.CompletedTask;
    }
}
