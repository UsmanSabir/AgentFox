# Composio.dev Skills Integration Guide

## Overview

Composio.dev is a platform providing pre-built integrations and actions for various services and APIs. This integration allows AgentFox to dynamically discover and use Composio.dev integrations as skills.

## Features

- **Dynamic Integration Discovery**: Automatically fetch all available integrations from Composio.dev
- **Action-to-Tool Mapping**: Convert Composio.dev actions to AgentFox tools
- **Skill Adapter**: Wrap Composio integrations as first-class Skill objects
- **Parameter Validation**: Automatic validation of required parameters
- **Error Handling**: Robust error handling with detailed logging
- **Caching**: Intelligent caching of integration metadata and actions

## Setup

### 1. Obtain API Key

Sign up at [Composio.dev](https://composio.dev) and obtain your API key from the dashboard.

### 2. Initialize in Your Application

```csharp
using AgentFox.Skills;
using Microsoft.Extensions.Logging;

// Get your logger
ILogger<ComposioSkillProvider> logger = // your logger

// Create the provider
var skillRegistry = new SkillRegistry(toolRegistry, promptBuilder, logger);
var composioProvider = new ComposioSkillProvider(
    apiKey: "your-composio-api-key",
    skillRegistry: skillRegistry,
    logger: logger
);

// Initialize all available integrations
await composioProvider.InitializeAsync();
```

### 3. Register Specific Integrations Only

```csharp
// Only register specific integrations
var integrationIds = new[] { "github", "slack", "jira" };
await composioProvider.InitializeAsync(filterIntegrationIds: integrationIds);
```

## Usage Examples

### List Available Integrations

```csharp
var integrations = await composioProvider.GetAvailableIntegrationsAsync();
foreach (var integration in integrations)
{
    Console.WriteLine($"{integration.Name}: {integration.Description}");
    Console.WriteLine($"  Actions: {integration.ActionsCount}");
}
```

### Search for Integrations

```csharp
// Search by name
var githubIntegrations = await composioProvider.SearchIntegrationsAsync(
    name: "github"
);

// Search by category
var communicationTools = await composioProvider.SearchIntegrationsAsync(
    category: "communication"
);
```

### Get Actions for an Integration

```csharp
var actions = await composioProvider.GetActionsAsync("github");
foreach (var action in actions)
{
    Console.WriteLine($"{action.DisplayName}: {action.Description}");
    Console.WriteLine("Parameters:");
    foreach (var param in action.InputParams)
    {
        var required = param.Required ? "[required]" : "[optional]";
        Console.WriteLine($"  - {param.Name} ({param.Type}) {required}");
    }
}
```

### Execute a Composio Action

```csharp
var parameters = new Dictionary<string, object>
{
    { "repo_url", "https://github.com/user/repo" },
    { "branch", "main" }
};

var result = await composioProvider.ExecuteActionAsync(
    integrationId: "github",
    actionId: "get_repo_info",
    parameters: parameters
);

Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
```

### Access Registered Skills

```csharp
// Get a specific skill
var githubSkill = composioProvider.GetSkill("github");
if (githubSkill != null)
{
    var tools = githubSkill.GetTools();
    var prompts = githubSkill.GetSystemPrompts();
}

// Get all Composio skills
var allComposioSkills = composioProvider.GetAllSkills();
```

## Available Integration Categories

Composio.dev provides integrations across various categories:

- **Developer Tools**: GitHub, GitLab, Bitbucket, GitPod
- **Communication**: Slack, Teams, Discord, Telegram
- **Project Management**: Jira, Asana, Monday.com, Trello
- **CRM**: Salesforce, HubSpot, Pipedrive
- **Email**: Gmail, Outlook, SendGrid
- **Files & Storage**: Google Drive, Dropbox, OneDrive
- **Analytics**: Google Analytics, Mixpanel, Amplitude
- **Monitoring**: DataDog, New Relic, Prometheus
- **And many more...**

## API Reference

### ComposioSkillProvider

#### Methods

- `InitializeAsync(filterIntegrationIds?)` - Initialize and register integrations
- `GetSkill(integrationId)` - Get a specific Composio skill
- `GetAllSkills()` - Get all registered Composio skills
- `GetAvailableIntegrationsAsync()` - List all available integrations
- `GetActionsAsync(integrationId)` - Get actions for an integration
- `ExecuteActionAsync(integrationId, actionId, parameters)` - Execute an action
- `SearchIntegrationsAsync(name?, category?)` - Search for integrations

### ComposioClient

Low-level API client for direct Composio.dev API interactions.

#### Methods

- `GetIntegrationsAsync()` - List all integrations
- `GetActionsAsync(integrationId)` - List actions for an integration
- `ExecuteActionAsync(integrationId, actionId, parameters)` - Execute an action
- `GetIntegrationAsync(integrationId)` - Get integration details
- `GetAuthModesAsync(integrationId)` - Get authentication modes

## Global Skills Registration

To register all Composio.dev skills during application startup, add to `Program.cs`:

```csharp
// In your service configuration
var composioProvider = new ComposioSkillProvider(
    apiKey: configuration["Composio:ApiKey"],
    skillRegistry: skillRegistry,
    logger: loggerFactory.CreateLogger<ComposioSkillProvider>()
);

await composioProvider.InitializeAsync();
```

## Error Handling

The provider includes comprehensive error handling:

```csharp
try
{
    await composioProvider.InitializeAsync();
}
catch (Exception ex)
{
    logger.LogError(ex, "Failed to initialize Composio provider");
    // Gracefully handle or re-throw
}
```

Each action execution is wrapped in try-catch with detailed logging and user-friendly error messages.

## Best Practices

1. **API Key Management**: Store your Composio API key in secure configuration (environment variables, secrets manager)
2. **Selective Registration**: Only register integrations you actually need to reduce overhead
3. **Caching**: The provider caches action metadata for performance
4. **Error Handling**: Always wrap Composio operations in try-catch blocks
5. **Logging**: Enable detailed logging during development for debugging
6. **Parameter Validation**: Check InputParams before executing actions
7. **Monitoring**: Track skill usage metrics for performance optimization

## Integration with System Prompts

Composio skills automatically generate system prompts that are injected into the agent's context:

```
## {Integration Name} Integration

You have access to the {Integration Name} integration through various actions:

Available Actions:
- action1: Description
- action2: Description
- ...
```

This allows the LLM to understand what capabilities are available through each integration.

## Performance Considerations

- **Initial Load**: First `InitializeAsync()` call fetches all integrations and their actions
- **Caching**: Actions are cached in memory for quick lookup
- **Async Operations**: All operations are fully async to avoid blocking
- **Rate Limiting**: Composio.dev may have rate limits; consider implementing backoff

## Troubleshooting

### Invalid API Key
```
Error: Authorization failed for Composio.dev
Solution: Verify your API key in configuration
```

### No Integrations Found
```
Error: Retrieved 0 integrations from Composio.dev
Solution: Check that your Composio.dev account has available integrations
```

### Action Execution Fails
```
Error: Missing required parameters: repo_url, branch
Solution: Check ComposioAction.InputParams for required fields
```

## See Also

- [Composio.dev Documentation](https://docs.composio.dev)
- [Skill System Architecture](./README_SUBAGENT_LANES.md)
- [System Prompts Guide](../SYSTEM_PROMPTS_IMPLEMENTATION.md)
