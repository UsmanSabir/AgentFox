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
    private readonly ILogger? _logger;
    private readonly ILLMProvider _llm;
    private readonly IAgentExecutor _executor;
    
    public ToolRegistry ToolRegistry => _toolRegistry;
    public ILogger? Logger { get; set; }
    
    public FoxAgentRuntime(ToolRegistry toolRegistry, ILLMProvider llm, ILogger? logger = null, SkillRegistry? skillRegistry = null)
    {
        _toolRegistry = toolRegistry;
        _skillRegistry = skillRegistry;
        _llm = llm;
        _logger = logger;
        _executor = new LLMExecutor(this, llm, skillRegistry);
    }
    
    public async Task<AgentResult> ExecuteAsync(Agent agent, string task)
    {
        //Logger?.LogInformation($"Executing agent '{agent.Config.Name}' with LLM: {_llm.Name}");
        //var baseUrl = _llm.DefaultConfig?.BaseUrl;
        //var apiKeyCredential = new ApiKeyCredential("key");
        //var openAiClient = new OpenAIClient(apiKeyCredential, new OpenAIClientOptions() { Endpoint = new Uri(baseUrl) });
        //var client = openAiClient.GetChatClient(_llm.DefaultConfig.Model);
        //client.AsAIAgent()

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