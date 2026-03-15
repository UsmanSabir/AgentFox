# SkillSystem Agent Integration Analysis

## Executive Summary
The `SkillSystem` is well-architected for **skill management** but has **critical gaps for agent integration**. It lacks:
- Agent context passing to skills
- Agent-specific skill authorization
- Skill selection/discovery mechanisms for agents
- Skill composition and chaining support
- Proper error recovery with agent awareness

**Current Integration Score: 65/100**

---

## 1. CURRENT INTEGRATION POINTS (What Works ✅)

### 1.1 Tool Registration Pipeline
```
SkillRegistry → EnableSkill() → GetTools() → ToolRegistry.Register()
                    ↓
             Agent accesses tools via ToolRegistry
```
✅ **Working**: Skills provide tools that agents effectively use

### 1.2 System Prompts
✅ **Working**: Skills expose `GetSystemPrompts()` for LLM context

### 1.3 Dependency Resolution
✅ **Working**: Skills auto-resolve dependencies on enable
```csharp
await skillRegistry.EnableSkillAsync("deployment");
// Automatically enables all dependencies (e.g., git, docker)
```

### 1.4 Event Hooks
✅ **Working**: Lifecycle hooks for monitoring
```csharp
HookRegistry.InvokeSkillPostEnableAsync(name, toolCount);
```

---

## 2. CRITICAL GAPS (What's Missing ❌)

### 2.1 **Agent Context in Skill Execution**
**Problem**: Skills don't know which agent is using them
```csharp
// Current - Skills are blind to agent context
public async Task<SkillExecutionResult> Execute(SkillExecutionContext context, ...)
{
    // context only has Logger + IAgentService
    // No way to know:
    // - Which agent is executing?
    // - What's the current task?
    // - Who is the user?
    // - What's the execution chain depth?
}
```

**Impact**: 
- Sub-agents can't provide agent-specific behavior
- Skills can't make agent-aware decisions
- Impossible to track execution lineage

**Recommendation**:
```csharp
public class EnhancedSkillExecutionContext
{
    public ILogger Logger { get; init; }
    public IAgentService AgentService { get; init; }
    
    // NEW: Agent context
    public string AgentId { get; init; }                    // ID of executing agent
    public string AgentName { get; init; }                  // Name of executing agent
    public Agent? ParentAgent { get; init; }                // Parent agent (if sub-agent)
    public string CurrentTask { get; init; }                // Current task description
    public string UserId { get; init; }                     // User context
    public IMemory? AgentMemory { get; init; }              // Agent's shared memory
    public int ExecutionDepth { get; init; }                // Sub-agent depth
    public CancellationToken CancellationToken { get; init; } // For timeout support
}
```

### 2.2 **No Agent-Skill Authorization**
**Problem**: Any agent can use any skill - no permissions/filtering
```csharp
// Current limitation:
SkillRegistry.GetEnabledSkills();  // Returns ALL enabled skills
// No way to filter by:
// - Agent role/type
// - Channel permissions
// - Security clearance
// - Resource quotas
```

**Impact**:
- Security: Sub-agents could access restricted skills
- Resource control: No way to limit which agents use expensive skills

**Recommendation**:
```csharp
public class SkillPermission
{
    public string SkillName { get; set; }
    public List<string> AllowedAgentRoles { get; set; }      // e.g., "admin", "developer"
    public List<string> AllowedChannels { get; set; }        // e.g., "internal", "public"
    public int MaxConcurrentExecutions { get; set; } = 1;
    public TimeSpan? TimeoutDuration { get; set; }           // Default timeout
    public bool RequiresApproval { get; set; }               // For sensitive skills
}

public class SkillRegistry
{
    private Dictionary<string, SkillPermission> _permissions = new();
    
    public List<Skill> GetAvailableSkillsFor(Agent agent)
    {
        return _skills.Where(s => _permissions[s.Key].AllowedAgentRoles
            .Contains(agent.Config.Role ?? "default")).Values.ToList();
    }
}
```

### 2.3 **No Skill Discovery for LLM Decision-Making**
**Problem**: Agents must choose from a flat tool list; skills aren't discoverable by capability
```csharp
// Current: Agents see all tools, must reason which to use
var tools = _toolRegistry.GetDefinitions();
// Returns 30+ tools - LLM must reason through them all

// Missing: Semantic discovery
// "I need to deploy" → SkillRegistry suggests "deployment" skill
//                       which brings in git, docker, etc.
```

**Impact**:
- Token waste: LLM processes irrelevant tools
- Decision accuracy: Hard to find the right skill
- Sub-agents: Limited reasoning about task decomposition

