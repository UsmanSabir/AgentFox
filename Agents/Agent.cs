using AgentFox.Memory;
using AgentFox.Models;
using AgentFox.Skills;
using AgentFox.Tools;

namespace AgentFox.Agents;

/// <summary>
/// Main agent class that orchestrates all functionality
/// </summary>
public class FoxAgent
{
    private readonly IAgentRuntime _runtime;
    private readonly Agent _agent;
    
    public string Id => _agent.Config.Id;
    public string Name => _agent.Config.Name;
    public AgentStatus Status => _agent.Status;
    public List<Agent> SubAgents => _agent.SubAgents;
    public IMemory? Memory => _agent.Memory;
    
    /// <summary>
    /// Skills enabled for this agent (used for capability-based routing)
    /// </summary>
    public List<Skill> EnabledSkills { get; set; } = new();
    
    /// <summary>
    /// Agent role for permission and resource limit checking
    /// </summary>
    public string Role { get; set; } = "default";
    
    public FoxAgent(IAgentRuntime runtime, AgentConfig config)
    {
        _runtime = runtime;
        _agent = new Agent
        {
            Config = config,
            Status = AgentStatus.Idle
        };
        
        // Add tool definitions from registry
        foreach (var toolDef in runtime.ToolRegistry.GetDefinitions())
        {
            if (!_agent.Config.Tools.Any(t => t.Name == toolDef.Name))
            {
                _agent.Config.Tools.Add(toolDef);
            }
        }
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
        return await _runtime.ExecuteAsync(_agent, task);
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
            Tools = config.Tools ?? new List<ToolDefinition>()
        };
        
        var subAgent = _runtime.SpawnSubAgent(_agent, agentConfig);
        var foxSubAgent = new FoxAgent(_runtime, subAgent.Config)
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
    
    /// <summary>
    /// Get conversation history
    /// </summary>
    public List<Message> GetHistory()
    {
        return _agent.ConversationHistory.ToList();
    }
    
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
    public void AddSystemMessage(string content)
    {
        _agent.ConversationHistory.Insert(0, new Message(MessageRole.System, content));
    }
    
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
    private readonly IAgentRuntime _runtime;
    private AgentConfig _config = new();
    private IMemory? _memory;
    
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
    
    public AgentBuilder WithHybridMemory(int shortTermSize = 50, string? longTermPath = null)
    {
        _memory = new HybridMemory(shortTermSize, longTermPath);
        return this;
    }
    
    public FoxAgent Build()
    {
        if (string.IsNullOrEmpty(_config.Name))
        {
            _config.Name = "AgentFox-" + Guid.NewGuid().ToString("N")[..8];
        }
        
        var agent = new FoxAgent(_runtime, _config);
        
        if (_memory != null)
        {
            agent.WithMemory(_memory);
        }
        
        return agent;
    }
}
