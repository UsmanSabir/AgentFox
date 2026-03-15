# Composio.dev Skills Integration - Setup Guide

## Overview

This integration adds Composio.dev support to AgentFox, enabling dynamic discovery and use of pre-built integrations and actions as skills. Composio.dev provides 200+ integrations across domains like:

- **Developer Tools**: GitHub, GitLab, Bitbucket, Heroku, AWS, etc.
- **Communication**: Slack, Teams, Discord, Telegram, etc.
- **Project Management**: Jira, Asana, Linear, Monday.com, etc.
- **CRM**: Salesforce, HubSpot, Pipedrive, etc.
- **Email**: Gmail, Outlook, SendGrid, etc.
- **Files & Storage**: Google Drive, Dropbox, OneDrive, Box, etc.
- **Analytics**: Google Analytics, Mixpanel, Amplitude, etc.
- **And 200+ more...**

## Prerequisites

1. **Composio.dev Account**: Sign up at https://composio.dev
2. **API Key**: Generate an API key from the Composio.dev dashboard
3. **.NET 8.0+**: Required by the project

## Installation Steps

### 1. Get Your Composio API Key

1. Visit https://composio.dev and sign up
2. Navigate to the API Keys section in your dashboard
3. Generate or copy your API key
4. Save it securely (use environment variables or secrets manager)

### 2. Configure in Your Application

#### Option A: Environment Variable

```bash
# Windows
set COMPOSIO_API_KEY=your-api-key-here

# Linux/Mac
export COMPOSIO_API_KEY=your-api-key-here

# Or in .env file
COMPOSIO_API_KEY=your-api-key-here
```

#### Option B: Configuration File (appsettings.json)

```json
{
  "Composio": {
    "ApiKey": "your-composio-api-key",
    "Integrations": ["github", "slack", "jira"],
    "EnableDetailedLogging": true
  }
}
```

### 3. Register in Program.cs

```csharp
using AgentFox.Skills;

var builder = WebApplication.CreateBuilder(args);

// Get configuration
var config = builder.Configuration;

// Add logging
builder.Services.AddLogging();

// Create skill registry
var toolRegistry = new ToolRegistry();
var skillRegistry = new SkillRegistry(toolRegistry);

// Register Composio skills
var composioApiKey = config["Composio:ApiKey"] 
    ?? Environment.GetEnvironmentVariable("COMPOSIO_API_KEY");

if (!string.IsNullOrEmpty(composioApiKey))
{
    var integrations = config.GetSection("Composio:Integrations")
        .Get<List<string>>();
    
    if (integrations?.Any() == true)
    {
        await skillRegistry.RegisterComposioSkillsAsync(
            composioApiKey, 
            filterIntegrationIds: integrations
        );
    }
    else
    {
        await skillRegistry.RegisterComposioSkillsAsync(composioApiKey);
    }
}

// Add to dependency injection
builder.Services.AddSingleton(skillRegistry);

var app = builder.Build();
app.Run();
```

## Quick Start

### Minimal Setup

```csharp
var apiKey = "your-composio-api-key";
var skillRegistry = new SkillRegistry(toolRegistry);

// Initialize all Composio integrations
await skillRegistry.RegisterComposioSkillsAsync(apiKey);

// Skills are now registered - can be used by agents
```

### With Specific Integrations

```csharp
var apiKey = "your-composio-api-key";
var skillRegistry = new SkillRegistry(toolRegistry);

// Only register GitHub, Slack, and Jira
await skillRegistry.RegisterComposioSkillsAsync(
    apiKey,
    filterIntegrationIds: new[] { "github", "slack", "jira" }
);
```

### Advanced Usage with Provider

```csharp
// Use the provider for more control
var composioProvider = new ComposioSkillProvider(
    apiKey: "your-api-key",
    skillRegistry: skillRegistry,
    logger: logger
);

// Explore available integrations
var integrations = await composioProvider.GetAvailableIntegrationsAsync();
foreach (var integration in integrations)
{
    Console.WriteLine($"{integration.Name} ({integration.ActionsCount} actions)");
}

// Search for specific integrations
var githubIntegrations = await composioProvider
    .SearchIntegrationsAsync(name: "github");

// Get actions for an integration
var actions = await composioProvider.GetActionsAsync("github");

// Initialize selected integrations
await composioProvider.InitializeAsync(
    filterIntegrationIds: new[] { "github", "slack" }
);
```

## File Structure

New files added to support Composio.dev:

```
Skills/
├── ComposioClient.cs              # Low-level API client
├── ComposioSkillProvider.cs       # Main integration provider
├── ComposioSkillsExample.cs       # Usage examples
├── ComposioIntegration.md         # Detailed documentation
└── COMPOSIO_SETUP_GUIDE.md        # This file
```