**Recommendation**:
```csharp
public class SkillMetadata
{
    public string SkillName { get; set; }
    public List<string> Capabilities { get; set; }          // e.g., ["vcs", "versioning"]
    public List<string> Tags { get; set; }                  // e.g., ["devops", "required"]
    public string InputType { get; set; }                   // Domain: code, db, api, etc.
    public string OutputType { get; set; }                  // What it returns
    public int ComplexityScore { get; set; }                // 1-10
    public bool IsCompositional { get; set; }               // Can chain with others
    public List<string> RelatedSkills { get; set; }         // Natural combinations
}

public List<Skill> DiscoverSkillsByCapability(string capability)
{
    return _skills.Where(s => s.Metadata?.Capabilities.Contains(capability))
                  .Values.ToList();
}

public List<Skill> DiscoverSkillsForTask(string taskDescription)
{
    // Use semantic search or keyword matching
    // Return skill recommendations for the task
}
```

### 2.4 **No Skill Composition/Chaining**
**Problem**: Skills operate in isolation; no built-in way to chain them
```csharp
// Desired workflow: Commit code → Push → Deploy
// Current: Three separate tool calls with no composition support

// Missing: Compound skills or orchestration
var result1 = await gitSkill.Execute(...);
var result2 = await gitSkill.Execute(...);  // Manual chaining
var result3 = await deploySkill.Execute(...);

// No atomic transaction, no rollback, no error recovery
```

**Impact**:
- Brittle multi-step workflows
- No rollback on failures
- Agents can't express compound operations

**Recommendation**:
```csharp
public class CompositeSkill : Skill
{
    private List<(Skill skill, string action, Func<SkillExecutionResult, Dictionary<string, object>> paramMapper)> _pipeline;
    
    public override async Task<SkillExecutionResult> ExecuteAsync(SkillExecutionContext context, Dictionary<string, object> parameters)
    {
        var intermediate = parameters;
        var results = new List<SkillExecutionResult>();
        
        try
        {
            foreach (var (skill, action, mapper) in _pipeline)
            {
                var result = await skill.Execute(context, intermediate);
                results.Add(result);
                
                if (!result.Success)
                {
                    // Rollback previous operations
                    await RollbackAsync(context, results.SkipLast(1));
                    return result;
                }
                
                intermediate = mapper(result);  // Pass output as input
            }
            
            return new SkillExecutionResult { Success = true, Message = "Composite operation successful" };
        }
        catch (Exception ex)
        {
            await RollbackAsync(context, results);
            throw;
        }
    }
}
```

### 2.5 **No Sub-Agent Skill Inheritance**
**Problem**: Sub-agents don't inherit parent's skill context
```csharp
// Current: Sub-agent is created with fresh skill configuration
var subAgent = _runtime.SpawnSubAgent(_agent, agentConfig);
// Agent loses parent's enabled skills, custom permissions, memory

// Missing explicit skill inheritance chain
```

**Impact**:
- Sub-agents start from scratch
- Can't leverage parent's specialized skills
- Configuration complexity

**Recommendation**:
```csharp
public class AgentSpawnConfig
{
    public string Name { get; set; }
    
    // NEW:
    public bool InheritEnabledSkills { get; set; } = true;      // Inherit parent's skill set
    public List<string>? AdditionalSkills { get; set; }         // Skills to enable
    public List<string>? ForbiddenSkills { get; set; }          // Skills to disable
    public SkillExecutionPolicy ExecutionPolicy { get; set; }   // Timeout, retries, etc.
}

public class DefaultAgentRuntime
{
    public Agent SpawnSubAgent(Agent parent, AgentSpawnConfig config)
    {
        var agent = new Agent { Config = new AgentConfig { /* ... */ }, Parent = parent };
        
        // NEW: Inherit parent's skill set
        if (config.InheritEnabledSkills && parent.EnabledSkills != null)
        {
            foreach (var skill in parent.EnabledSkills)
            {
                if (config.ForbiddenSkills?.Contains(skill.Name) != true)
                {
                    agent.EnabledSkills.Add(skill);
                }
            }
        }
        
        // Enable additional skills
        if (config.AdditionalSkills != null)
        {
            foreach (var skillName in config.AdditionalSkills)
            {
                agent.EnabledSkills.Add(_skillRegistry.Get(skillName));
            }
        }
        
        return agent;
    }
}
```

### 2.6 **No Error Recovery/Retry Strategy**
**Problem**: Skill failures don't have recovery logic
```csharp
// Current:
try
{
    var result = await skill.Execute(context, parameters);
    if (!result.Success) return result;  // Hard failure
}
catch (Exception ex)
{
    return new SkillExecutionResult { Success = false, Message = ex.Message };
}

// Missing: Retry, fallback, timeout handling
```

