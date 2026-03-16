using AgentFox.Agents;
using AgentFox.Models;
using AgentFox.Skills;
using AgentFox.Tools;
using Microsoft.Extensions.Logging;
using OpenAI;
using System.ClientModel;
using OpenAI.Chat;

namespace AgentFox.LLM;

/// <summary>
/// Agent runtime with LLM support
/// </summary>
public class FoxAgentRuntime : IAgentRuntime
{
    private readonly ToolRegistry _toolRegistry;
    private readonly SkillRegistry? _skillRegistry;
    private readonly ILLMProvider _llm;
    private readonly IAgentExecutor _executor;
    private readonly ILogger<FoxAgentRuntime>? _logger;
    
    public ToolRegistry ToolRegistry => _toolRegistry;
    public ILogger? Logger { get; set; }
    
    public FoxAgentRuntime(ToolRegistry toolRegistry, ILLMProvider llm, ILogger<FoxAgentRuntime>? logger = null, SkillRegistry? skillRegistry = null)
    {
        _toolRegistry = toolRegistry;
        _skillRegistry = skillRegistry;
        _llm = llm;
        _logger = logger;
        _executor = new LLMExecutor(this, llm, skillRegistry);
        
        _logger?.LogInformation("FoxAgentRuntime initialized with LLM provider: {ProviderName}", llm.Name);
    }
    
    public async Task<AgentResult> ExecuteAsync(Agent agent, string task)
    {
        _logger?.LogInformation("Executing agent '{AgentName}' with task: {Task}", agent.Config.Name, task.Length > 100 ? task[..100] + "..." : task);
        
        try
        {
            var result = await _executor.ExecuteAsync(agent, task);
            _logger?.LogInformation("Agent '{AgentName}' completed successfully", agent.Config.Name);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Agent '{AgentName}' failed to execute", agent.Config.Name);
            throw;
        }
    }
    
    public Agent SpawnSubAgent(Agent parent, AgentConfig config)
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
}