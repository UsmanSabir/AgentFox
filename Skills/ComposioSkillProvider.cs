using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AgentFox.Tools;

namespace AgentFox.Skills;

/// <summary>
/// Provider for managing Composio.dev skills and actions
/// </summary>
public class ComposioSkillProvider
{
    private readonly ComposioClient _client;
    private readonly ILogger<ComposioSkillProvider>? _logger;
    private readonly SkillRegistry _skillRegistry;
    private readonly Dictionary<string, ComposioSkillAdapter> _composioSkills = new();
    private readonly Dictionary<string, ComposioAction> _cachedActions = new();

    public ComposioSkillProvider(
        string apiKey,
        SkillRegistry skillRegistry,
        ILogger<ComposioSkillProvider>? logger = null)
    {
        _client = new ComposioClient(apiKey, logger as ILogger<ComposioClient>);
        _skillRegistry = skillRegistry ?? throw new ArgumentNullException(nameof(skillRegistry));
        _logger = logger;
    }

    /// <summary>
    /// Initialize and register all available Composio.dev integrations as skills
    /// </summary>
    public async Task InitializeAsync(IEnumerable<string>? filterIntegrationIds = null)
    {
        try
        {
            _logger?.LogInformation("Initializing Composio.dev skill provider");

            var integrations = await _client.GetIntegrationsAsync();
            
            if (filterIntegrationIds != null)
            {
                var filterSet = filterIntegrationIds.ToHashSet();
                integrations = integrations.Where(i => filterSet.Contains(i.Id)).ToList();
            }

            _logger?.LogInformation("Found {Count} available integrations", integrations.Count);

            foreach (var integration in integrations.Where(i => i.IsAvailable))
            {
                try
                {
                    await RegisterIntegrationSkillAsync(integration);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to register skill for integration {IntegrationId}", integration.Id);
                }
            }

            _logger?.LogInformation("Composio.dev skill provider initialized with {Count} skills", 
                _composioSkills.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize Composio.dev skill provider");
            throw;
        }
    }

    /// <summary>
    /// Register a single integration as a skill
    /// </summary>
    private async Task RegisterIntegrationSkillAsync(ComposioIntegration integration)
    {
        try
        {
            var actions = await _client.GetActionsAsync(integration.Id);
            
            var skillAdapter = new ComposioSkillAdapter(
                integration,
                actions,
                _client,
                _logger
            );

            _skillRegistry.Register(skillAdapter);
            _composioSkills[integration.Id] = skillAdapter;

            // Cache actions for quick lookup
            foreach (var action in actions)
            {
                _cachedActions[$"{integration.Id}:{action.Id}"] = action;
            }

            _logger?.LogInformation(
                "Registered Composio.dev skill {IntegrationName} with {ActionCount} actions",
                integration.Name,
                actions.Count
            );
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to register integration {IntegrationId} as skill", integration.Id);
            throw;
        }
    }

    /// <summary>
    /// Get a specific Composio skill by integration ID
    /// </summary>
    public ComposioSkillAdapter? GetSkill(string integrationId)
    {
        return _composioSkills.TryGetValue(integrationId, out var skill) ? skill : null;
    }

    /// <summary>
    /// Get all registered Composio skills
    /// </summary>
    public List<ComposioSkillAdapter> GetAllSkills()
    {
        return _composioSkills.Values.ToList();
    }

    /// <summary>
    /// Get all available integrations from Composio.dev
    /// </summary>
    public async Task<List<ComposioIntegration>> GetAvailableIntegrationsAsync()
    {
        var integrations = await _client.GetIntegrationsAsync();
        return integrations.Where(i => i.IsAvailable).ToList();
    }

    /// <summary>
    /// Get actions for a specific integration
    /// </summary>
    public async Task<List<ComposioAction>> GetActionsAsync(string integrationId)
    {
        return await _client.GetActionsAsync(integrationId);
    }

    /// <summary>
    /// Execute a Composio action directly
    /// </summary>
    public async Task<Dictionary<string, object>> ExecuteActionAsync(
        string integrationId,
        string actionId,
        Dictionary<string, object> parameters)
    {
        _logger?.LogInformation(
            "Executing Composio action {ActionId} on integration {IntegrationId}",
            actionId,
            integrationId
        );

        return await _client.ExecuteActionAsync(integrationId, actionId, parameters);
    }

    /// <summary>
    /// Search for integrations by name or category
    /// </summary>
    public async Task<List<ComposioIntegration>> SearchIntegrationsAsync(string? name = null, string? category = null)
    {
        var integrations = await _client.GetIntegrationsAsync();
        
        return integrations.Where(i =>
        {
            bool matchName = string.IsNullOrEmpty(name) || 
                i.Name.Contains(name, StringComparison.OrdinalIgnoreCase);
            bool matchCategory = string.IsNullOrEmpty(category) || 
                i.Category?.Equals(category, StringComparison.OrdinalIgnoreCase) == true;
            return matchName && matchCategory && i.IsAvailable;
        }).ToList();
    }
}

/// <summary>
/// Skill adapter that wraps a Composio.dev integration as a Skill
/// </summary>
public class ComposioSkillAdapter : Skill, ISkillPlugin
{
    private readonly ComposioIntegration _integration;
    private readonly List<ComposioAction> _actions;
    private readonly ComposioClient _client;
    private readonly ILogger? _logger;