**Impact**:
- Transient failures terminate workflows
- No resilience for network/resource issues
- Agents can't implement recovery strategies

**Recommendation**:
```csharp
public class SkillExecutionPolicy
{
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    public double ExponentialBackoffMultiplier { get; set; } = 2.0;
    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public List<string>? RetryableErrorPatterns { get; set; }  // Errors to retry on
    public Func<Exception, bool>? ShouldRetry { get; set; }    // Custom retry logic
}

public class ResilientSkillExecutor
{
    public async Task<SkillExecutionResult> ExecuteWithRetryAsync(
        Skill skill,
        SkillExecutionContext context,
        Dictionary<string, object> parameters,
        SkillExecutionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; attempt <= policy.MaxRetries; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(policy.ExecutionTimeout);
                
                var result = await skill.Execute(context, parameters);
                if (result.Success) return result;
                
                // Check if failure is retryable
                if (!IsRetryable(result, policy)) return result;
            }
            catch (OperationCanceledException)
            {
                if (attempt == policy.MaxRetries)
                    return new SkillExecutionResult { Success = false, Message = "Execution timeout" };
            }
            
            // Exponential backoff
            if (attempt < policy.MaxRetries)
            {
                var delay = TimeSpan.FromMilliseconds(
                    policy.RetryDelay.TotalMilliseconds * 
                    Math.Pow(policy.ExponentialBackoffMultiplier, attempt)
                );
                await Task.Delay(delay, cancellationToken);
            }
        }
        
        return new SkillExecutionResult { Success = false, Message = "Max retries exceeded" };
    }
}
```

### 2.7 **No Skill Execution Observability**
**Problem**: Limited telemetry for skill execution
```csharp
// Current: Event hooks exist but no built-in observability
HookRegistry.InvokeSkillPostEnableAsync(...);

// Missing: Execution metrics, tracing, cost tracking
```

**Impact**:
- Can't analyze skill performance
- No cost tracking for API calls
- Hard to debug multi-agent scenarios

**Recommendation**:
```csharp
public class SkillExecutionMetrics
{
    public string SkillName { get; set; }
    public string AgentId { get; set; }
    public long ExecutionTimeMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int AttemptNumber { get; set; }
    public Dictionary<string, object> Tags { get; set; }      // Custom metrics
    public decimal EstimatedCost { get; set; }               // API costs, etc.
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
}

public class SkillMetricsCollector
{
    private readonly List<SkillExecutionMetrics> _metrics = new();
    private readonly object _lock = new();
    
    public void Record(SkillExecutionMetrics metric)
    {
        lock (_lock) { _metrics.Add(metric); }
    }
    
    public List<SkillExecutionMetrics> GetMetricsFor(string skillName, TimeSpan? timeRange = null)
    {
        lock (_lock)
        {
            var query = _metrics.Where(m => m.SkillName == skillName);
            if (timeRange.HasValue)
            {
                var minTime = DateTime.UtcNow.Subtract(timeRange.Value);
                query = query.Where(m => m.ExecutedAt >= minTime);
            }
            return query.ToList();
        }
    }
    
    public SkillStatistics GetStatistics(string skillName)
    {
        var metrics = GetMetricsFor(skillName);
        return new()
        {
            SkillName = skillName,
            TotalExecutions = metrics.Count,
            SuccessRate = metrics.Count > 0 ? metrics.Count(m => m.Success) / (double)metrics.Count : 0,
            AverageExecutionTimeMs = metrics.Count > 0 ? metrics.Average(m => m.ExecutionTimeMs) : 0,
            MaxExecutionTimeMs = metrics.Count > 0 ? metrics.Max(m => m.ExecutionTimeMs) : 0,
            TotalEstimatedCost = metrics.Sum(m => m.EstimatedCost)
        };
    }
}
```

### 2.8 **No Tool Parameter Mapping from Agent Task**
**Problem**: Agents must manually map task context to skill parameters
```csharp
// Current: Agent must extract params from task
string task = "Deploy version 1.2.3 to production";
// Agent must parse & map: version → param, environment → param
// No automatic mapping framework

// Missing: Semantic parameter binding
```

**Impact**:
- Complex agent code to parse tasks
- Error-prone parameter extraction
- Difficult multi-agent coordination

