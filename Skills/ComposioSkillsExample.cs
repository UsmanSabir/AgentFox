using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentFox.Skills;
using AgentFox.Tools;
using Microsoft.Extensions.Logging;

namespace AgentFox.Examples;

/// <summary>
/// Example usage of Composio.dev skills integration in AgentFox
/// </summary>
public class ComposioSkillsExample
{
    private readonly ILogger<ComposioSkillsExample> _logger;

    public ComposioSkillsExample(ILogger<ComposioSkillsExample> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Example 1: Initialize Composio skills with all available integrations
    /// </summary>
    public async Task Example1_InitializeAllIntegrationsAsync()
    {
        _logger.LogInformation("Example 1: Initialize all Composio integrations");

        // Create skill registry and tool registry
        var toolRegistry = new ToolRegistry();
        var skillRegistry = new SkillRegistry(toolRegistry, logger: _logger as ILogger<SkillRegistry>);

        // Get API key from environment or configuration
        var composioApiKey = Environment.GetEnvironmentVariable("COMPOSIO_API_KEY") 
            ?? throw new InvalidOperationException("COMPOSIO_API_KEY environment variable not set");

        // Register all Composio.dev integrations
        await skillRegistry.RegisterComposioSkillsAsync(composioApiKey);

        var allSkills = skillRegistry.GetAll();
        _logger.LogInformation("Registered {Count} skills total", allSkills.Count);

        foreach (var skill in allSkills)
        {
            var tools = skill.GetTools();
            _logger.LogInformation("  - {SkillName}: {ToolCount} tools", skill.Name, tools.Count);
        }
    }

    /// <summary>
    /// Example 2: Initialize with specific integrations only
    /// </summary>
    public async Task Example2_InitializeSpecificIntegrationsAsync()
    {
        _logger.LogInformation("Example 2: Initialize specific integrations");

        var toolRegistry = new ToolRegistry();
        var skillRegistry = new SkillRegistry(toolRegistry, logger: _logger as ILogger<SkillRegistry>);

        var composioApiKey = Environment.GetEnvironmentVariable("COMPOSIO_API_KEY")
            ?? throw new InvalidOperationException("COMPOSIO_API_KEY environment variable not set");

        // Only register GitHub, Slack, and Jira integrations
        var integrationIds = new[] { "github", "slack", "jira" };
        await skillRegistry.RegisterComposioSkillsAsync(composioApiKey, filterIntegrationIds: integrationIds);

        _logger.LogInformation("Specific integrations registered");
    }

    /// <summary>
    /// Example 3: Use the Composio skill provider directly
    /// </summary>
    public async Task Example3_UseSkillProviderDirectlyAsync()
    {
        _logger.LogInformation("Example 3: Use Composio skill provider directly");

        var toolRegistry = new ToolRegistry();
        var skillRegistry = new SkillRegistry(toolRegistry, logger: _logger as ILogger<SkillRegistry>);

        var composioApiKey = Environment.GetEnvironmentVariable("COMPOSIO_API_KEY")
            ?? throw new InvalidOperationException("COMPOSIO_API_KEY environment variable not set");

        // Create the provider
        var composioProvider = new ComposioSkillProvider(
            apiKey: composioApiKey,
            skillRegistry: skillRegistry,
            logger: _logger as ILogger<ComposioSkillProvider>
        );

        // Get available integrations before initializing
        var availableIntegrations = await composioProvider.GetAvailableIntegrationsAsync();
        _logger.LogInformation("Available integrations from Composio.dev:");

        foreach (var integration in availableIntegrations.Take(10))
        {
            _logger.LogInformation("  - {Name} ({Category}): {ActionsCount} actions",
                integration.Name,
                integration.Category ?? "N/A",
                integration.ActionsCount
            );
        }

        // Initialize (register all)
        await composioProvider.InitializeAsync();

        // Get all registered skills
        var allComposioSkills = composioProvider.GetAllSkills();
        _logger.LogInformation("Registered {Count} Composio skills", allComposioSkills.Count);
    }

    /// <summary>
    /// Example 4: Search for specific integrations
    /// </summary>
    public async Task Example4_SearchIntegrationsAsync()
    {
        _logger.LogInformation("Example 4: Search integrations");

        var toolRegistry = new ToolRegistry();
        var skillRegistry = new SkillRegistry(toolRegistry, logger: _logger as ILogger<SkillRegistry>);

        var composioApiKey = Environment.GetEnvironmentVariable("COMPOSIO_API_KEY")
            ?? throw new InvalidOperationException("COMPOSIO_API_KEY environment variable not set");

        var composioProvider = new ComposioSkillProvider(
            apiKey: composioApiKey,
            skillRegistry: skillRegistry,
            logger: _logger as ILogger<ComposioSkillProvider>
        );

        // Search for GitHub integrations
        var githubIntegrations = await composioProvider.SearchIntegrationsAsync(name: "github");
        _logger.LogInformation("Found {Count} integrations matching 'github'", githubIntegrations.Count);

        foreach (var integration in githubIntegrations)
        {
            _logger.LogInformation("  - {Name}: {Description}", integration.Name, integration.Description);
        }

        // Search by category
        var communicationTools = await composioProvider.SearchIntegrationsAsync(category: "communication");
        _logger.LogInformation("Found {Count} communication integrations", communicationTools.Count);
    }

    /// <summary>
    /// Example 5: Get and examine actions for an integration
    /// </summary>
    public async Task Example5_ExamineIntegrationActionsAsync()
    {
        _logger.LogInformation("Example 5: Examine integration actions");

        var toolRegistry = new ToolRegistry();
        var skillRegistry = new SkillRegistry(toolRegistry, logger: _logger as ILogger<SkillRegistry>);

        var composioApiKey = Environment.GetEnvironmentVariable("COMPOSIO_API_KEY")
            ?? throw new InvalidOperationException("COMPOSIO_API_KEY environment variable not set");

        var composioProvider = new ComposioSkillProvider(
            apiKey: composioApiKey,
            skillRegistry: skillRegistry,
            logger: _logger as ILogger<ComposioSkillProvider>
        );

        // Get actions for GitHub integration
        try
        {
            var actions = await composioProvider.GetActionsAsync("github");
            _logger.LogInformation("GitHub integration has {Count} actions:", actions.Count);

            // Show first 5 actions with details
            foreach (var action in actions.Take(5))
            {
                _logger.LogInformation("  Action: {DisplayName}", action.DisplayName);
                _logger.LogInformation("    Description: {Description}", action.Description);
                _logger.LogInformation("    Input Parameters:");

                foreach (var param in action.InputParams.Take(3))
                {
                    var required = param.Required ? "[REQUIRED]" : "[optional]";
                    _logger.LogInformation("      - {Name} ({Type}) {Required}: {Description}",
                        param.Name,
                        param.Type,
                        required,
                        param.Description
                    );
                }

                if (action.InputParams.Count > 3)
                {
                    _logger.LogInformation("      ... and {Count} more parameters",
                        action.InputParams.Count - 3
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get actions for GitHub integration");
        }
    }

    /// <summary>
    /// Example 6: Execute a Composio action directly
    /// </summary>
    public async Task Example6_ExecuteComposioActionAsync()
    {
        _logger.LogInformation("Example 6: Execute Composio action");

        var toolRegistry = new ToolRegistry();
        var skillRegistry = new SkillRegistry(toolRegistry, logger: _logger as ILogger<SkillRegistry>);

        var composioApiKey = Environment.GetEnvironmentVariable("COMPOSIO_API_KEY")
            ?? throw new InvalidOperationException("COMPOSIO_API_KEY environment variable not set");

        var composioProvider = new ComposioSkillProvider(
            apiKey: composioApiKey,
            skillRegistry: skillRegistry,
            logger: _logger as ILogger<ComposioSkillProvider>
        );

        try
        {
            // Execute an action on GitHub (example: get repository info)
            // This requires proper authentication setup in Composio.dev
            var result = await composioProvider.ExecuteActionAsync(
                integrationId: "github",
                actionId: "get_repository",
                parameters: new Dictionary<string, object>
                {
                    { "owner", "your-github-username" },
                    { "repo", "your-repo-name" }
                }
            );

            _logger.LogInformation("Action result: {Result}",
                System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute action (this is expected if authentication is not set up)");
        }
    }

    /// <summary>
    /// Example 7: Configuration for Program.cs
    /// </summary>
    public static void ConfigureComposioInProgram(/* IServiceCollection services, IConfiguration config */)
    {
        // This shows how to set up Composio in your Program.cs:
        /*
        // 1. In your appsettings.json:
        {
          "Composio": {
            "ApiKey": "your-composio-api-key",
            "Integrations": ["github", "slack", "jira"]  // optional: specific integrations
          }
        }

        // 2. In Program.cs:
        var composioApiKey = configuration["Composio:ApiKey"] 
            ?? throw new InvalidOperationException("Composio API key not configured");

        var skillRegistry = new SkillRegistry(toolRegistry, promptBuilder, loggerFactory.CreateLogger<SkillRegistry>());

        // Register Composio skills (optionally with specific integrations)
        var integrationIds = configuration.GetSection("Composio:Integrations")
            .Get<List<string>>() ?? new();

        if (integrationIds.Any())
        {
            await skillRegistry.RegisterComposioSkillsAsync(composioApiKey, filterIntegrationIds: integrationIds);
        }
        else
        {
            await skillRegistry.RegisterComposioSkillsAsync(composioApiKey);
        }

        services.AddSingleton(skillRegistry);
        */
    }
}

/// <summary>
/// Configuration for Composio integrations
/// </summary>
public record ComposioConfiguration
{
    /// <summary>
    /// Composio.dev API key
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional: specific integration IDs to enable
    /// Leave empty to enable all available integrations
    /// </summary>
    public List<string> Integrations { get; set; } = new();

    /// <summary>
    /// Enable detailed logging for Composio operations
    /// </summary>
    public bool EnableDetailedLogging { get; set; }

    /// <summary>
    /// Cache timeout in seconds for integration metadata
    /// </summary>
    public int CacheTimeoutSeconds { get; set; } = 3600;
}
