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
using SystemPromptBuilder = AgentFox.LLM.SystemPromptBuilder;

namespace AgentFox.Agents;

/// <summary>
/// Main agent class that orchestrates all functionality
/// </summary>
public class FoxAgent
{
    private readonly Agent _agent;
    private readonly ChatClientAgent _chatAgent;
    
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
    

    public FoxAgent(ChatClientAgent agent, AgentConfig config, IConversationStore store)
    {
        _agent = new Agent
        {
            Config = config,
            Status = AgentStatus.Idle,
            ConversationStore = store
        };
        _chatAgent = agent;

        //// Add tool definitions from registry
        //foreach (var toolDef in runtime.ToolRegistry.GetDefinitions())
        //{
        //    if (!_agent.Config.Tools.Any(t => t.Name == toolDef.Name))
        //    {
        //        _agent.Config.Tools.Add(toolDef);
        //    }
        //}
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
        // Populate agent's enabled skills if empty
        if (EnabledSkills.Count == 0 && _agent.Config.SkillRegistry != null)
        {
            _agent.EnabledSkills.AddRange(_agent.Config.SkillRegistry.GetAll());
            //Logger?.LogInformation($"Auto-populated agent with {agent.EnabledSkills.Count} available skills");
        }
        return await ProcessAsync(task, _agent.DefaultConversationId);
    }

