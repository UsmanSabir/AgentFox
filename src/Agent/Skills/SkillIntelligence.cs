using System.Text;
using System.Text.RegularExpressions;
using AgentFox.Agents;
using AgentFox.Memory;
using AgentFox.Plugins.Interfaces;
using AgentFox.Tools;

namespace AgentFox.Skills;

/// <summary>
/// Builds system prompts with injected skill guidance contexts
/// </summary>
public class SystemPromptBuilder
{
    private readonly List<string> _prependedContexts = new();      // High priority, prepended first
    private readonly List<string> _appendedContexts = new();       // Low priority, appended last
    private readonly string? _baseSystemPrompt;
    private readonly object _lock = new();
    
    public SystemPromptBuilder(string? basePrompt = null)
    {
        _baseSystemPrompt = basePrompt;
    }
    
    /// <summary>
    /// Prepend context (high priority - appears early in prompt)
    /// Use for critical tool usage guidance
    /// </summary>
    public void PrependSystemContext(string guidance)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(guidance))
                _prependedContexts.Add(guidance);
        }
    }
    
    /// <summary>
    /// Append context (low priority - appears late in prompt)
    /// Use for supplementary information
    /// </summary>
    public void AppendSystemContext(string guidance)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(guidance))
                _appendedContexts.Add(guidance);
        }
    }
    
    /// <summary>
    /// Get all prepended contexts
    /// </summary>
    public List<string> GetPrependedContexts()
    {
        lock (_lock)
        {
            return new List<string>(_prependedContexts);
        }
    }
    
    /// <summary>
    /// Get all appended contexts
    /// </summary>
    public List<string> GetAppendedContexts()
    {
        lock (_lock)
        {
            return new List<string>(_appendedContexts);
        }
    }
    
    /// <summary>
    /// Build the final system prompt with all injected contexts
    /// </summary>
    public string BuildSystemPrompt()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            
            // Prepended contexts first (critical guidance)
            foreach (var context in _prependedContexts)
            {
                sb.AppendLine(context);
                sb.AppendLine();
            }
            
            // Base system prompt
            if (!string.IsNullOrWhiteSpace(_baseSystemPrompt))
            {
                sb.AppendLine(_baseSystemPrompt);
                sb.AppendLine();
            }
            
            // Appended contexts last (supplementary)
            foreach (var context in _appendedContexts)
            {
                sb.AppendLine(context);
                sb.AppendLine();
            }
            
            return sb.ToString().TrimEnd();
        }
    }
    
    /// <summary>
    /// Clear all injected contexts
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _prependedContexts.Clear();
            _appendedContexts.Clear();
        }
    }
}

/// <summary>
/// Filter for querying agents by their skill capabilities
/// </summary>
public class SkillFilter
{
    public List<string> RequiredSkills { get; set; } = new();     // Must have all
    public List<string> PreferredSkills { get; set; } = new();    // Nice to have
    public List<string> ForbiddenSkills { get; set; } = new();    // Must not have
    public List<string> RequiredCapabilities { get; set; } = new(); // Skill capabilities required
    public List<string> PreferredCapabilities { get; set; } = new(); // Skill capabilities preferred
    public Func<FoxAgent, bool>? CustomPredicate { get; set; }       // Custom filter logic
    
    /// <summary>
    /// Create a filter for agents that can handle API integrations
    /// </summary>
    public static SkillFilter ForApiIntegration() => new()
    {
        RequiredCapabilities = new[] { "api_integration", "rest" }.ToList()
    };
    
    /// <summary>
    /// Create a filter for agents that can do database operations
    /// </summary>
    public static SkillFilter ForDatabase() => new()
    {
        RequiredCapabilities = new[] { "database" }.ToList()
    };
    
    /// <summary>
    /// Create a filter for agents that can do version control
    /// </summary>
    public static SkillFilter ForVersionControl() => new()
    {
        RequiredCapabilities = new[] { "vcs", "versioning" }.ToList()
    };
    
    /// <summary>
    /// Create a filter for agents that can do deployment
    /// </summary>
    public static SkillFilter ForDeployment() => new()
    {
        RequiredCapabilities = new[] { "deployment", "devops" }.ToList()
    };
}

/// <summary>
/// Routes incoming messages to the best sub-agent based on capabilities
/// </summary>
public class AgentRouter
{
    private readonly List<FoxAgent> _subAgents = new();
    private readonly object _lock = new();
    
    public void RegisterSubAgent(FoxAgent agent)
    {
        lock (_lock)
        {
            _subAgents.Add(agent);
        }
    }
    
    public void UnregisterSubAgent(FoxAgent agent)
    {
        lock (_lock)
        {
            _subAgents.Remove(agent);
        }
    }
    
    /// <summary>
    /// Find all agents suitable for the given filter
    /// </summary>
    public List<FoxAgent> FindAgentsForTask(SkillFilter filter)
    {
        lock (_lock)
        {
            return _subAgents.Where(agent => MatchesFilter(agent, filter)).ToList();
        }
    }
    
