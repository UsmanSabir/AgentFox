# Composio.dev Skills - Quick Reference

## ⚡ Get Started in 5 Minutes

### 1. Get API Key
Sign up at https://composio.dev and copy your API key

### 2. Set Environment Variable
```bash
export COMPOSIO_API_KEY=your-api-key-here
```

### 3. Register in Code
```csharp
var skillRegistry = new SkillRegistry(toolRegistry);
var apiKey = Environment.GetEnvironmentVariable("COMPOSIO_API_KEY");
await skillRegistry.RegisterComposioSkillsAsync(apiKey);
```

**Done!** All Composio integrations are now available as skills.

---

## 🎯 Common Tasks

### List All Available Integrations
```csharp
var provider = new ComposioSkillProvider(apiKey, skillRegistry);
var integrations = await provider.GetAvailableIntegrationsAsync();

foreach (var integration in integrations)
{
    Console.WriteLine($"{integration.Name}: {integration.ActionsCount} actions");
}
```

### Register Only Specific Integrations
```csharp
await skillRegistry.RegisterComposioSkillsAsync(
    apiKey,
    filterIntegrationIds: new[] { "github", "slack", "jira" }
);
```

### Search for Integrations
```csharp
// By name
var github = await provider.SearchIntegrationsAsync(name: "github");

// By category
var communication = await provider.SearchIntegrationsAsync(category: "communication");
```

### Get Actions for an Integration
```csharp
var actions = await provider.GetActionsAsync("github");

foreach (var action in actions)
{
    Console.WriteLine($"{action.DisplayName}");
    foreach (var param in action.InputParams.Where(p => p.Required))
    {
        Console.WriteLine($"  Required: {param.Name} ({param.Type})");
    }
}
```

### Execute a Composio Action
```csharp
var result = await provider.ExecuteActionAsync(
    integrationId: "github",
    actionId: "create_issue",
    parameters: new Dictionary<string, object>
    {
        { "owner", "your-username" },
        { "repo", "your-repo" },
        { "title", "Bug Title" },
        { "body", "Bug Description" }
    }
);

Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
    result, 
    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
));
```

---

## 🔗 Integration Examples

### GitHub
```csharp
// Create Issue
await provider.ExecuteActionAsync("github", "create_issue", new() {
    { "owner", "username" },
    { "repo", "repo-name" },
    { "title", "Issue Title" },
    { "body", "Issue Body" }
});

// Create Pull Request
await provider.ExecuteActionAsync("github", "create_pull_request", new() {
    { "owner", "username" },
    { "repo", "repo-name" },
    { "title", "PR Title" },
    { "head", "feature-branch" },
    { "base", "main" }
});
```

### Slack
```csharp
// Send Message
await provider.ExecuteActionAsync("slack", "send_message", new() {
    { "channel_id", "C123456" },
    { "message", "Hello from AgentFox!" }
});

// Create Channel
await provider.ExecuteActionAsync("slack", "create_channel", new() {
    { "channel_name", "new-channel" },
    { "is_private", false }
});
```

### Jira
```csharp
// Create Issue
await provider.ExecuteActionAsync("jira", "create_issue", new() {
    { "project_key", "PROJ" },
    { "issue_type", "Bug" },
    { "summary", "Issue Summary" },
    { "description", "Issue Description" }
});
```

---

## 📋 Configuration Reference

### appsettings.json
```json
{
  "Composio": {
    "ApiKey": "your-composio-api-key",
    "Integrations": ["github", "slack", "jira"],
    "EnableDetailedLogging": true,
    "CacheTimeoutSeconds": 3600
  }
}
```

### Program.cs Setup
```csharp
// Option 1: All integrations
await skillRegistry.RegisterComposioSkillsAsync(config["Composio:ApiKey"]);

// Option 2: Specific integrations from config
var integrations = config.GetSection("Composio:Integrations").Get<List<string>>();
if (integrations?.Any() == true)
{
    await skillRegistry.RegisterComposioSkillsAsync(
        config["Composio:ApiKey"],
        filterIntegrationIds: integrations
    );
}
```

---

## 🏗️ Class Reference

### ComposioSkillProvider
Main class for managing Composio skills.