**Recommendation**:
```csharp
public class SmartParameterMapper
{
    public Dictionary<string, object> MapTaskToParameters(
        string task,
        Dictionary<string, ToolParameter> expectedParams,
        IMemory? agentMemory = null)
    {
        var result = new Dictionary<string, object>();
        
        // Use NLP/regex to extract values from task
        foreach (var (paramName, paramDef) in expectedParams)
        {
            // Try multiple extraction strategies
            if (ExtractFromTask(task, paramName, paramDef, out var value))
            {
                result[paramName] = value;
            }
            else if (agentMemory?.Recall(paramName) is object memoryValue)
            {
                result[paramName] = memoryValue;
            }
            else if (paramDef.Default != null)
            {
                result[paramName] = paramDef.Default;
            }
        }
        
        return result;
    }
}
```

### 2.9 **No Skills Discovery from Directory/Metadata Files**
**Problem**: Skills must be manually registered in code; no filesystem-based discovery
```csharp
// Current: Hardcoded registration
Register(new GitSkill());
Register(new DockerSkill());
// Skills must be compiled into assembly

// Missing: File-based skill loading with metadata
```

**Impact**:
- Can't add skills without recompiling
- No versioning of skills independently
- Difficult to manage skill marketplace or plugins
- No separation of skill definition from code

**Recommendation**:
```csharp
public class SkillDescriptor
{
    public string Name { get; set; }
    public string Version { get; set; }
    public string Description { get; set; }
    public List<string> Dependencies { get; set; } = new();
    public List<ToolDescriptor> Tools { get; set; } = new();
    public List<string> Capabilities { get; set; } = new();
    public string? SkillAssemblyType { get; set; }  // Type name if assembly-based
    public Dictionary<string, object> Configuration { get; set; } = new();
}

public class SkillLoader
{
    private readonly string _skillsDirectory;
    private readonly IServiceProvider _serviceProvider;
    
    public async Task<List<Skill>> LoadSkillsFromDirectoryAsync()
    {
        var skills = new List<Skill>();
        var skillDirs = Directory.GetDirectories(_skillsDirectory);
        
        foreach (var skillDir in skillDirs)
        {
            var descriptorPath = Path.Combine(skillDir, "skill.md");
            var configPath = Path.Combine(skillDir, "skill.json");
            
            if (File.Exists(descriptorPath) && File.Exists(configPath))
            {
                // Parse skill.md for documentation
                var descriptor = await ParseSkillDescriptor(descriptorPath, configPath);
                
                // Load skill assembly or create from descriptor
                var skill = await InstantiateSkillAsync(descriptor, skillDir);
                if (skill != null)
                    skills.Add(skill);
            }
        }
        
        return skills;
    }
    
    private async Task<SkillDescriptor> ParseSkillDescriptor(string mdPath, string jsonPath)
    {
        // Parse YAML-like frontmatter from skill.md
        var content = await File.ReadAllTextAsync(mdPath);
        var config = JsonConvert.DeserializeObject<SkillDescriptor>(await File.ReadAllTextAsync(jsonPath));
        // Extract capabilities, tools, usage patterns from markdown
        return config;
    }
}
```

**skill.md Example**:
```markdown
# Git Skill

## Overview
Provides version control operations (commit, push, pull, branch management).

## Capabilities
- Version Control
- Change Tracking
- Branching Strategy
- Collaboration

## Tools Provided
- git_commit: Create commits
- git_push: Push to remote
- git_pull: Fetch from remote

## When to Use
Use this skill for any version control operations, including:
- Creating feature branches
- Committing changes
- Merging branches
- Pushing to repositories

## Best Practices
- Always commit with meaningful messages
- Use feature branches for new work
- Pull before pushing to avoid conflicts
```

### 2.10 **No Hook-Based Plugin Registration with Agent Guidance**
**Problem**: Skills don't inject guidance during registration; agent guidance is static
```csharp
// Current: Tools registered, guidance is separate and static
skillRegistry.Register(gitSkill);
// No mechanism for skill to customize how agent should use it

// Missing: Plugin registration hooks that inject agent guidance
```

**Impact**:
- Agent lacks context-specific tool usage guidance
- Skills can't provide best practices to LLM
- No dynamic prompt customization per skill
- Difficult to maintain tool usage patterns

