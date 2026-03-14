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
    /// Example 3a: Initialize with only authorized toolkits from auth configs
    /// </summary>
    public async Task Example3a_InitializeAuthorizedToolkitsAsync()
    {
        _logger.LogInformation("Example 3a: Initialize with authorized toolkits only");

        var toolRegistry = new ToolRegistry();
        var skillRegistry = new SkillRegistry(toolRegistry, logger: _logger as ILogger<SkillRegistry>);

        var composioApiKey = Environment.GetEnvironmentVariable("COMPOSIO_API_KEY")
            ?? throw new InvalidOperationException("COMPOSIO_API_KEY environment variable not set");

        var composioProvider = new ComposioSkillProvider(
            apiKey: composioApiKey,
            skillRegistry: skillRegistry,
            logger: _logger as ILogger<ComposioSkillProvider>
        );

        // Get authorized toolkits from auth configs
        var authorizedToolkits = await composioProvider.GetAuthorizedToolkitsAsync();
        _logger.LogInformation("Found {Count} authorized toolkits:", authorizedToolkits.Count);

        foreach (var toolkit in authorizedToolkits)
        {
            _logger.LogInformation("  - {Name} (Slug: {Slug})",
                toolkit.Name,
                toolkit.Slug ?? toolkit.Id
            );
        }

        // Initialize with only authorized toolkits
        await composioProvider.InitializeAsync();

        var allComposioSkills = composioProvider.GetAllSkills();
        _logger.LogInformation("Registered {Count} authorized Composio skills", allComposioSkills.Count);
    }

    /// <summary>
    /// Example 3b: Use the Composio skill provider directly
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

        // Get available toolkits before initializing
        var availableToolkits = await composioProvider.GetAvailableToolkitsAsync();
        _logger.LogInformation("Available toolkits from Composio.dev:");

        foreach (var toolkit in availableToolkits.Take(10))
        {
            _logger.LogInformation("  - {Name} ({Category}): {ToolsCount} tools",
                toolkit.Name,
                toolkit.Category ?? "N/A",
                toolkit.ToolsCount
            );
        }

        // Initialize (register all)
        await composioProvider.InitializeAsync();

        // Get all registered skills
        var allComposioSkills = composioProvider.GetAllSkills();
        _logger.LogInformation("Registered {Count} Composio skills", allComposioSkills.Count);
    }

    /// <summary>
    /// Example 4: Search for specific toolkits
    /// </summary>
    public async Task Example4_SearchToolkitsAsync()
    {
        _logger.LogInformation("Example 4: Search toolkits");

        var toolRegistry = new ToolRegistry();
        var skillRegistry = new SkillRegistry(toolRegistry, logger: _logger as ILogger<SkillRegistry>);

        var composioApiKey = Environment.GetEnvironmentVariable("COMPOSIO_API_KEY")
            ?? throw new InvalidOperationException("COMPOSIO_API_KEY environment variable not set");

        var composioProvider = new ComposioSkillProvider(
            apiKey: composioApiKey,
            skillRegistry: skillRegistry,
            logger: _logger as ILogger<ComposioSkillProvider>
        );

        // Search for GitHub toolkits
        var githubToolkits = await composioProvider.SearchToolkitsAsync(name: "github");
        _logger.LogInformation("Found {Count} toolkits matching 'github'", githubToolkits.Count);

        foreach (var toolkit in githubToolkits)
        {
            _logger.LogInformation("  - {Name}: {Description}", toolkit.Name, toolkit.Description);
        }

        // Search by category
        var communicationTools = await composioProvider.SearchToolkitsAsync(category: "communication");
        _logger.LogInformation("Found {Count} communication toolkits", communicationTools.Count);
    }

    /// <summary>
    /// Example 5: Get and examine tools for a toolkit
    /// </summary>
    public async Task Example5_ExamineToolkitToolsAsync()
    {
        _logger.LogInformation("Example 5: Examine toolkit tools");

        var toolRegistry = new ToolRegistry();
        var skillRegistry = new SkillRegistry(toolRegistry, logger: _logger as ILogger<SkillRegistry>);

        var composioApiKey = Environment.GetEnvironmentVariable("COMPOSIO_API_KEY")
            ?? throw new InvalidOperationException("COMPOSIO_API_KEY environment variable not set");

        var composioProvider = new ComposioSkillProvider(
            apiKey: composioApiKey,
            skillRegistry: skillRegistry,
            logger: _logger as ILogger<ComposioSkillProvider>
        );

        // Get tools for Gmail toolkit (using slug)
        try
        {
            var tools = await composioProvider.GetToolsAsync("gmail");
            _logger.LogInformation("Gmail toolkit has {Count} tools:", tools.Count);

            // Show first 3 tools with details
            foreach (var tool in tools.Take(3))
            {
                _logger.LogInformation("  Tool: {DisplayName}", tool.DisplayName);
                _logger.LogInformation("    Slug: {Slug}", tool.Slug);
                _logger.LogInformation("    Description: {Description}", tool.Description);
                _logger.LogInformation("    Human Description: {HumanDescription}", tool.HumanDescription);
                
                if (tool.InputParameters?.Properties != null && tool.InputParameters.Properties.Count > 0)
                {
                    _logger.LogInformation("    Input Parameters:");
                    foreach (var (paramName, paramDef) in tool.InputParameters.Properties.Take(3))
                    {
                        var isRequired = tool.InputParameters.Required?.Contains(paramName) ?? false;
                        var required = isRequired ? "[REQUIRED]" : "[optional]";
                        _logger.LogInformation("      - {Name} ({Type}) {Required}: {Description}",
                            paramName,
                            paramDef.Type ?? "string",
                            required,
                            paramDef.Description ?? ""
                        );
                    }

                    if (tool.InputParameters.Properties.Count > 3)
                    {
                        _logger.LogInformation("      ... and {Count} more parameters",
                            tool.InputParameters.Properties.Count - 3
                        );
                    }
                }
                
                if (tool.Scopes.Count > 0)
                {
                    _logger.LogInformation("    Required Scopes:");
                    foreach (var scope in tool.Scopes.Take(2))
                    {
                        _logger.LogInformation("      - {Scope}", scope);
                    }
                    if (tool.Scopes.Count > 2)
                    {
                        _logger.LogInformation("      ... and {Count} more scopes", tool.Scopes.Count - 2);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get tools for Gmail toolkit");
        }
    }

    /// <summary>
    /// Example 6: Execute a Composio tool directly
    /// </summary>
    public async Task Example6_ExecuteComposioToolAsync()
    {
        _logger.LogInformation("Example 6: Execute Composio tool");

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
            // Execute a tool (example: get repository info)
            // This requires proper authentication setup in Composio.dev
            var result = await composioProvider.ExecuteToolAsync(
                toolSlug: "github_get_repository",
                parameters: new Dictionary<string, object>
                {
                    { "owner", "your-github-username" },
                    { "repo", "your-repo-name" }
                },
                "accoutId",
                "userId"
            );

            _logger.LogInformation("Tool result: {Result}",
                System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute tool (this is expected if authentication is not set up)");
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