    public async Task<AgentResult> ProcessAsync(string task, string? conversationId=null, CancellationToken cancellationToken = default)
    {
        conversationId ??= Guid.NewGuid().ToString("N");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        double TimeoutSeconds=3600;
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        var timeoutToken = cts.Token;

        try
        {
            var agent = _chatAgent;

            var thread = ConversationStore.GetThread(conversationId);
            if (thread == null)
            {
                thread = await agent.CreateSessionAsync(timeoutToken);
                ConversationStore.SaveThread(conversationId, thread);
            }

            var runOptions = new AgentRunOptions();
            var response = await agent.RunAsync(task, thread, options: runOptions, cancellationToken: timeoutToken);

            var responseText = response.Text ?? "I apologize, but I wasn't able to generate a response.";
            var result = new AgentResult { Success = true, Output = responseText};
            return result;
            //_logger.LogInformation(
            //    "Agent completed. Response: {Response}",
            //    responseText);

            //return new Dtos.ChatResponse
            //{
            //    Answer = responseText,
            //    ConversationId = conversationId,

            //};
        }
        catch (Exception ex)
        {
            //_logger.LogError(ex, "Error processing chat request");
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
        var agent = new Agent
        {
            Config = config,
            Parent = parent,
            Status = AgentStatus.Idle
        };

        if (parent.Memory != null)
        {
            agent.Memory = new Memory.ShortTermMemory();
        }

        foreach (var tool in parent.Config.Tools)
        {
            if (!agent.Config.Tools.Any(t => t.Name == tool.Name))
            {
                agent.Config.Tools.Add(tool);
            }
        }

        parent.SubAgents.Add(agent);
        //Logger?.LogInformation($"Spawned sub-agent '{config.Name}' from parent '{parent.Config.Name}'");

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
    private IAgentRuntime _runtime;
    private AgentConfig _config = new();
    private IMemory? _memory;
    private SkillRegistry? _skillRegistry = null;
    private MCPClient? _mcpClient;
    private IConversationStore _conversationStore;

    public AgentBuilder(ToolRegistry toolRegistry)
    {
        _toolRegistry = toolRegistry;
        _runtime = new DefaultAgentRuntime(toolRegistry);
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

    public AgentBuilder WithLLMProvider(ILLMProvider provider)
    {
        _config.Model = provider.DefaultConfig.Model;
        _runtime = new LLMEnabledRuntime(_toolRegistry, provider, skillRegistry: _skillRegistry);
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
        var enabledSkills = _skillRegistry?.GetAll();
        if (enabledSkills != null)
            foreach (var skill in enabledSkills)
            {
                try
                {
                    var skillTools = skill.GetTools();
                    foreach (var tool in skillTools)
                    {
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
                    //_runtime.Logger?.LogWarning(ex, $"Failed to get tools from skill {skill.Name}");
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
                        _runtime.Logger?.LogInformation($"Found tool '{toolName}' in skill '{skill.Name}'");
                        return matchedTool;
                    }
                }
        }
        catch (Exception ex)
        {
            _runtime.Logger?.LogWarning(ex, "Error searching for tool in agent skills");
        }

        return null;
    }

    private async Task<ToolResult> ExecuteToolAsync2(ToolCall toolCall, Agent agent)
    {
        // Handle spawn_agent specially
        if (toolCall.ToolName == "spawn_agent")
        {
            return ToolResult.Ok("Sub-agent spawning is handled by the runtime.");
        }

        // First, try to get tool from global registry
        var tool = _runtime.ToolRegistry.Get(toolCall.ToolName);

        // If not found, search in agent's enabled skills (for Composio and other skill-based tools)
        if (tool == null)
        {
            tool = FindToolInAgentSkills(toolCall.ToolName);
        }


        if (tool == null)
        {
            // Log available tools for debugging
            var availableTools = _runtime.ToolRegistry.GetAll();
            var toolNames = string.Join(", ", availableTools.Select(t => t.Name));
            var skillToolsInfo = string.Join(", ", agent.EnabledSkills.SelectMany(s => s.GetTools()).Select(t => t.Name));
            _runtime.Logger?.LogWarning($"Tool '{toolCall.ToolName}' not found. Global tools: {toolNames}. Skill tools: {skillToolsInfo}");

            return ToolResult.Fail($"Tool not found: {toolCall.ToolName}");
        }

        try
        {
            var result = await tool.ExecuteAsync(toolCall.Arguments);
            toolCall.IsCompleted = true;
            toolCall.Result = result.Output;
            return result;
        }
        catch (Exception ex)
        {
            _runtime.Logger?.LogError(ex, $"Error executing tool {toolCall.ToolName}");
            return ToolResult.Fail(ex.Message);
        }
    }

    private async Task<ToolResult> ExecuteToolAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        // Handle spawn_agent specially
        if (toolName == "spawn_agent")
        {
            return ToolResult.Ok("Sub-agent spawning is handled by the runtime.");
        }

        // First, try to get tool from global registry
        var tool = _runtime.ToolRegistry.Get(toolName);

        // If not found, search in agent's enabled skills (for Composio and other skill-based tools)
        if (tool == null)
        {
            tool = FindToolInAgentSkills(toolName);
        }


        if (tool == null)
        {
            // Log available tools for debugging
            var availableTools = _runtime.ToolRegistry.GetAll();
            var toolNames = string.Join(", ", availableTools.Select(t => t.Name));
            var enabledSkills = _skillRegistry?.GetEnabledSkills();
            if (enabledSkills != null)
            {
                var skillToolsInfo = string.Join(", ", enabledSkills.SelectMany(s => s.GetTools()).Select(t => t.Name));
                _runtime.Logger?.LogWarning($"Tool '{toolName}' not found. Global tools: {toolNames}. Skill tools: {skillToolsInfo}");
            }

            return ToolResult.Fail($"Tool not found: {toolName}");
        }

        try
        {
            var result = await tool.ExecuteAsync(arguments);
            return result;//.Output;
        }
        catch (Exception ex)
        {
            _runtime.Logger?.LogError(ex, $"Error executing tool {toolName}");
            throw;
            return ToolResult.Fail("Error: " + ex.Message);
        }
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
        
        var tool = AIFunctionFactory.Create(
            async (AIFunctionArguments? args, CancellationToken ct) =>
            {
                var jsonArgs = args is null or { Count: 0 }
                    ? "{}"
                    : JsonSerializer.Serialize(args);

                Dictionary<string, object?> dict = new Dictionary<string, object?>(args);
                var dict2 = args.ToDictionary(kv => kv.Key, kv => kv.Value);
                var res = await ExecuteToolAsync(toolDefinition.Name, dict, ct);
                    return res;
                
                //return await InvokeMcpToolAsync(toolName, jsonArgs, ct);
            },
            new AIFunctionFactoryOptions
            {
                Name = toolName,
                Description = toolDescription
            });

        //_logger.LogInformation("Created Agent Framework tool for MCP tool: {ToolName}", toolName);

        return tool;
    }


    public FoxAgent Build(string apiKey, string? baseUrl=null)
    {
        if (string.IsNullOrEmpty(_config.Name))
        {
            _config.Name = "AgentFox-" + Guid.NewGuid().ToString("N")[..8];
        }


        var keyCredential = new ApiKeyCredential(apiKey);
        var openAiClient = new OpenAIClient(keyCredential, new OpenAIClientOptions(){Endpoint = new Uri(baseUrl)});
        var chatClient = openAiClient.GetChatClient(_config.Model);

        var tools = GetAvailableTools();
        if (_config.Tools.Count==0)
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
                 _runtime.Logger.LogWarning(ex, "Failed to create Agent Framework tool for MCP tool: {ToolName}", toolDefinition.Name);
            }
        }


        var systemPrompt = BuildSystemMessage();
        var agent = chatClient.AsAIAgent(systemPrompt, tools: agentTools);
        var foxAgent = new FoxAgent(agent, _config, _conversationStore);
        //if (_memory != null)
        //{
        //    agent.WithMemory(_memory);
        //}
        
        return foxAgent;
    }
}