**Recommendation**:
```csharp
public interface ISkillPlugin
{
    Task OnRegisterAsync(ISkillRegistrationContext context);
}

public interface ISkillRegistrationContext
{
    void RegisterTool(ITool tool);
    void PrependSystemContext(string guidance);
    void AppendSystemContext(string guidance);
    ToolEventHookRegistry HookRegistry { get; }
}

public class GitSkill : Skill, ISkillPlugin
{
    public async Task OnRegisterAsync(ISkillRegistrationContext context)
    {
        // Register tools with context
        context.RegisterTool(new GitCommitTool());
        context.RegisterTool(new GitPushTool());
        context.RegisterTool(new GitPullTool());
        
        // Inject agent guidance for tool usage
        context.PrependSystemContext(@"
## Git Tools Usage Guidelines
- Always create feature branches before making changes
- Use descriptive commit messages (format: type(scope): description)
- Pull before pushing to avoid conflicts
- Use `git_status` to check current state before committing
- For important changes, create a branch first with `git_branch create feature-name`
- Group related changes into a single commit when possible
        ");
    }
}

public async Task RegisterSkillAsync(Skill skill)
{
    var context = new SkillRegistrationContext();
    
    if (skill is ISkillPlugin plugin)
    {
        await plugin.OnRegisterAsync(context);  // Hook point
    }
    else
    {
        // Fallback: register tools normally
        var tools = skill.GetTools();
        foreach (var tool in tools)
            context.RegisterTool(tool);
    }
    
    // Store collected system prompts for later agent initialization
    _skillSystemPrompts[skill.Name] = context.SystemPromptGuidance;
}
```

### 2.11 **Agent Unaware of Sub-agent Skills for Message Routing**
**Problem**: Parent agents can't make intelligent routing decisions based on sub-agent capabilities
```csharp
// Current: Parent agent must manually decide which sub-agent handles message
var response = await subAgent.ExecuteAsync(task);
// No way to query: "Which sub-agent can handle API integrations?"

// Missing: SkillFilter for agent capability discovery
```

**Impact**:
- Inefficient message routing to sub-agents
- No capability-based routing decisions
- Difficult to auto-select specialized sub-agents
- Manual coordination overhead

**Recommendation**:
```csharp
public class SkillFilter
{
    public List<string> RequiredSkills { get; set; } = new();     // Must have all
    public List<string> PreferredSkills { get; set; } = new();    // Nice to have
    public List<string> ForbiddenSkills { get; set; } = new();    // Must not have
    public List<string> RequiredCapabilities { get; set; } = new(); // Skill capabilities
    public Func<Agent, bool>? CustomPredicate { get; set; }       // Custom logic
}

public class Agent
{
    public List<Skill> EnabledSkills { get; set; }
    
    public SkillFilter GetSupportedSkills()
    {
        return new SkillFilter
        {
            RequiredSkills = EnabledSkills.Select(s => s.Name).ToList(),
            RequiredCapabilities = EnabledSkills
                .SelectMany(s => s.Metadata?.Capabilities ?? new())
                .Distinct()
                .ToList()
        };
    }
}

public class AgentRouter
{
    private readonly List<Agent> _subAgents;
    
    public List<Agent> FindAgentsForTask(string task, SkillFilter filter)
    {
        return _subAgents.Where(agent =>
        {
            var agentSkills = agent.EnabledSkills.Select(s => s.Name).ToList();
            var agentCapabilities = agent.EnabledSkills
                .SelectMany(s => s.Metadata?.Capabilities ?? new())
                .ToList();
            
            // Check required skills
            if (!filter.RequiredSkills.All(rs => agentSkills.Contains(rs)))
                return false;
            
            // Check forbidden skills
            if (filter.ForbiddenSkills.Any(fs => agentSkills.Contains(fs)))
                return false;
            
            // Check required capabilities
            if (!filter.RequiredCapabilities.All(rc => agentCapabilities.Contains(rc)))
                return false;
            
            // Check custom predicate
            if (filter.CustomPredicate != null && !filter.CustomPredicate(agent))
                return false;
            
            return true;
        }).ToList();
    }
    
    public Agent? FindBestAgentForTask(string task, SkillFilter filter)
    {
        var candidates = FindAgentsForTask(task, filter);
        
        if (!candidates.Any()) return null;
        
        // Find best match: prefer more skills, more capabilities, or custom scoring
        return candidates
            .OrderByDescending(a => a.EnabledSkills.Count)
            .ThenByDescending(a => a.EnabledSkills
                .SelectMany(s => s.Metadata?.Capabilities ?? new())
                .Count())
            .FirstOrDefault();
    }
}
```

**Usage Example**:
```csharp
// Parent agent routes to specialty sub-agents based on task
var apiFilter = new SkillFilter
{
    RequiredCapabilities = new[] { "api_integration" },
    RequiredSkills = new[] { "rest_client" }
};

var apiAgent = agentRouter.FindBestAgentForTask(task, apiFilter);
if (apiAgent != null)
{
    var result = await apiAgent.ExecuteAsync(task);
}
```

