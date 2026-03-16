using AgentFox.LLM;
using AgentFox.MCP;
using AgentFox.Memory;
using AgentFox.Models;
using AgentFox.Skills;
using AgentFox.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OllamaSharp.Models.Chat;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemPromptBuilder = AgentFox.LLM.SystemPromptBuilder;

namespace AgentFox.Agents;

/// <summary>
/// Main agent class that orchestrates all functionality
/// </summary>
public class FoxAgent
{
    private readonly Agent _agent;
    private readonly ChatClientAgent _chatAgent;
    private readonly ILogger<FoxAgent>? _logger;

    public string Id => _agent.Config.Id;
    public string Name => _agent.Config.Name;
    public IMemory Memory => _agent.Memory;
    public IConversationStore ConversationStore => _agent.ConversationStore;
    public AgentStatus Status => _agent.Status;
    public List<Agent> SubAgents => _agent.SubAgents;
    
    /// <summary>
    /// Skills enabled for this agent (used for capability-based routing)
    /// </summary>
    public List<Skill> EnabledSkills { get; set; } = new();

    /// <summary>
    /// Agent role for permission and resource limit checking
    /// </summary>
    public string Role { get; set; } = "default";


    public FoxAgent(ChatClientAgent agent, AgentConfig config, IConversationStore store, ILogger<FoxAgent>? logger = null)
    {
        _agent = new Agent
        {
            Config = config,
            Status = AgentStatus.Idle,
            ConversationStore = store
        };
        _chatAgent = agent;
        _logger = logger;        
    }

    /// <summary>
    /// Configure memory for the agent
    /// </summary>
    public FoxAgent WithMemory(IMemory memory)
    {
        _agent.Memory = memory;
        return this;
    }

    /// <summary>
    /// Configure hybrid memory (short-term + long-term)
    /// </summary>
    public FoxAgent WithHybridMemory(int shortTermSize = 50, string? longTermPath = null)
    {
        _agent.Memory = new HybridMemory(shortTermSize, longTermPath);
        return this;
    }

    /// <summary>
    /// Execute a task with the agent
    /// </summary>
    public async Task<AgentResult> ExecuteAsync(string task)
    {
        _logger?.LogInformation("Agent '{AgentName}' executing task: {Task}", Name, task.Length > 100 ? task[..100] + "..." : task);
        
        // Populate agent's enabled skills if empty
        if (EnabledSkills.Count == 0 && _agent.Config.SkillRegistry != null)
        {
            _agent.EnabledSkills.AddRange(_agent.Config.SkillRegistry.GetAll());
            _logger?.LogInformation("Auto-populated agent '{AgentName}' with {SkillCount} available skills", Name, _agent.EnabledSkills.Count);
        }
        return await ProcessAsync(task, _agent.DefaultConversationId);
    }

    public async Task<AgentResult> ProcessAsync(string task, string? conversationId = null, CancellationToken cancellationToken = default)
    {
        conversationId ??= Guid.NewGuid().ToString("N");

        _logger?.LogInformation("Agent '{AgentName}' processing task in conversation {ConversationId}", Name, conversationId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        double TimeoutSeconds = 3600;
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        var timeoutToken = cts.Token;

        try
        {
            var agent = _chatAgent;

            var thread = ConversationStore.GetThread(conversationId);
            if (thread == null)
            {
                _logger?.LogDebug("Creating new conversation thread for {ConversationId}", conversationId);
                thread = await agent.CreateSessionAsync(timeoutToken);
                ConversationStore.SaveThread(conversationId, thread);
            }

            var runOptions = new AgentRunOptions();
            var response = await agent.RunAsync(task, thread, options: runOptions, cancellationToken: timeoutToken);

            var responseText = response.Text ?? "I apologize, but I wasn't able to generate a response.";
            _logger?.LogInformation("Agent '{AgentName}' completed task in conversation {ConversationId}", Name, conversationId);
            
            var result = new AgentResult { Success = true, Output = responseText };
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Agent '{AgentName}' task timed out after {Timeout} seconds", Name, TimeoutSeconds);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Agent '{AgentName}' failed to process task in conversation {ConversationId}", Name, conversationId);
            throw;
        }
    }