## Key Classes

### ComposioClient
Low-level HTTP client for Composio.dev API:
- `GetIntegrationsAsync()` - List all integrations
- `GetActionsAsync(integrationId)` - Get actions for integration
- `ExecuteActionAsync(integrationId, actionId, parameters)` - Execute an action
- `GetIntegrationAsync(integrationId)` - Get integration details
- `GetAuthModesAsync(integrationId)` - Get auth modes

### ComposioSkillProvider
High-level provider for managing skills:
- `InitializeAsync(filterIntegrationIds?)` - Register integrations as skills
- `GetSkill(integrationId)` - Get a specific skill
- `GetAllSkills()` - Get all registered skills
- `GetAvailableIntegrationsAsync()` - List available integrations
- `SearchIntegrationsAsync(name?, category?)` - Search integrations
- `ExecuteActionAsync(integrationId, actionId, parameters)` - Execute an action

### ComposioSkillAdapter
Wraps a Composio integration as a Skill:
- Automatically creates tools from Composio actions
- Generates system prompts for the LLM
- Implements ISkillPlugin for registration hooks

### ComposioActionTool
Wraps a Composio action as an ITool:
- Implements parameter validation
- Handles execution and error handling
- Compatible with AgentFox tool system

## Common Integration Examples

### GitHub Integration

```csharp
var actions = await composioProvider.GetActionsAsync("github");
// Available actions: create_issue, create_pull_request, list_repositories, etc.

await composioProvider.ExecuteActionAsync(
    "github",
    "create_issue",
    new Dictionary<string, object>
    {
        { "owner", "your-username" },
        { "repo", "your-repo" },
        { "title", "Bug: Something is broken" },
        { "body", "Description of the bug" }
    }
);
```

### Slack Integration

```csharp
await composioProvider.ExecuteActionAsync(
    "slack",
    "send_message",
    new Dictionary<string, object>
    {
        { "channel_id", "C123456" },
        { "message", "Hello from AgentFox!" }
    }
);
```

### Jira Integration

```csharp
await composioProvider.ExecuteActionAsync(
    "jira",
    "create_issue",
    new Dictionary<string, object>
    {
        { "project_key", "PROJ" },
        { "issue_type", "Bug" },
        { "summary", "Integration test" },
        { "description", "Test description" }
    }
);
```

## Troubleshooting

### "COMPOSIO_API_KEY environment variable not set"
**Solution**: Set the environment variable or configure it in appsettings.json

### "Authorization failed for Composio.dev"
**Solution**: Verify your API key is correct and active in the Composio.dev dashboard

### "Retrieved 0 integrations from Composio.dev"
**Solution**: Check that your Composio.dev account has access to integrations

### "Missing required parameters"
**Solution**: Check the action's InputParams for required fields using GetActionsAsync()

## Performance Tips

1. **Selective Registration**: Only register integrations you need
   ```csharp
   await skillRegistry.RegisterComposioSkillsAsync(
       apiKey,
       filterIntegrationIds: new[] { "github", "slack" }
   );
   ```

2. **Async Loading**: All operations are async for non-blocking behavior

3. **Caching**: Integration metadata is cached automatically

4. **Error Handling**: Use try-catch for robust error handling

## Authentication Setup

Some Composio integrations require authentication:

1. **OAuth Integrations** (GitHub, Google, etc.):
   - Authenticated through Composio.dev dashboard
   - Setup authorization once, use across agents

2. **API Key Integrations** (Slack, Jira, etc.):
   - Provide API keys in Composio.dev dashboard
   - Configure once, reuse for all actions

3. **Connected Accounts**:
   - Use `GetAuthModesAsync()` to check available auth methods
   - Configure in Composio.dev UI

## Next Steps

1. Run the examples in [ComposioSkillsExample.cs](./ComposioSkillsExample.cs)
2. Read the detailed [ComposioIntegration.md](./ComposioIntegration.md) guide
3. Set up authentication for your needed integrations in Composio.dev
4. Integrate with your agent's skill system
5. Monitor usage and adjust registrations based on needs

## Support & Resources

- **Composio.dev Docs**: https://docs.composio.dev
- **API Reference**: https://api.composio.dev/docs
- **Example Code**: See [ComposioSkillsExample.cs](./ComposioSkillsExample.cs)
- **Skill System Docs**: See [README_SUBAGENT_LANES.md](./README_SUBAGENT_LANES.md)

## License

This integration is part of AgentFox and follows the same license terms.