### 2.12 **No Agent Prompt Space Guidance Injection**
**Problem**: Skills can't naturally guide LLM behavior through system prompt customization
```csharp
// Current: Agent has static system prompt
var systemPrompt = "You are a helpful coding assistant...";

// Missing: Skills injecting contextual tool usage guidance
// Agent doesn't know best practices for using each tool
```

**Impact**:
- LLM doesn't understand proper tool usage patterns
- Suboptimal tool selection decisions
- No guidance on when/how to use specific tools
- Training overhead for each agent setup

**Recommendation**:
```csharp
public class SystemPromptBuilder
{
    private List<string> _prependedContexts = new();      // High priority
    private List<string> _appendedContexts = new();       // Low priority
    private string _baseSystemPrompt;
    
    public void PrependSystemContext(string guidance)
    {
        _prependedContexts.Add(guidance);
    }
    
    public void AppendSystemContext(string guidance)
    {
        _appendedContexts.Add(guidance);
    }
    
    public string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        
        // Prepended contexts first (critical guidance)
        foreach (var context in _prependedContexts)
        {
            sb.AppendLine(context);
            sb.AppendLine();
        }
        
        // Base system prompt
        sb.AppendLine(_baseSystemPrompt);
        sb.AppendLine();
        
        // Appended contexts last (supplementary)
        foreach (var context in _appendedContexts)
        {
            sb.AppendLine(context);
            sb.AppendLine();
        }
        
        return sb.ToString();
    }
}

public class Agent
{
    private SystemPromptBuilder _promptBuilder;
    
    public async Task InitializeAsync()
    {
        _promptBuilder = new SystemPromptBuilder { _baseSystemPrompt = Config.SystemPrompt };
        
        // Enable skills and inject their guidance
        foreach (var skillName in Config.EnabledSkillNames)
        {
            var skill = await _skillRegistry.EnableSkillAsync(skillName);
            if (skill is ISkillPlugin plugin)
            {
                var context = new SkillRegistrationContext(_promptBuilder);
                await plugin.OnRegisterAsync(context);
            }
        }
        
        // Final system prompt includes all injected guidance
        Config.SystemPrompt = _promptBuilder.BuildSystemPrompt();
    }
}
```

**Example Injected Guidance**:
```
## REST API Integration Best Practices

When using the `rest_call` tool:
1. Validate the endpoint URL before making the call
2. Include required authentication headers
3. For POST/PUT requests, validate JSON body structure
4. Always include appropriate error handling
5. Check response status codes before processing data
6. Rate-limit API calls to respect service quotas

## GraphQL Best Practices

When using the `graphql_query` tool:
1. Validate query syntax before execution
2. Request only necessary fields to reduce payload
3. Use aliases for complex nested queries
4. Implement pagination for large result sets
5. Cache results when appropriate
```

---

## 3. ARCHITECTURAL RECOMMENDATIONS (Priority Order)

### Priority 1: CRITICAL (Implement First)
| Issue | Recommendation | Impact |
|-------|---|---|
| Agent context missing | Add `EnhancedSkillExecutionContext` | Enables agent-aware skills |
| No authorization | Add `SkillPermission` system | Security + resource control |
| No error recovery | Add `SkillExecutionPolicy` + retry logic | Resilience |

### Priority 2: HIGH (Implement Second)
| Issue | Recommendation | Impact |
|-------|---|---|
| No skill discovery | Add `SkillMetadata` + discovery methods | Better LLM decision-making |
| Sub-agent skill inheritance | Add `InheritEnabledSkills` config | Proper sub-agent setup |
| No observability | Add `SkillMetricsCollector` | Debugging + cost tracking |

### Priority 2+ EXTENDED: HIGH (Next Wave)
| Issue | Recommendation | Impact |
|-------|---|---|
| No skill discovery | Add `SkillLoader` + skill.md files | Dynamic skill loading |
| No plugin hooks | Add `ISkillPlugin` + `OnRegisterAsync` | Auto agent guidance injection |
| No sub-agent routing | Add `SkillFilter` + `AgentRouter` | Capability-based routing |
| No prompt guidance | Add `SystemPromptBuilder` + context injection | LLM-aware tool usage |

### Priority 3: MEDIUM (Nice to Have)
| Issue | Recommendation | Impact |
|-------|---|---|
| No skill chaining | Add `CompositeSkill` pattern | Atomic workflows |
| No param mapping | Add `SmartParameterMapper` | Simpler agent code |
| No versioning | Add `SkillVersion` validation | Compatibility checking |

---

## 4. IMPLEMENTATION ROADMAP

### Phase 1 (Week 1: Core Agent-Awareness)
```
1. Modify SkillExecutionContext → EnhancedSkillExecutionContext
2. Update all skill Execute() signatures
3. Add SkillPermission & authorization checks
4. Implement SkillExecutionPolicy + ResilientSkillExecutor
5. Update Agent.cs to pass enhanced context
```