    /// <summary>
    /// Spawn a sub-agent
    /// </summary>
    public FoxAgent SpawnSubAgent(AgentSpawnConfig config)
    {
        var agentConfig = new AgentConfig
        {
            Name = config.Name,
            Description = config.Description,
            SystemPrompt = config.SystemPrompt,
            MaxIterations = config.MaxIterations,
            Tools = config.Tools ?? new List<ToolDefinition>(),
            SkillRegistry = _agent.Config.SkillRegistry
        };

        var subAgent = SpawnSubAgentInternal(_agent, agentConfig);
        var foxSubAgent = new FoxAgent(_chatAgent, subAgent.Config, ConversationStore)
        {
            Role = config.Role ?? Role  // Inherit role from parent by default
        };

        // Handle skill inheritance
        if (config.InheritEnabledSkills && EnabledSkills.Any())
        {
            // Copy parent skills
            foxSubAgent.EnabledSkills.AddRange(EnabledSkills);

            // Add additional skills if specified
            if (config.AdditionalSkills?.Any() == true)
            {
                // TODO: Load additional skills from SkillRegistry
            }

            // Remove forbidden skills
            if (config.ForbiddenSkills?.Any() == true)
            {
                foreach (var forbidden in config.ForbiddenSkills)
                {
                    foxSubAgent.DisableSkill(forbidden);
                }
            }
        }

        return foxSubAgent;
    }

    private Agent SpawnSubAgentInternal(Agent parent, AgentConfig config)
    {
        _logger?.LogInformation("Spawning sub-agent '{SubAgentName}' from parent '{ParentName}'", config.Name, parent.Config.Name);
        
        var agent = new Agent
        {
            Config = config,
            Parent = parent,
            Status = AgentStatus.Idle
        };

        if (parent.Memory != null)
        {
            agent.Memory = new Memory.ShortTermMemory();
            _logger?.LogDebug("Sub-agent '{SubAgentName}' inherited memory from parent", config.Name);
        }

        foreach (var tool in parent.Config.Tools)
        {
            if (!agent.Config.Tools.Any(t => t.Name == tool.Name))
            {
                agent.Config.Tools.Add(tool);
            }
        }

        parent.SubAgents.Add(agent);
        _logger?.LogInformation("Sub-agent '{SubAgentName}' spawned successfully with {ToolCount} tools", config.Name, agent.Config.Tools.Count);

        return agent;
    }

    /// <summary>
    /// Get conversation history
    /// </summary>
    //public List<Message> GetHistory()
    //{
    //    return _conversationStore.GetThread(_agent.DefaultConversationId).StateBag  // _agent.ConversationHistory.ToList();
    //}

    /// <summary>
    /// Clear conversation history
    /// </summary>
    public void ClearHistory()
    {
        _agent.ConversationHistory.Clear();
    }

    /// <summary>
    /// Add a system message to the conversation
    /// </summary>
    //public void AddSystemMessage(string content)
    //{
    //    _agent.ConversationHistory.Insert(0, new Message(MessageRole.System, content));
    //}

    /// <summary>
    /// Get agent info
    /// </summary>
    public AgentInfo GetInfo()
    {
        return new AgentInfo
        {
            Id = _agent.Config.Id,
            Name = _agent.Config.Name,
            Description = _agent.Config.Description,
            Status = _agent.Status,
            SubAgentCount = _agent.SubAgents.Count,
            MessageCount = _agent.ConversationHistory.Count,
            CreatedAt = _agent.CreatedAt,
            LastActiveAt = _agent.LastActiveAt,
            HasMemory = _agent.Memory != null,
            ToolCount = _agent.Config.Tools.Count,
            EnabledSkillCount = EnabledSkills.Count,
            Role = Role
        };
    }

    /// <summary>
    /// Get supported skills as a filter for routing
    /// </summary>
    public SkillFilter GetSupportedSkills()
    {
        var skillNames = EnabledSkills.Select(s => s.Name).ToList();
        var capabilities = EnabledSkills
            .Where(s => s.Metadata != null)
            .SelectMany(s => s.Metadata!.Capabilities)
            .Distinct()
            .ToList();

        return new SkillFilter
        {
            RequiredSkills = skillNames,
            RequiredCapabilities = capabilities
        };
    }

    /// <summary>
    /// Enable a skill for this agent
    /// </summary>
    public async Task EnableSkillAsync(Skill skill)
    {
        if (!EnabledSkills.Any(s => s.Name == skill.Name))
        {
            EnabledSkills.Add(skill);
            // Trigger any skill registration hooks
            if (skill is ISkillPlugin plugin)
            {
                var context = new SkillRegistrationContext(
                    new DummyLogger<Skill>(),
                    null,
                    null);
                await plugin.OnRegisterAsync(context);
            }
        }
    }

    /// <summary>
    /// Disable a skill for this agent
    /// </summary>
    public void DisableSkill(string skillName)
    {
        EnabledSkills.RemoveAll(s => s.Name == skillName);
    }

