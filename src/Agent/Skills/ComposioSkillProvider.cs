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
    private readonly Dictionary<string, ComposioTool> _cachedActions = new();

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
    /// Initialize and register authorized toolkits from auth configs as skills
    /// </summary>
    public async Task InitializeAsync(IEnumerable<string>? filterToolkitIds = null)
    {
        bool useOnlyAuthorizedToolkits = true; //todo
        try
        {
            _logger?.LogInformation("Initializing Composio.dev skill provider");

            List<ComposioToolkit> toolkits;

            if (useOnlyAuthorizedToolkits)
            {
                var activeAccounts = await _client.GetActiveConnectedAccountsAsync();
                var toolkitDict = new Dictionary<string, ComposioToolkit>();

                foreach (var account in activeAccounts)
                {
                    if (account.Toolkit != null && !string.IsNullOrEmpty(account.Toolkit.Slug))
                    {
                        var slug = account.Toolkit.Slug;
                        if (!toolkitDict.ContainsKey(slug))
                        {
                            toolkitDict[slug] = new ComposioToolkit
                            {
                                Id = slug,
                                Name = account.Toolkit.Name ?? slug,
                                Slug = slug,
                                ConnectedAccountId = account.Id,
                                UserId = account.UserId,
                                IsAvailable = true,
                                Logo = account.Toolkit.Logo
                            };
                        }
                    }
                }

                toolkits = toolkitDict.Values.ToList();
                _logger?.LogInformation("Using toolkits from {Count} active connected accounts", activeAccounts.Count);
            }
            else
            {
                toolkits = await _client.GetToolkitsAsync();
                _logger?.LogInformation("Using all available toolkits");
            }

            if (filterToolkitIds != null)
            {
                var filterSet = filterToolkitIds.ToHashSet();
                toolkits = toolkits.Where(t => filterSet.Contains(t.Slug ?? t.Id)).ToList();
            }

            _logger?.LogInformation("Found {Count} available toolkits", toolkits.Count);

            foreach (var toolkit in toolkits.Where(t => t.IsAvailable))
            {
                try
                {
                    await RegisterToolkitSkillAsync(toolkit);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to register skill for toolkit {ToolkitId}", toolkit.Id);
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
    /// Register a single toolkit as a skill
    /// </summary>
    private async Task RegisterToolkitSkillAsync(ComposioToolkit toolkit)
    {
        try
        {
            var toolkitSlug = toolkit.Slug ?? toolkit.Id;
            var tools = await _client.GetToolsAsync(toolkitSlug);

            var skillAdapter = new ComposioSkillAdapter(
                toolkit,
                tools,
                _client,
                _logger
            );

            _skillRegistry.Register(skillAdapter);
            _composioSkills[toolkit.Id] = skillAdapter;

            // Cache tools for quick lookup using slug (ID may be empty)
            foreach (var tool in tools)
            {
                var toolKey = tool.Slug ?? tool.Id ?? tool.Name;
                if (!string.IsNullOrEmpty(toolKey))
                {
                    _cachedActions[$"{toolkit.Slug ?? toolkit.Id}:{toolKey}"] = tool;
                }
            }

            _logger?.LogInformation(
                "Registered Composio.dev skill {ToolkitName} with {ToolCount} tools",
                toolkit.Name,
                tools.Count
            );
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to register toolkit {ToolkitId} as skill", toolkit.Id);
            throw;
        }
    }

    /// <summary>
    /// Get a specific Composio skill by toolkit ID
    /// </summary>
    public ComposioSkillAdapter? GetSkill(string toolkitId)
    {
        return _composioSkills.TryGetValue(toolkitId, out var skill) ? skill : null;
    }

    /// <summary>
    /// Get all registered Composio skills
    /// </summary>
    public List<ComposioSkillAdapter> GetAllSkills()
    {
        return _composioSkills.Values.ToList();
    }

    /// <summary>
    /// Get all authorized toolkits from auth configs
    /// </summary>
    public async Task<List<ComposioToolkit>> GetAuthorizedToolkitsAsync()
    {
        return await _client.GetEnabledToolkitsAsync();
    }

    /// <summary>
    /// Get all available toolkits from Composio.dev
    /// </summary>
    public async Task<List<ComposioToolkit>> GetAvailableToolkitsAsync()
    {
        var toolkits = await _client.GetToolkitsAsync();
        return toolkits.Where(t => t.IsAvailable).ToList();
    }

    /// <summary>
    /// Get tools for a specific toolkit
    /// </summary>
    public async Task<List<ComposioTool>> GetToolsAsync(string toolkitSlug)
    {
        return await _client.GetToolsAsync(toolkitSlug);
    }
    
    /// <summary>
    /// Execute a Composio tool with connected account context
    /// </summary>
    public async Task<Dictionary<string, object>> ExecuteToolAsync(
        string toolSlug,
        Dictionary<string, object> parameters,
        string connectedAccountId,
        string userId)
    {
        _logger?.LogInformation(
            "Executing Composio tool {ToolSlug} with account {AccountId}",
            toolSlug,
            connectedAccountId
        );

        return await _client.ExecuteToolAsync(
            toolSlug,
            parameters,
            connectedAccountId,
            userId
        );
    }

    /// <summary>
    /// Get all connected accounts
    /// </summary>
    public async Task<List<ComposioConnectedAccount>> GetConnectedAccountsAsync()
    {
        return await _client.GetConnectedAccountsAsync();
    }

    /// <summary>
    /// Get active connected accounts for a specific toolkit
    /// </summary>
    public async Task<List<ComposioConnectedAccount>> GetActiveConnectedAccountsAsync()
    {
        return await _client.GetActiveConnectedAccountsAsync();
    }

    /// <summary>
    /// Search for toolkits by name or category
    /// </summary>
    public async Task<List<ComposioToolkit>> SearchToolkitsAsync(string? name = null, string? category = null)
    {
        var toolkits = await _client.GetToolkitsAsync();

        return toolkits.Where(t =>
        {
            bool matchName = string.IsNullOrEmpty(name) ||
                t.Name.Contains(name, StringComparison.OrdinalIgnoreCase);
            bool matchCategory = string.IsNullOrEmpty(category) ||
                t.Category?.Equals(category, StringComparison.OrdinalIgnoreCase) == true;
            return matchName && matchCategory && t.IsAvailable;
        }).ToList();
    }
}

/// <summary>
/// Skill adapter that wraps a Composio.dev toolkit as a Skill
/// </summary>
public class ComposioSkillAdapter : Skill, ISkillPlugin
{
    private readonly ComposioToolkit _toolkit;
    private readonly List<ComposioTool> _tools;
    private readonly ComposioClient _client;
    private readonly ILogger? _logger;

    public ComposioSkillAdapter(
        ComposioToolkit toolkit,
        List<ComposioTool> tools,
        ComposioClient client,
        ILogger? logger = null)
    {
        _toolkit = toolkit;
        _tools = tools;
        _client = client;
        _logger = logger;

        Name = toolkit.Slug ?? toolkit.Id;
        // Fallback if description is missing
        Description = !string.IsNullOrWhiteSpace(toolkit.Description)
            ? $"{toolkit.Name}: {toolkit.Description}"
            : toolkit.Name;
        Version = "1.0.0";

        Metadata = new SkillMetadata
        {
            SkillName = toolkit.Name,
            Version = "1.0.0",
            Capabilities = _tools.Select(t => t.Name).ToList(),
            Tags = _tools.SelectMany(t => t.Tags).Distinct().ToList(),
            ComplexityScore = Math.Min(10, _tools.Count / 2),
            IsCompositional = true,
            RelatedSkills = new()
        };
    }

    public override List<ITool> GetTools()
    {
        return _tools.Select(tool =>
        {
            // Use slug as the primary identifier since ID may be empty
            var toolId = tool.Slug ?? tool.Id ?? tool.Name;
            return new ComposioToolWrapper(
                toolId,
                tool,
                _client,
                _logger as ILogger<ComposioToolWrapper>
            ) as ITool;
        }).ToList();
    }

    public override List<string> GetSystemPrompts()
    {
        var toolDescriptions = string.Join("\n", _tools.Select(t =>
        {
            var displayName = !string.IsNullOrWhiteSpace(t.DisplayName) ? t.DisplayName : t.Name;
            var description = !string.IsNullOrWhiteSpace(t.Description) ? t.Description : "(no description available)";
            return $"- {displayName}: {description}";
        }));

        var systemPrompt = $@"
## {_toolkit.Name} Toolkit

You have access to the {_toolkit.Name} toolkit through various tools:

Available Tools:
{toolDescriptions}

When using these tools:
1. Understand the input parameters required for each tool
2. Provide all required parameters
3. Handle the output appropriately
4. Use these tools to automate {_toolkit.Category ?? "toolkit"} tasks
";
        return new List<string> { systemPrompt };
    }

    public SkillManifest GetManifest() => new(
        Name,
        $"{_toolkit.Name}: {_toolkit.Description ?? ""}",
        _tools.Count,
        "composio");

    public async Task OnRegisterAsync(ISkillRegistrationContext context)
    {
        foreach (var tool in GetTools())
        {
            context.RegisterTool(tool);
        }
        // Full guidance loaded on-demand via load_skill tool; skip injecting here
        //  var systemPrompts = GetSystemPrompts();
        // foreach (var prompt in systemPrompts)
        // {
        //     context.PrependSystemContext(prompt);
        // }

        _logger?.LogInformation("Registered Composio skill {SkillName}", Name);
    }
}

/// <summary>
/// Tool wrapper for a Composio.dev tool
/// </summary>
public class ComposioToolWrapper : ITool
{
    private readonly string _toolSlug;
    private readonly ComposioTool _tool;
    private readonly ComposioClient _client;
    private readonly ILogger<ComposioToolWrapper>? _logger;
    private Dictionary<string, ToolParameter>? _parameters;

    public string Name => _tool.Name;
    public string Description => _tool.Description;
    public List<string> Tags => _tool.Tags;

    public Dictionary<string, ToolParameter> Parameters
    {
        get
        {
            if (_parameters == null)
            {
                _parameters = _tool.InputParams.ToDictionary(
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

    public ComposioToolWrapper(
        string toolSlug,
        ComposioTool tool,
        ComposioClient client,
        ILogger<ComposioToolWrapper>? logger = null)
    {
        _toolSlug = toolSlug;
        _tool = tool;
        _client = client;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(Dictionary<string, object?> arguments)
    {
        try
        {
            _logger?.LogInformation(
                "Executing Composio tool {ToolName} with parameters {ParameterCount}",
                _tool.Name,
                arguments?.Count ?? 0
            );

            var parameters = arguments ?? new Dictionary<string, object?>();

            // Validate required parameters
            var missingParams = _tool.InputParams
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

            Dictionary<string, object> result;

            // Try to get active connected accounts for this tool's toolkit
            // This allows execution with proper authentication context
            try
            {
                // Extract toolkit slug from tool metadata if available
                // For Composio tools, we need to determine the toolkit
                var toolkitSlug = ExtractToolkitSlug();

                if (!string.IsNullOrEmpty(toolkitSlug))
                {
                    var activeAccounts = await _client.GetActiveConnectedAccountsAsync(toolkitSlug);

                    if (activeAccounts.Any())
                    {
                        // Use first active account
                        var account = activeAccounts.First();

                        _logger?.LogInformation(
                            "Using active account {AccountId} for user {UserId}",
                            account.Id,
                            account.UserId
                        );

                        result = await _client.ExecuteToolAsync(
                            _toolSlug,
                            cleanParams,
                            account.Id,
                            account.UserId
                        );
                    }
                    else
                    {
                        _logger?.LogWarning(
                            "No active connected account found for toolkit {ToolkitSlug}. Using unauthenticated execution.",
                            toolkitSlug
                        );

                        return ToolResult.Fail($"No active connected account found for toolkit {toolkitSlug}. Cannot proceed with tool call");
                    }
                }
                else
                {
                    return ToolResult.Fail($"No active connected account found for toolkit {toolkitSlug}. Cannot proceed with tool call");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Failed to get active accounts, falling back to unauthenticated execution"
                );
                return ToolResult.Fail($"Failed to get active accounts");
            }

            return ToolResult.Ok(System.Text.Json.JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to execute Composio tool {ToolName}", _tool.Name);
            return ToolResult.Fail($"Error executing tool: {ex.Message}");
        }
    }

    /// <summary>
    /// Extract toolkit slug from tool slug (format: TOOLKIT_ACTION or toolkit_slug:action)
    /// </summary>
    private string? ExtractToolkitSlug()
    {
        try
        {
            // If tool slug contains a colon, extract the toolkit part
            if (_toolSlug.Contains(':'))
            {
                return _toolSlug.Split(':')[0].ToLower();
            }

            // For UPPERCASE_UNDERSCORE format, convert to lowercase slug
            // Example: GMAIL_FETCH_EMAILS -> gmail
            var parts = _toolSlug.Split('_');
            if (parts.Length > 0)
            {
                return parts[0].ToLower();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to extract toolkit slug from tool slug {ToolSlug}", _toolSlug);
            return null;
        }
    }
}