### Phase 2 (Week 2: Skill Discovery & Plugin System)
```
1. Create SkillLoader for file-based discovery
2. Define skill.md format + skill.json structure
3. Implement ISkillPlugin interface with OnRegisterAsync hook
4. Create SkillRegistrationContext for guidance injection
5. Create sample skill.md files in skills directory
```

### Phase 3 (Week 3: Agent Intelligence)
```
1. Implement SystemPromptBuilder for context injection
2. Add PrependSystemContext/AppendSystemContext mechanics
3. Implement SkillFilter + AgentRouter for capability-based routing
4. Update FindBestAgentForTask with capability matching
5. Create routing examples for specialized sub-agents
```

### Phase 4 (Week 4: Advanced Patterns)
```
1. Add SkillMetadata to Skill base class (capabilities, tags)
2. Implement DiscoverSkillsByCapability()
3. Add SmartParameterMapper
4. Add CompositeSkill pattern
5. Implement SkillMetricsCollector
```

---

## 5. SECTION-BY-SECTION INTEGRATION GUIDE

### For Skill Discovery
```csharp
// OLD: Manual registration in code
registry.Register(new GitSkill());
registry.Register(new DockerSkill());

// NEW: Automatic filesystem-based loading
var loader = new SkillLoader("./skills", serviceProvider);
var skills = await loader.LoadSkillsFromDirectoryAsync();

foreach (var skill in skills)
{
    await registry.RegisterSkillAsync(skill);  // Invokes ISkillPlugin.OnRegisterAsync
}
```

**Directory Structure**:
```
skills/
├── git/
│   ├── skill.md        # Metadata + guidance
│   ├── skill.json      # Configuration
│   └── GitSkill.cs     # Implementation (optional)
├── docker/
│   ├── skill.md
│   ├── skill.json
│   └── DockerSkill.cs
└── ...
```

### For Agent Skill Inheritance & Guidance
```csharp
// OLD: Manual configuration, static prompts
var agent = new Agent { Config = config };

// NEW: Skills inject guidance during initialization
await agent.InitializeAsync();  // Loads skills, injects guidance
var finalSystemPrompt = agent.GetFinalSystemPrompt();
// Now includes all skill-specific tool usage guidance

// Prompt now contains:
// 1. Base system prompt
// 2. Prepended skill contexts (critical guidance first)
// 3. Tool definitions
// 4. Appended skill contexts (supplementary info)
```

### For Sub-Agent Message Routing
```csharp
// OLD: Parent must manually decide routing
var response = await specializedSubAgent.ExecuteAsync(task);

// NEW: Route based on capabilities
var apiFilter = new SkillFilter
{
    RequiredCapabilities = new[] { "api_integration", "rest" },
    ForbiddenSkills = new[] { "database" }  // Don't use DB tools
};

var apiHandlers = router.FindAgentsForTask(task, apiFilter);
var bestHandler = router.FindBestAgentForTask(task, apiFilter);

if (bestHandler != null)
{
    var result = await bestHandler.ExecuteAsync(task);
}
```

### For LLM Integration
```csharp
// OLD: Flat tool list
var allTools = toolRegistry.GetDefinitions();

// NEW: Skill-grouped tools with injected guidance
public List<SkillDefinition> GetSkillDefinitionsForLLM(Agent agent)
{
    return skillRegistry.GetAvailableSkillsFor(agent)
        .Select(s => new SkillDefinition
        {
            Name = s.Name,
            Description = s.Description,
            Capabilities = s.Metadata?.Capabilities,
            ToolUsageGuidance = s.GetInjectedGuidance(),  // From OnRegisterAsync
            Tools = s.GetTools().Select(t => new ToolDefinition { /* ... */ }).ToList()
        })
        .ToList();
}

// LLM system prompt now has:
// - Tool definitions (from GetTools)
// - Usage guidance (from PrependSystemContext)
// - Best practices (from skill.md)
// - Capability hints (from SkillMetadata)
```

---

## 6. RECOMMENDATIONS SUMMARY

### TIER 1: CRITICAL (Implement First)
1. ✅ Add agent context to `SkillExecutionContext` (2.1)
2. ✅ Implement skill authorization/permissions (2.2)
3. ✅ Add error recovery with retry policies (2.6)
4. ✅ Skills directory discovery with skill.md (2.9) **NEW**
5. ✅ Plugin registration hooks (ISkillPlugin) (2.10) **NEW**