    /// <summary>
    /// Get all skill capabilities for this agent
    /// </summary>
    public List<string> GetSkillCapabilities()
    {
        return EnabledSkills
            .Where(s => s.Metadata != null)
            .SelectMany(s => s.Metadata!.Capabilities)
            .Distinct()
            .ToList();
    }
}

/// <summary>
/// Agent information
/// </summary>
public class AgentInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AgentStatus Status { get; set; }
    public int SubAgentCount { get; set; }
    public int MessageCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastActiveAt { get; set; }
    public bool HasMemory { get; set; }
    public int ToolCount { get; set; }
    public int EnabledSkillCount { get; set; }
    public string Role { get; set; } = "default";
}

/// <summary>
/// Builder for creating agents
/// </summary>
public class AgentBuilder
{
    private readonly ToolRegistry _toolRegistry;
    private AgentConfig _config = new();
    private IMemory? _memory;
    private SkillRegistry? _skillRegistry = null;
    private MCPClient? _mcpClient;
    private IConversationStore? _conversationStore;
    private ILogger<FoxAgent>? _logger;
    private IChatClient? _chatClient;

    public AgentBuilder(ToolRegistry toolRegistry)
    {
        _toolRegistry = toolRegistry;
    }

    public AgentBuilder WithName(string name)
    {
        _config.Name = name;
        return this;
    }

    public AgentBuilder WithDescription(string description)
    {
        _config.Description = description;
        return this;
    }

    public AgentBuilder WithSystemPrompt(string systemPrompt)
    {
        _config.SystemPrompt = systemPrompt;
        return this;
    }

    public AgentBuilder WithMaxIterations(int maxIterations)
    {
        _config.MaxIterations = maxIterations;
        return this;
    }

    public AgentBuilder WithTemperature(double temperature)
    {
        _config.Temperature = temperature;
        return this;
    }

    public AgentBuilder WithMemory(IMemory memory)
    {
        _memory = memory;
        return this;
    }

    public AgentBuilder WithConversationStore(IConversationStore conversationStore)
    {
        _conversationStore = conversationStore;
        return this;
    }

    public AgentBuilder WithChatClient(IChatClient chatClient)
    {
        _chatClient = chatClient;
        return this;
    }

    public AgentBuilder WithHybridMemory(int shortTermSize = 50, string? longTermPath = null)
    {
        _memory = new HybridMemory(shortTermSize, longTermPath);
        return this;
    }

    public AgentBuilder WithSkillsRegistry(SkillRegistry skillRegistry)
    {
        _skillRegistry = skillRegistry;
        _config.SkillRegistry = skillRegistry;
        return this;
    }

    public AgentBuilder WithMCPClient(MCPClient mcpClient)
    {
        _mcpClient = mcpClient;
        return this;
    }
    
    public AgentBuilder WithLogger(ILogger<FoxAgent> logger)
    {
        _logger = logger;
        return this;
    }


    private string BuildSystemMessage()
    {
        // Get base prompt depending on agent configuration or use default
        var basePrompt = _config.SystemPrompt ?? SystemPromptConfig.AgentPrompts.BaseAssistant;

        var builder = new SystemPromptBuilder()
            .WithPersona(basePrompt);

        // Add available tools if any
        if (_config.Tools.Count > 0)
        {
            var toolNames = _config.Tools
                .Select(t => $"{t.Name}: {t.Description}")
                .ToArray();
            builder.WithTools(toolNames);
            builder.WithToolInstructions(false);
        }

        return builder.Build();
    }

