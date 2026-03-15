using AgentFox.Agents;
using AgentFox.Models;
using AgentFox.Tools;
using AgentFox.Skills;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentFox.LLM;

/// <summary>
/// LLM-powered agent executor
/// </summary>
public class LLMExecutor : IAgentExecutor
{
    private readonly IAgentRuntime _runtime;
    private readonly ILLMProvider _llm;
    private readonly SkillRegistry? _skillRegistry;
    
    public LLMExecutor(IAgentRuntime runtime, ILLMProvider llm, SkillRegistry? skillRegistry = null)
    {
        _runtime = runtime;
        _llm = llm;
        _skillRegistry = skillRegistry;
    }
    
    public async Task<AgentResult> ExecuteAsync(Agent agent, string task)
    {
        var startTime = DateTime.UtcNow;
        var result = new AgentResult { Success = false };
        
        try
        {
            // Populate agent's enabled skills if empty
            if (agent.EnabledSkills.Count == 0 && _skillRegistry != null)
            {
                agent.EnabledSkills.AddRange(_skillRegistry.GetAll());
                _runtime.Logger?.LogInformation($"Auto-populated agent with {agent.EnabledSkills.Count} available skills");
            }
            
            agent.ConversationHistory.Add(new Message(MessageRole.User, task));
            agent.Status = AgentStatus.Thinking;
            
            // Build system prompt
            var systemMessage = BuildSystemMessage(agent);
            var messages = new List<Message> { new(MessageRole.System, systemMessage) };
            messages.AddRange(agent.ConversationHistory.Where(m => m.Role != MessageRole.System));
            
            // Get available tools including skill-based tools
            var availableTools = GetAvailableTools(agent);
            
            // Build LLM config
            var llmConfig = new LLMConfig
            {
                Model = agent.Config.Model ?? _llm.DefaultConfig?.Model,
                Temperature = agent.Config.Temperature,
                MaxTokens = agent.Config.MaxTokens
            };
            
            // Call LLM
            var response = await _llm.GenerateAsync(messages, availableTools, llmConfig);
            
            // Add assistant message
            agent.ConversationHistory.Add(new Message(MessageRole.Assistant, response));
            
            // Parse tool calls from response (may trigger a single JSON-fix retry)
            var toolCalls = await ParseToolCallsAsync(agent, response, llmConfig);
            
            if (toolCalls.Count > 0)
            {
                // Continue executing tool calls in a loop until we get a response without tools
                var finalResponse = await ExecuteToolCallsLoop(agent, toolCalls, result, llmConfig);
                result.Output = finalResponse;
                result.Success = true;
            }
            else
            {
                result.Output = response;
                result.Success = true;
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            agent.Status = AgentStatus.Error;
            _runtime.Logger?.LogError(ex, "LLM execution failed");
        }
        
        result.Duration = DateTime.UtcNow - startTime;
        agent.Status = result.Success ? AgentStatus.Completed : AgentStatus.Error;
        
        return result;
    }
    
    private async Task<string> ExecuteToolCallsLoop(Agent agent, List<ToolCall> toolCalls, AgentResult result, LLMConfig llmConfig)
    {
        while (toolCalls.Count > 0)
        {
            // Execute all tool calls
            foreach (var toolCall in toolCalls)
            {
                agent.Status = AgentStatus.ExecutingTool;
                _runtime.Logger?.LogInformation($"Executing tool: {toolCall.ToolName}");
                
                var toolResult = await ExecuteToolAsync(toolCall, agent);
                result.ToolCalls.Add(toolCall);
                
                agent.ConversationHistory.Add(new Message
                {
                    Role = MessageRole.Tool,
                    Content = toolResult.Output,
                    ToolCallId = toolCall.Id,
                    ToolName = toolCall.ToolName
                });
            }
            
            // Continue conversation with tool results
            agent.Status = AgentStatus.Thinking;
            var messagesWithResult = new List<Message>(agent.ConversationHistory);
            messagesWithResult.Add(new Message(MessageRole.System, "Based on the tool results, provide your final answer."));
            var response = await _llm.GenerateAsync(
                messagesWithResult,
                null,
                llmConfig
            );
            
            // Add assistant message
            agent.ConversationHistory.Add(new Message(MessageRole.Assistant, response));
            
            // Check if the response contains more tool calls (may trigger a single JSON-fix retry)
            toolCalls = await ParseToolCallsAsync(agent, response, llmConfig);
            
            if (toolCalls.Count == 0)
            {
                // No more tool calls, return the final response
                return response;
            }
        }
        
        // Should not reach here, but return empty string as fallback
        return string.Empty;
    }
    
    private string BuildSystemMessage(Agent agent)
    {
        // Get base prompt depending on agent configuration or use default
        var basePrompt = agent.Config.SystemPrompt ?? SystemPromptConfig.AgentPrompts.BaseAssistant;
        
        var builder = new SystemPromptBuilder()
            .WithPersona(basePrompt);
        
        // Add available tools if any
        if (agent.Config.Tools.Count > 0)
        {
            var toolNames = agent.Config.Tools
                .Select(t => $"{t.Name}: {t.Description}")
                .ToArray();
            builder.WithTools(toolNames);
            builder.WithToolInstructions(true);
        }
        
        return builder.Build();
    }
    
    private List<ToolDefinition> GetAvailableTools(Agent agent)
    {
        var tools = new List<ToolDefinition>();
        
        // Add configured tools
        tools.AddRange(agent.Config.Tools);
        
        // Add tools from enabled skills (including Composio toolkit tools)
        foreach (var skill in agent.EnabledSkills)
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
                _runtime.Logger?.LogWarning(ex, $"Failed to get tools from skill {skill.Name}");
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
    
    private Task<List<ToolCall>> ParseToolCallsAsync(Agent agent, string response, LLMConfig llmConfig)
        => ParseToolCallsInternalAsync(agent, response, llmConfig, isRetry: false);

    private async Task<List<ToolCall>> ParseToolCallsInternalAsync(Agent agent, string response, LLMConfig llmConfig, bool isRetry)
    {
        var toolCalls = new List<ToolCall>();
        
        try
        {
            // Try to parse JSON response
            if (response.Contains("tool_calls") || response.Contains("tool_uses"))
            {
                // Extract JSON from response (response may contain text before/after JSON)
                string jsonToParse = response;
                
                // If response contains text and JSON, try to extract just the JSON object
                int jsonStart = response.IndexOf('{');
                if (jsonStart > 0)
                {
                    // Find the matching closing brace
                    int braceCount = 0;
                    int jsonEnd = -1;
                    for (int i = jsonStart; i < response.Length; i++)
                    {
                        if (response[i] == '{') braceCount++;
                        else if (response[i] == '}') 
                        {
                            braceCount--;
                            if (braceCount == 0)
                            {
                                jsonEnd = i;
                                break;
                            }
                        }
                    }
                    
                    if (jsonEnd > jsonStart)
                    {
                        jsonToParse = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    }
                }
                
                var json = JObject.Parse(jsonToParse);
                
                // Try OpenAI format: tool_calls[].function.name and tool_calls[].function.arguments
                var calls = json["tool_calls"]?.ToArray();
                
                if (calls != null)
                {
                    foreach (var call in calls)
                    {
                        var toolCall = new ToolCall();
                        
                        // OpenAI format: function.name
                        var functionObj = call["function"];
                        if (functionObj != null)
                        {
                            toolCall.ToolName = functionObj["name"]?.ToString() ?? "";
                            
                            // Arguments come as a JSON string, need to parse it
                            var argsString = functionObj["arguments"]?.ToString();
                            if (!string.IsNullOrEmpty(argsString))
                            {
                                try
                                {
                                    var argsJson = JObject.Parse(argsString);
                                    foreach (var prop in argsJson.Properties())
                                    {
                                        toolCall.Arguments[prop.Name] = prop.Value?.ToString();
                                    }
                                }
                                catch
                                {
                                    // If not valid JSON, treat as single string argument
                                    toolCall.Arguments["input"] = argsString;
                                }
                            }
                        }
                        else
                        {
                            // Fallback: direct name/arguments (some providers use this)
                            toolCall.ToolName = call["name"]?.ToString() ?? "";
                            
                            var args = call["arguments"] as JObject;
                            if (args != null)
                            {
                                foreach (var prop in args.Properties())
                                {
                                    toolCall.Arguments[prop.Name] = prop.Value?.ToString();
                                }
                            }
                        }
                        
                        // Get tool call ID if present
                        var id = call["id"]?.ToString();
                        if (!string.IsNullOrEmpty(id))
                        {
                            toolCall.Id = id;
                        }
                        
                        if (!string.IsNullOrEmpty(toolCall.ToolName))
                        {
                            toolCalls.Add(toolCall);
                        }
                    }
                }
                
                // Try Anthropic format: tool_uses[].name and tool_uses[].input
                if (toolCalls.Count == 0)
                {
                    var toolUses = json["tool_uses"]?.ToArray();
                    if (toolUses != null)
                    {
                        foreach (var toolUse in toolUses)
                        {
                            var toolCall = new ToolCall
                            {
                                ToolName = toolUse["name"]?.ToString() ?? ""
                            };
                            
                            var input = toolUse["input"] as JObject;
                            if (input != null)
                            {
                                foreach (var prop in input.Properties())
                                {
                                    toolCall.Arguments[prop.Name] = prop.Value?.ToString();
                                }
                            }
                            
                            var id = toolUse["id"]?.ToString();
                            if (!string.IsNullOrEmpty(id))
                            {
                                toolCall.Id = id;
                            }
                            
                            if (!string.IsNullOrEmpty(toolCall.ToolName))
                            {
                                toolCalls.Add(toolCall);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _runtime.Logger?.LogWarning(ex, $"Failed to parse tool calls from response: {response}");

            // If the assistant attempted tool calls but JSON was invalid, immediately
            // ask the LLM to fix the JSON and retry ONCE synchronously.
            if (!isRetry && (response.Contains("tool_calls") || response.Contains("tool_uses")))
            {
                var repairMessages = new List<Message>(agent.ConversationHistory)
                {
                    new(MessageRole.System,
                        "Your previous response attempted to call tools but the JSON was invalid and could not be parsed. " +
                        "You must now respond with ONLY a single valid JSON object containing the corrected tool_calls/tool_uses, " +
                        "with no natural language, no comments, and no text before or after the JSON."),
                    new(MessageRole.Assistant, response)
                };

                var fixedResponse = await _llm.GenerateAsync(repairMessages, null, llmConfig);

                // Record the fixed response in the conversation
                agent.ConversationHistory.Add(new Message(MessageRole.Assistant, fixedResponse));

                // Retry parsing once with the fixed response
                return await ParseToolCallsInternalAsync(agent, fixedResponse, llmConfig, isRetry: true);
            }
        }

        return toolCalls;
    }
    
    private async Task<ToolResult> ExecuteToolAsync(ToolCall toolCall, Agent agent)
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
            tool = FindToolInAgentSkills(agent, toolCall.ToolName);
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

    /// <summary>
    /// Search for a tool in the agent's enabled skills by name
    /// </summary>
    private ITool? FindToolInAgentSkills(Agent agent, string toolName)
    {
        try
        {
            // Search each enabled skill's tools for a match
            foreach (var skill in agent.EnabledSkills)
            {
                var skillTools = skill.GetTools();
                var matchedTool = skillTools.FirstOrDefault(t => 
                    t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase) ||
                    t.Name.EndsWith($":{toolName}") || // Handle toolkit:tool format
                    toolName.EndsWith($":{t.Name}")    // Handle reverse format
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
}

/// <summary>
/// Agent runtime with LLM support
/// </summary>
public class LLMEnabledRuntime : IAgentRuntime
{
    private readonly ToolRegistry _toolRegistry;
    private readonly SkillRegistry? _skillRegistry;
    private readonly ILogger? _logger;
    private readonly ILLMProvider _llm;
    private readonly IAgentExecutor _executor;
    
    public ToolRegistry ToolRegistry => _toolRegistry;
    public ILogger? Logger { get; set; }
    
    public LLMEnabledRuntime(ToolRegistry toolRegistry, ILLMProvider llm, ILogger? logger = null, SkillRegistry? skillRegistry = null)
    {
        _toolRegistry = toolRegistry;
        _skillRegistry = skillRegistry;
        _llm = llm;
        _logger = logger;
        _executor = new LLMExecutor(this, llm, skillRegistry);
    }
    
    public async Task<AgentResult> ExecuteAsync(Agent agent, string task)
    {
        Logger?.LogInformation($"Executing agent '{agent.Config.Name}' with LLM: {_llm.Name}");
        return await _executor.ExecuteAsync(agent, task);
    }
    
    public Agent SpawnSubAgent(Agent parent, AgentConfig config)
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
        Logger?.LogInformation($"Spawned sub-agent '{config.Name}' from parent '{parent.Config.Name}'");
        
        return agent;
    }
}