    /// <summary>
    /// Find the best agent for a task (most skills/capabilities match)
    /// </summary>
    public FoxAgent? FindBestAgentForTask(SkillFilter filter)
    {
        var candidates = FindAgentsForTask(filter);
        
        if (!candidates.Any()) return null;
        
        // Score agents by how well they match
        return candidates
            .Select(a => (agent: a, score: ScoreAgent(a, filter)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.agent.Name)  // Tiebreaker: alphabetical
            .FirstOrDefault()
            .agent;
    }
    
    /// <summary>
    /// Find best agent for task by natural language description
    /// </summary>
    public FoxAgent? FindBestAgentForTaskDescription(string description, List<string> possibleCapabilities)
    {
        var filter = new SkillFilter
        {
            RequiredCapabilities = DeriveCapabilitiesFromDescription(description, possibleCapabilities)
        };
        return FindBestAgentForTask(filter);
    }
    
    private bool MatchesFilter(FoxAgent agent, SkillFilter filter)
    {
        // TODO: Implement skill-based filtering once FoxAgent supports EnabledSkills property
        var agentSkills = new List<string>(); // Will be populated from agent.EnabledSkills
        var agentCapabilities = new List<string>(); // Will be populated from agent.EnabledSkills[].Metadata.Capabilities
        
        // Check required skills
        if (filter.RequiredSkills.Any() && 
            !filter.RequiredSkills.All(rs => agentSkills.Contains(rs)))
            return false;
        
        // Check forbidden skills
        if (filter.ForbiddenSkills.Any(fs => agentSkills.Contains(fs)))
            return false;
        
        // Check required capabilities
        if (filter.RequiredCapabilities.Any() &&
            !filter.RequiredCapabilities.All(rc => agentCapabilities.Contains(rc)))
            return false;
        
        // Check custom predicate
        if (filter.CustomPredicate != null && !filter.CustomPredicate(agent))
            return false;
        
        return true;
    }
    
    private int ScoreAgent(FoxAgent agent, SkillFilter filter)
    {
        var score = 0;
        // TODO: Compute score based on agent skills once FoxAgent supports EnabledSkills property
        var agentSkills = new List<string>(); // Will be populated from agent.EnabledSkills
        var agentCapabilities = new List<string>(); // Will be populated from agent.EnabledSkills[].Metadata.Capabilities
        
        // +2 for each preferred skill matched
        score += filter.PreferredSkills.Count(ps => agentSkills.Contains(ps)) * 2;
        
        // +1 for each required capability matched
        score += filter.RequiredCapabilities.Count(rc => agentCapabilities.Contains(rc));
        
        // +1 for each preferred capability matched
        score += filter.PreferredCapabilities.Count(pc => agentCapabilities.Contains(pc));
        
        return score;
    }
    
    private List<string> DeriveCapabilitiesFromDescription(string description, List<string> possibleCapabilities)
    {
        var derived = new List<string>();
        var lowerDesc = description.ToLower();
        
        foreach (var capability in possibleCapabilities)
        {
            if (lowerDesc.Contains(capability, StringComparison.OrdinalIgnoreCase))
            {
                derived.Add(capability);
            }
        }
        
        return derived;
    }
}

/// <summary>
/// Maps task parameters to skill parameters using semantic extraction
/// </summary>
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
            // Try multiple extraction strategies in order
            if (ExtractFromTask(task, paramName, paramDef, out var value) && value != null)
            {
                result[paramName] = value;
            }
            else if (paramDef.Default != null)
            {
                result[paramName] = paramDef.Default;
            }
            else if (paramDef.Required)
            {
                throw new InvalidOperationException(
                    $"Required parameter '{paramName}' could not be extracted from task or memory");
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Extract a parameter value from task description
    /// </summary>
    private bool ExtractFromTask(
        string task,
        string paramName,
        ToolParameter paramDef,
        out object? value)
    {
        value = null;
        
        // Strategy 1: Look for "paramName: value" pattern
        var colonPattern = @$"{Regex.Escape(paramName)}:\s*([^\s,;\n]+)";
        var colonMatch = Regex.Match(task, colonPattern, RegexOptions.IgnoreCase);
        if (colonMatch.Success)
        {
            value = ConvertValue(colonMatch.Groups[1].Value, paramDef.Type);
            return true;
        }
        
        // Strategy 2: Look for "as paramName" pattern
        var asPattern = @$"\bas\s+{Regex.Escape(paramName)}\s+([^\s,;\n]+)";
        var asMatch = Regex.Match(task, asPattern, RegexOptions.IgnoreCase);
        if (asMatch.Success)
        {
            value = ConvertValue(asMatch.Groups[1].Value, paramDef.Type);
            return true;
        }
        
        // Strategy 3: Look for quoted strings if parameter name appears
        if (task.Contains(paramName, StringComparison.OrdinalIgnoreCase))
        {
            var quotedPattern = @"""([^""]*)""";
            var quotedMatch = Regex.Match(task, quotedPattern);
            if (quotedMatch.Success)
            {
                value = ConvertValue(quotedMatch.Groups[1].Value, paramDef.Type);
                return true;
            }
        }
        
        // Strategy 4: For boolean params, check if param name appears positively
        if (paramDef.Type == "boolean" && task.Contains(paramName, StringComparison.OrdinalIgnoreCase))
        {
            // Check for negation
            if (Regex.IsMatch(task, @"(?:no|not|don't)\s+" + paramName, RegexOptions.IgnoreCase))
            {
                value = false;
            }
            else
            {
                value = true;
            }
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Convert string value to appropriate type
    /// </summary>
    private object ConvertValue(string value, string targetType)
    {
        return targetType.ToLower() switch
        {
            "boolean" => bool.Parse(value),
            "number" => double.Parse(value),
            "integer" => int.Parse(value),
            "array" => value.Split(',').Select(v => v.Trim()).ToArray(),
            _ => value
        };
    }
}