    private List<ToolDefinition> GetAvailableTools()
    {
        var tools = new List<ToolDefinition>();

        // Add configured tools
        tools.AddRange(_toolRegistry.GetDefinitions());

        // Add tools from enabled skills (including Composio toolkit tools)
        var enabledSkills = _skillRegistry?.GetEnabledSkills();
        if (enabledSkills != null)
            foreach (var skill in enabledSkills)
            {
                try
                {
                    var skillTools = skill.GetTools();
                    foreach (var tool in skillTools)
                    {
                        if (tools.Any(t => t.Name.Equals(tool.Name, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        // Convert skill tool to tool definition for the LLM
                        var parameters = new Dictionary<string, Models.ToolParameter>();
                        foreach (var kv in tool.Parameters)
                        {
                            parameters[kv.Key] = new Models.ToolParameter
                            {
                                Type = kv.Value.Type,
                                Description = kv.Value.Description,
                                Required = kv.Value.Required,
                                Default = kv.Value.Default,
                                Pattern = kv.Value.Pattern,
                                MinLength = kv.Value.MinLength,
                                MaxLength = kv.Value.MaxLength,
                                Minimum = kv.Value.Minimum,
                                Maximum = kv.Value.Maximum
                            };
                        }

                        tools.Add(new ToolDefinition
                        {
                            Name = tool.Name,
                            Description = tool.Description,
                            Parameters = parameters
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to get tools from skill {SkillName}", skill.Name);
                }
            }

        // Add spawn agent tool
        tools.Add(new ToolDefinition
        {
            Name = "spawn_agent",
            Description = "Spawn a sub-agent to handle a subtask",
            Parameters = new Dictionary<string, Models.ToolParameter>
            {
                ["name"] = new() { Type = "string", Description = "Name of the sub-agent", Required = true },
                ["description"] = new() { Type = "string", Description = "Description of the sub-agent's task", Required = true },
                ["task"] = new() { Type = "string", Description = "Task for the sub-agent", Required = true }
            }
        });

        return tools;
    }

    /// <summary>
    /// Search for a tool in the agent's enabled skills by name
    /// </summary>
    private ITool? FindToolInAgentSkills(string toolName)
    {
        try
        {
            // Search each enabled skill's tools for a match
            var enabledSkills = _skillRegistry?.GetEnabledSkills();
            if (enabledSkills != null)
                foreach (var skill in enabledSkills)
                {
                    var skillTools = skill.GetTools();
                    var matchedTool = skillTools.FirstOrDefault(t =>
                            t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase) ||
                            t.Name.EndsWith($":{toolName}") || // Handle toolkit:tool format
                            toolName.EndsWith($":{t.Name}") // Handle reverse format
                    );

                    if (matchedTool != null)
                    {
                        _logger?.LogInformation($"Found tool '{toolName}' in skill '{skill.Name}'");
                        return matchedTool;
                    }
                }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error searching for tool in agent skills");
        }

        return null;
    }

    private async Task<ToolResult> ExecuteToolAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        // Handle spawn_agent specially
        if (toolName == "spawn_agent")
        {
            return ToolResult.Ok("Sub-agent spawning is handled by the runtime.");
        }

        // First, try to get tool from global registry
        var tool = _toolRegistry.Get(toolName);

        // If not found, search in agent's enabled skills (for Composio and other skill-based tools)
        if (tool == null)
        {
            tool = FindToolInAgentSkills(toolName);
        }

        if (tool == null)
        {
            // Log available tools for debugging
            var availableTools = _toolRegistry.GetAll();
            var toolNames = string.Join(", ", availableTools.Select(t => t.Name));
            var enabledSkills = _skillRegistry?.GetEnabledSkills();
            if (enabledSkills != null)
            {
                var skillToolsInfo = string.Join(", ", enabledSkills.SelectMany(s => s.GetTools()).Select(t => t.Name));
                _logger?.LogWarning($"Tool '{toolName}' not found. Global tools: {toolNames}. Skill tools: {skillToolsInfo}");
            }

            return ToolResult.Fail($"Tool not found: {toolName}");
        }

        try
        {
            var result = await tool.ExecuteAsync(arguments);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"Error executing tool {toolName}");
            return ToolResult.Fail($"Error: {ex.Message}");
        }
    }

    public static JsonElement BuildJsonSchema(ToolDefinition tool)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var (name, param) in tool.Parameters)
        {
            JsonObject prop = new()
            {
                ["type"] = param.Type
            };

            if (!string.IsNullOrEmpty(param.Description))
                prop["description"] = param.Description;

            if (param.Pattern != null)
                prop["pattern"] = param.Pattern;

            if (param.MinLength.HasValue)
                prop["minLength"] = param.MinLength;

            if (param.MaxLength.HasValue)
                prop["maxLength"] = param.MaxLength;

            if (param.Minimum.HasValue)
                prop["minimum"] = param.Minimum;

            if (param.Maximum.HasValue)
                prop["maximum"] = param.Maximum;

            properties[name] = prop;

            if (param.Required)
                required.Add(name);
        }

        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };

        if (required.Count > 0)
            root["required"] = required;

        return JsonSerializer.SerializeToElement(root);
    }

    static object? ConvertJsonValue(object? value)
    {
        if (value is JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object?>>(el.GetRawText()),
                JsonValueKind.Array => JsonSerializer.Deserialize<List<object?>>(el.GetRawText()),
                _ => el.GetRawText()
            };
        }

