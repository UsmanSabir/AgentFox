using AgentFox.Models;
using AgentFox.Memory;
using AgentFox.Tools;
using AgentFox.LLM;

namespace AgentFox.Agents;

using Microsoft.Extensions.Logging;

/// <summary>
/// Interface for the agent runtime that manages agent execution
/// </summary>
public interface IAgentRuntime
{
    /// <summary>
    /// Execute an agent with the given task
    /// </summary>
    Task<AgentResult> ExecuteAsync(Agent agent, string task);
    
    /// <summary>
    /// Spawn a sub-agent
    /// </summary>
    Agent SpawnSubAgent(Agent parent, AgentConfig config);
    
    /// <summary>
    /// Get tool registry
    /// </summary>
    ToolRegistry ToolRegistry { get; }
    
    /// <summary>
    /// Get logger
    /// </summary>
    ILogger? Logger { get; set; }
}

/// <summary>
/// Configuration for agent spawning
/// </summary>
public class AgentSpawnConfig
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? SystemPrompt { get; set; }
    public List<ToolDefinition>? Tools { get; set; }
    public int MaxIterations { get; set; } = 10;
    public bool InheritMemory { get; set; } = true;
    public bool InheritTools { get; set; } = true;
}

/// <summary>
/// Result of spawning a sub-agent
/// </summary>
public class SpawnResult
{
    public bool Success { get; set; }
    public Agent? Agent { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Default agent runtime implementation
/// </summary>
public class DefaultAgentRuntime : IAgentRuntime
{
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger? _logger;
    private readonly IAgentExecutor _executor;
    
    public ToolRegistry ToolRegistry => _toolRegistry;
    public ILogger? Logger { get; set; }
    
    public DefaultAgentRuntime(ToolRegistry toolRegistry, IAgentExecutor? executor = null, ILogger? logger = null)
    {
        _toolRegistry = toolRegistry;
        _logger = logger;
        _executor = executor ?? new DefaultAgentExecutor(this);
    }
    
    public async Task<AgentResult> ExecuteAsync(Agent agent, string task)
    {
        Logger?.LogInformation($"Executing agent '{agent.Config.Name}' with task: {task}");
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
        
        // Optionally inherit memory from parent
        if (parent.Memory != null)
        {
            agent.Memory = new ShortTermMemory(); // Sub-agent gets its own short-term
        }
        
        // Optionally inherit tools from parent
        if (parent.Config.Tools.Count > 0)
        {
            foreach (var tool in parent.Config.Tools)
            {
                if (!agent.Config.Tools.Any(t => t.Name == tool.Name))
                {
                    agent.Config.Tools.Add(tool);
                }
            }
        }
        
        parent.SubAgents.Add(agent);
        Logger?.LogInformation($"Spawned sub-agent '{config.Name}' from parent '{parent.Config.Name}'");
        
        return agent;
    }
}

/// <summary>
/// Interface for agent execution
/// </summary>
public interface IAgentExecutor
{
    Task<AgentResult> ExecuteAsync(Agent agent, string task);
}

/// <summary>
/// Default agent executor - handles the main agent loop
/// </summary>
public class DefaultAgentExecutor : IAgentExecutor
{
    private readonly IAgentRuntime _runtime;
    
    public DefaultAgentExecutor(IAgentRuntime runtime)
    {
        _runtime = runtime;
    }
    
    public async Task<AgentResult> ExecuteAsync(Agent agent, string task)
    {
        var startTime = DateTime.UtcNow;
        var result = new AgentResult
        {
            Success = false
        };
        
        try
        {
            // Add user message to conversation
            agent.ConversationHistory.Add(new Message(MessageRole.User, task));
            agent.Status = AgentStatus.Thinking;
            
            // Build system prompt
            var systemMessage = BuildSystemMessage(agent);
            agent.ConversationHistory.Insert(0, new Message(MessageRole.System, systemMessage));
            
            // Main agent loop
            for (int iteration = 0; iteration < agent.Config.MaxIterations; iteration++)
            {
                agent.LastActiveAt = DateTime.UtcNow;
                
                // Get available tools
                var availableTools = GetAvailableTools(agent);
                
                // In a real implementation, this would call an LLM
                // For now, we'll simulate the agent's reasoning
                var response = await SimulateAgentResponse(agent, availableTools);
                
                if (response == null)
                {
                    // No more responses - task complete
                    break;
                }
                
                // Add assistant message
                agent.ConversationHistory.Add(new Message(MessageRole.Assistant, response.Content));
                
                // Check if response contains tool calls
                if (response.ToolCalls.Count > 0)
                {
                    foreach (var toolCall in response.ToolCalls)
                    {
                        agent.Status = AgentStatus.ExecutingTool;
                        _runtime.Logger?.LogInformation($"Executing tool: {toolCall.ToolName}");
                        
                        var toolResult = await ExecuteToolAsync(toolCall);
                        result.ToolCalls.Add(toolCall);
                        
                        // Add tool result to conversation
                        agent.ConversationHistory.Add(new Message
                        {
                            Role = MessageRole.Tool,
                            Content = toolResult.Output,
                            ToolCallId = toolCall.Id,
                            ToolName = toolCall.ToolName
                        });
                        
                        // Store in memory
                        if (agent.Memory != null)
                        {
                            await agent.Memory.AddAsync(new MemoryEntry
                            {
                                Content = $"Tool '{toolCall.ToolName}' executed: {toolResult.Output}",
                                Type = MemoryType.ToolExecution,
                                Importance = 0.6
                            });
                        }
                        
                        if (!toolResult.Success)
                        {
                            _runtime.Logger?.LogWarning($"Tool execution failed: {toolResult.Error}");
                        }
                    }
                }
                
                // Check for task completion
                if (IsTaskComplete(response.Content))
                {
                    result.Success = true;
                    result.Output = response.Content;
                    break;
                }
                
                agent.Status = AgentStatus.Thinking;
            }
            
            if (!result.Success && string.IsNullOrEmpty(result.Output))
            {
                result.Output = GetLastAssistantMessage(agent);
                result.Success = !string.IsNullOrEmpty(result.Output);
            }
            
            result.Iterations = agent.Config.MaxIterations;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            agent.Status = AgentStatus.Error;
            _runtime.Logger?.LogError(ex, "Agent execution failed");
        }
        
        result.Duration = DateTime.UtcNow - startTime;
        agent.Status = result.Success ? AgentStatus.Completed : AgentStatus.Error;
        
        return result;
    }
    
    private string BuildSystemMessage(Agent agent)
    {
        // Get base prompt or use default
        var basePrompt = agent.Config.SystemPrompt ?? "You are a helpful AI assistant.";
        
        var builder = new SystemPromptBuilder()
            .WithPersona(basePrompt);
        
        // Add available tools if any
        if (agent.Config.Tools.Count > 0)
        {
            var toolNames = agent.Config.Tools
                .Select(t => $"{t.Name}: {t.Description}")
                .ToArray();
            builder.WithTools(toolNames);
        }
        
        return builder.Build();
    }
    
    private List<ToolDefinition> GetAvailableTools(Agent agent)
    {
        var tools = new List<ToolDefinition>();
        
        // Add configured tools
        tools.AddRange(agent.Config.Tools);
        
        // Also add runtime tools (for sub-agent spawning, etc.)
        var spawnTool = new ToolDefinition
        {
            Name = "spawn_agent",
            Description = "Spawn a sub-agent to handle a subtask",
            Parameters = new Dictionary<string, Models.ToolParameter>
            {
                ["name"] = new() { Type = "string", Description = "Name of the sub-agent", Required = true },
                ["description"] = new() { Type = "string", Description = "Description of the sub-agent's task", Required = true },
                ["task"] = new() { Type = "string", Description = "Task for the sub-agent", Required = true },
                ["system_prompt"] = new() { Type = "string", Description = "System prompt for the sub-agent", Required = false }
            }
        };
        tools.Add(spawnTool);
        
        return tools;
    }
    
    private async Task<AgentResponse?> SimulateAgentResponse(Agent agent, List<ToolDefinition> availableTools)
    {
        // This is a simulation - in a real implementation, this would call an LLM
        // For demonstration, we'll parse the last user message and make decisions
        
        var lastUserMessage = agent.ConversationHistory
            .Where(m => m.Role == MessageRole.User)
            .LastOrDefault();
            
        if (lastUserMessage == null)
            return null;
            
        var response = new AgentResponse
        {
            Content = ""
        };
        
        // Simple pattern matching for demo purposes
        var content = lastUserMessage.Content.ToLower();
        
        // Check if we should spawn a sub-agent
        if (content.Contains("subagent") || content.Contains("spawn") || content.Contains("delegate"))
        {
            // Extract task for sub-agent
            var subAgentTask = "Help with: " + content;
            var subAgent = _runtime.SpawnSubAgent(agent, new AgentConfig
            {
                Name = "SubAgent-" + Guid.NewGuid().ToString("N")[..8],
                Description = "Sub-agent spawned by parent",
                SystemPrompt = "You are a helpful sub-agent.",
                Tools = availableTools
            });
            
            // Note: In a real implementation, we'd execute the sub-agent
            // For demo, we just indicate the sub-agent was spawned
            response.Content = $"Would spawn sub-agent '{subAgent.Config.Name}' to handle the task.";
        }
        else if (content.Contains("memory") || content.Contains("remember"))
        {
            if (agent.Memory != null)
            {
                var memories = await agent.Memory.GetRecentAsync(5);
                response.Content = "Recent memories:\n" + string.Join("\n", memories.Select(m => $"- {m.Content}"));
            }
            else
            {
                response.Content = "No memory system configured.";
            }
        }
        else if (content.Contains("list") && content.Contains("tools"))
        {
            response.Content = "Available tools:\n" + string.Join("\n", availableTools.Select(t => $"- {t.Name}: {t.Description}"));
        }
        else if (content.Contains("status"))
        {
            response.Content = $"Agent Status: {agent.Status}\n" +
                $"Sub-agents: {agent.SubAgents.Count}\n" +
                $"Messages: {agent.ConversationHistory.Count}";
        }
        else
        {
            // Default response - indicate we're ready to help
            response.Content = $"I understand your request: '{lastUserMessage.Content}'. " +
                "I can help you with file operations, shell commands, spawning sub-agents, and more. " +
                "What would you like me to do?";
        }
        
        return response;
    }
    
    private AgentResult result = new();
    
    private async Task<ToolResult> ExecuteToolAsync(ToolCall toolCall)
    {
        var tool = _runtime.ToolRegistry.Get(toolCall.ToolName);
        
        if (tool == null)
        {
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
            return ToolResult.Fail(ex.Message);
        }
    }
    
    private bool IsTaskComplete(string response)
    {
        // Simple heuristic - check for completion indicators
        if (string.IsNullOrEmpty(response))
            return false;
            
        var lower = response.ToLower();
        return lower.Contains("complete") || 
               lower.Contains("done") || 
               lower.Contains("finished") ||
               lower.Contains("task completed");
    }
    
    private string GetLastAssistantMessage(Agent agent)
    {
        return agent.ConversationHistory
            .Where(m => m.Role == MessageRole.Assistant)
            .LastOrDefault()?.Content ?? "";
    }
}

/// <summary>
/// Simulated agent response
/// </summary>
public class AgentResponse
{
    public string Content { get; set; } = string.Empty;
    public List<ToolCall> ToolCalls { get; set; } = new();
}