    public ComposioSkillAdapter(
        ComposioIntegration integration,
        List<ComposioAction> actions,
        ComposioClient client,
        ILogger? logger = null)
    {
        _integration = integration;
        _actions = actions;
        _client = client;
        _logger = logger;

        Name = integration.Id;
        Description = $"{integration.Name}: {integration.Description}";
        Version = "1.0.0";

        Metadata = new SkillMetadata
        {
            SkillName = integration.Name,
            Version = "1.0.0",
            Capabilities = _actions.Select(a => a.Name).ToList(),
            Tags = _actions.SelectMany(a => a.Tags).Distinct().ToList(),
            ComplexityScore = Math.Min(10, _actions.Count / 2),
            IsCompositional = true,
            RelatedSkills = new()
        };
    }

    public override List<ITool> GetTools()
    {
        return _actions.Select(action =>
            new ComposioActionTool(
                _integration.Id,
                action,
                _client,
                _logger as ILogger<ComposioActionTool>
            ) as ITool
        ).ToList();
    }

    public override List<string> GetSystemPrompts()
    {
        var systemPrompt = $@"
## {_integration.Name} Integration

You have access to the {_integration.Name} integration through various actions:

Available Actions:
{string.Join("\n", _actions.Select(a => $"- {a.DisplayName}: {a.Description}"))}

When using these actions:
1. Understand the input parameters required for each action
2. Provide all required parameters
3. Handle the output appropriately
4. Use these actions to automate {_integration.Category ?? "integration"} tasks
";
        return new List<string> { systemPrompt };
    }

    public async Task OnRegisterAsync(ISkillRegistrationContext context)
    {
        foreach (var tool in GetTools())
        {
            context.RegisterTool(tool);
        }

        var systemPrompts = GetSystemPrompts();
        foreach (var prompt in systemPrompts)
        {
            context.PrependSystemContext(prompt);
        }

        _logger?.LogInformation("Registered Composio skill {SkillName}", Name);
    }
}

/// <summary>
/// Tool wrapper for a Composio.dev action
/// </summary>
public class ComposioActionTool : ITool
{
    private readonly string _integrationId;
    private readonly ComposioAction _action;
    private readonly ComposioClient _client;
    private readonly ILogger<ComposioActionTool>? _logger;
    private Dictionary<string, ToolParameter>? _parameters;

    public string Name => _action.Name;
    public string Description => _action.Description;
    public List<string> Tags => _action.Tags;

    public Dictionary<string, ToolParameter> Parameters 
    { 
        get
        {
            if (_parameters == null)
            {
                _parameters = _action.InputParams.ToDictionary(
                    p => p.Name,
                    p => new ToolParameter
                    {
                        Description = p.Description,
                        Type = p.Type,
                        Required = p.Required,
                        Default = p.Default,
                        EnumValues = p.EnumValues.Any() ? p.EnumValues : null
                    }
                );
            }
            return _parameters;
        }
    }

    public ComposioActionTool(
        string integrationId,
        ComposioAction action,
        ComposioClient client,
        ILogger<ComposioActionTool>? logger = null)
    {
        _integrationId = integrationId;
        _action = action;
        _client = client;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(Dictionary<string, object?> arguments)
    {
        try
        {
            _logger?.LogInformation(
                "Executing Composio action {ActionName} with parameters {ParameterCount}",
                _action.Name,
                arguments?.Count ?? 0
            );

            var parameters = arguments ?? new Dictionary<string, object?>();

            // Validate required parameters
            var missingParams = _action.InputParams
                .Where(p => p.Required && !parameters.ContainsKey(p.Name))
                .Select(p => p.Name)
                .ToList();

            if (missingParams.Any())
            {
                return ToolResult.Fail($"Missing required parameters: {string.Join(", ", missingParams)}");
            }

            // Filter out null values and convert to object dict
            var cleanParams = new Dictionary<string, object>();
            foreach (var kv in parameters)
            {
                if (kv.Value != null)
                {
                    cleanParams[kv.Key] = kv.Value;
                }
            }

            var result = await _client.ExecuteActionAsync(
                _integrationId,
                _action.Id,
                cleanParams
            );

            return ToolResult.Ok(System.Text.Json.JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to execute Composio action {ActionName}", _action.Name);
            return ToolResult.Fail($"Error executing action: {ex.Message}");
        }
    }
}