        return value;
    }

    private AITool CreateAgentTool(ToolDefinition toolDefinition)
    {
        var toolName = toolDefinition.Name;
        var toolDescription = toolDefinition.Description ?? $"Tool: {toolName}";

        //if (toolDefinition.JsonSchema.ValueKind != JsonValueKind.Undefined && toolDefinition.JsonSchema.ValueKind != JsonValueKind.Null)
        //{
        //    var schemaJson = JsonSerializer.Serialize(toolDefinition.JsonSchema, new JsonSerializerOptions { WriteIndented = false });
        //    toolDescription += $"\nParameters (JSON Schema): {schemaJson}";
        //}

        var schema = BuildJsonSchema(toolDefinition);
        

        var tool = AIFunctionFactory.Create(
            async (AIFunctionArguments? args, CancellationToken ct) =>
            {
                //var jsonArgs = args is null or { Count: 0 }
                //    ? "{}"
                //    : JsonSerializer.Serialize(args);

                var dict = args?.ToDictionary(k => k.Key, v => ConvertJsonValue(v.Value))
                           ?? new Dictionary<string, object?>();

                var res = await ExecuteToolAsync(toolDefinition.Name, dict, ct);
                return res;
                //return JsonSerializer.Serialize(res);

                //return await InvokeMcpToolAsync(toolName, jsonArgs, ct);
            },
            new AIFunctionFactoryOptions
            {
                Name = toolName,
                //Description = toolDescription+$"\nParameters (JSON Schema): {schema}",
                Description = toolDescription, //if using DynamicSchemaFunction
                //JsonSchemaCreateOptions = new AIJsonSchemaCreateOptions()
                //{
                //    // Dynamically provide descriptions for parameters
                //    ParameterDescriptionProvider=  (parameter) =>
                //    {
                //        if (parameter.Name == "dynamic_id") return "The unique ID from your external system";
                //        return toolDescription; // Fallback to default
                //    }
                //}
            });
        AIFunction executableDynamicFunc = new DynamicSchemaFunction(tool, schema);
        _logger?.LogDebug("Created Agent Framework tool: {ToolName}", toolName);
        return executableDynamicFunc;
        //return AIFunctionFactory.CreateDeclaration(toolName, toolDescription, schema);
        //chat  history
        //https://github.com/microsoft/agent-framework/blob/1b7940c91e045c563faafe11d5b03067f4ea7b16/docs/decisions/0015-agent-run-context.md?plain=1#L36
        return tool;
    }

    public sealed class DynamicSchemaFunction(AIFunction inner, JsonElement customSchema) : DelegatingAIFunction(inner)
    {
        // Return the dynamic schema instead of the one inferred from the delegate
        public override JsonElement JsonSchema => customSchema;
    }
    
    public FoxAgent Build(string apiKey, string? baseUrl = null)
    {
        if (string.IsNullOrEmpty(_config.Name))
        {
            _config.Name = "AgentFox-" + Guid.NewGuid().ToString("N")[..8];
        }

        var chatClient = _chatClient;

        var tools = GetAvailableTools();
        if (_config.Tools.Count == 0)
        {
            _config.Tools.AddRange(tools);
        }
        //var toolCalls = tools?.Select(t => new
        //{
        //    type = "function",
        //    function = new
        //    {
        //        name = t.Name,
        //        description = t.Description,
        //        parameters = new
        //        {
        //            type = "object",
        //            properties = t.Parameters.ToDictionary(p => p.Key,
        //                p => new { type = p.Value.Type, description = p.Value.Description }),
        //            required = t.Parameters.Where(p => p.Value.Required).Select(p => p.Key)
        //        }
        //    }
        //});

        var agentTools = new List<AITool>();

        foreach (var toolDefinition in tools)
        {
            try
            {
                var tool = CreateAgentTool(toolDefinition);
                agentTools.Add(tool);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to create Agent Framework tool for MCP tool: {ToolName}", toolDefinition.Name);
            }
        }

        var systemPrompt = BuildSystemMessage();
        var agent = chatClient.AsAIAgent(systemPrompt, tools: agentTools);
        
        _logger?.LogInformation("Building FoxAgent '{AgentName}' with {ToolCount} tools", _config.Name, tools.Count);
        
        var foxAgent = new FoxAgent(agent, _config, _conversationStore!, _logger);

        // Apply memory configuration if set
        if (_memory != null)
        {
            foxAgent.WithMemory(_memory);
        }

        _logger?.LogInformation("FoxAgent '{AgentName}' built successfully", _config.Name);
        
        return foxAgent;
    }
}