**Key Methods:**
- `InitializeAsync(filterIds?)` - Register integrations
- `GetSkill(id)` - Get specific skill
- `GetAllSkills()` - Get all skills
- `GetAvailableIntegrationsAsync()` - List integrations
- `GetActionsAsync(integrationId)` - Get actions
- `SearchIntegrationsAsync(name?, category?)` - Search
- `ExecuteActionAsync(id, actionId, params)` - Execute

### ComposioClient
Low-level API client (usually don't use directly).

**Key Methods:**
- `GetIntegrationsAsync()`
- `GetActionsAsync(integrationId)`
- `ExecuteActionAsync(integrationId, actionId, parameters)`
- `GetIntegrationAsync(integrationId)`
- `GetAuthModesAsync(integrationId)`

### Data Models
- `ComposioIntegration` - Integration metadata
- `ComposioAction` - Action definition
- `ComposioParameter` - Parameter definition
- `ComposioApiResponse<T>` - API response wrapper

---

## 🚨 Troubleshooting

| Problem | Solution |
|---------|----------|
| "API key not configured" | Set `COMPOSIO_API_KEY` env var or config |
| "Authorization failed" | Verify API key is correct in Composio.dev |
| "0 integrations found" | Check your Composio.dev account settings |
| "Missing required parameters" | Check action's `InputParams` with `GetActionsAsync()` |
| "Action execution failed" | Verify authentication for that integration in Composio.dev |

---

## 📚 Available Integration Categories

```
Developer Tools: github, gitlab, bitbucket, heroku, aws, azure
Communication: slack, teams, discord, telegram, whatsapp
Project Management: jira, asana, linear, monday, trello
CRM: salesforce, hubspot, pipedrive, zendesk
Email: gmail, outlook, sendgrid, mailgun
Files: google-drive, dropbox, onedrive, box
Analytics: google-analytics, mixpanel, amplitude, segment
Monitoring: datadog, neweelic, prometheus, grafana
```

See full list: [ComposioIntegration.md - Integration Categories](./ComposioIntegration.md#available-integration-categories)

---

## 💡 Tips & Best Practices

### Performance
1. **Register only what you need**
   ```csharp
   await skillRegistry.RegisterComposioSkillsAsync(apiKey, 
       filterIntegrationIds: new[] { "github", "slack" });
   ```

2. **Cache integration metadata**
   - Already done automatically
   - Set `CacheTimeoutSeconds` if needed

3. **Use specific integrations for agents**
   - Reduce context size
   - Faster agent responses

### Security
1. **Never commit API keys**
   - Use environment variables
   - Use secrets manager in production

2. **Authenticate integrations in Composio.dev**
   - Setup once, use everywhere
   - Credentials never leave Composio.dev

3. **Monitor usage**
   - Check execution logs
   - Watch for unusual patterns

### Reliability
1. **Handle exceptions**
   - Wrap calls in try-catch
   - Log errors appropriately

2. **Validate parameters**
   - Check required fields
   - Use enums when available

3. **Retry failed actions**
   - Implement backoff logic
   - Consider rate limits

---

## 🔗 Links

- **Demo/Examples**: See [ComposioSkillsExample.cs](./ComposioSkillsExample.cs)
- **Full Setup Guide**: See [COMPOSIO_SETUP_GUIDE.md](./COMPOSIO_SETUP_GUIDE.md)
- **Technical Details**: See [ComposioIntegration.md](./ComposioIntegration.md)
- **Official Docs**: https://docs.composio.dev
- **API Reference**: https://api.composio.dev/docs

---

## 🎓 Learning Path

1. **Get Started** (5 min)
   - Sign up at Composio.dev
   - Get API key
   - Follow "Get Started in 5 Minutes" above

2. **Explore** (10 min)
   - List integrations with code
   - Search for ones you need
   - Examine available actions

3. **Configure** (10 min)
   - Setup authentication in Composio.dev
   - Add to your app config
   - Test with examples

4. **Integrate** (varies)
   - Use in agent system prompts
   - Call from agent actions
   - Monitor execution

5. **Production** (varies)
   - Secure API key management
   - Error handling & monitoring
   - Usage optimization

---

**Last Updated**: March 2026  
**Version**: 1.0.0  
**Status**: Production Ready ✅