### TIER 2: HIGH PRIORITY (Next Wave)
6. ✅ Add skill discovery/metadata (2.3)
7. ✅ Add sub-agent skill inheritance config (2.5)
8. ✅ Implement metrics collection (2.7)
9. ✅ SkillFilter + Agent routing (2.11) **NEW**
10. ✅ SystemPromptBuilder for guidance injection (2.12) **NEW**

### TIER 3: MEDIUM (Nice to Have)
11. Skill composition/chaining patterns (2.4)
12. Smart parameter mapping (2.8)
13. Skill versioning & compatibility checks
14. Advanced observability dashboards

### Key New Patterns Added
- **Filesystem-based discovery**: Skills loaded from ~/skills directories with skill.md metadata
- **Plugin registration hooks**: `ISkillPlugin.OnRegisterAsync()` for agent guidance injection
- **Capability-based routing**: `SkillFilter` + `AgentRouter` for intelligent sub-agent selection
- **Prompt space injection**: `SystemPromptBuilder` with prepend/append context for LLM tool usage guidance

---

## 7. QUICK WIN: Minimal Viable Change

If you can only do one thing:

**Modify `SkillExecutionContext` to include agent context:**
```csharp
public class SkillExecutionContext
{
    public ILogger Logger { get; }
    public IAgentService AgentService { get; }
    public string AgentId { get; }           // NEW
    public string CurrentTask { get; }       // NEW
    public CancellationToken CancellationToken { get; }  // NEW
}
```

This enables all other improvements without breaking existing code.

---

## 7. EMERGING PATTERNS: File-Based Skills + Plugin Architecture

The new architectural recommendations reveal a **plugin-like system** for skills:

```
Skill Discovery Layer (File-based)
         ↓
Plugin Registration (ISkillPlugin hooks)
         ↓
Agent Guidance Injection (SystemPromptBuilder)
         ↓
Capability-based Routing (SkillFilter + Router)
         ↓
LLM-Aware Execution (Context-injected agents)
```

### Benefits of This Architecture

1. **Extensibility**: Add skills without recompiling
   - Plugin files in skills/ directory
   - skill.md documents functionality
   - skill.json configures behavior

2. **Agent Intelligence**: Skills guide LLM behavior
   - Prepended guidance for critical tool usage
   - Context-aware prompt injection
   - Capability-aware tool selection

3. **Smart Routing**: Parent agents delegate to specialists
   - Query sub-agents by capability
   - Automatic best-agent selection
   - Skill filters prevent unauthorized access

4. **Observability**: Full execution awareness
   - Metrics per skill/agent combo
   - Execution tracing through agent hierarchy
   - Cost tracking by skill usage

---

## 8. QUICK WIN: Minimal Viable Implementation

Start with these 3 changes to unblock all other improvements:

### Step 1: Agent Context (2.1)
```csharp
public class EnhancedSkillExecutionContext
{
    public ILogger Logger { get; init; }
    public IAgentService AgentService { get; init; }
    public string AgentId { get; init; }                    // NEW
    public string CurrentTask { get; init; }                // NEW
    public CancellationToken CancellationToken { get; init; } // NEW
}
```

### Step 2: Plugin Hooks (2.10)
```csharp
public interface ISkillPlugin
{
    Task OnRegisterAsync(ISkillRegistrationContext context);
}

// In OnRegisterAsync, skills call:
// context.PrependSystemContext("Tool usage guidance...")
```

### Step 3: Skill Directory Loading (2.9)
```csharp
var loader = new SkillLoader("./skills", serviceProvider);
var skills = await loader.LoadSkillsFromDirectoryAsync();

foreach (var skill in skills)
    await skillRegistry.RegisterSkillAsync(skill);
```

These 3 components together enable:
- ✅ Skills knowing their agent context
- ✅ Skills injecting LLM guidance automatically
- ✅ Skills loading dynamically from files
- ✅ Foundation for routing + metrics

---

## 9. Conclusion

The SkillSystem needs to evolve from **static tool providers** to **dynamic, plugin-based agents** that:

1. **Discover themselves** from the filesystem (skill.md files)
2. **Register intelligently** with agent guidance injection hooks (ISkillPlugin)
3. **Operate contextually** with agent/task/user awareness (EnhancedSkillExecutionContext)
4. **Route intelligently** based on capabilities (SkillFilter + AgentRouter)
5. **Guide LLMs** through injected prompt contexts (SystemPromptBuilder)
6. **Execute reliably** with retry + metrics (SkillExecutionPolicy + Metrics)

These enhancements transform CSharpClaw from a rigid agent framework to a **flexible, extensible, multi-agent orchestration platform** comparable to advanced systems like Anthropic's Interop Protocols.
