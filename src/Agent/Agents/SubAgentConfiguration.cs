namespace AgentFox.Agents;

/// <summary>
/// Configuration for sub-agent execution behavior
/// This defines policy constraints and defaults for spawning and managing sub-agents
/// </summary>
public class SubAgentConfiguration
{
    /// <summary>
    /// Maximum spawn depth allowed (-1 = unlimited)
    /// Prevents infinite recursion of sub-agent spawning
    /// Inspired by OpenClaw's depth limiting approach
    /// </summary>
    public int MaxSpawnDepth { get; set; } = 3;
    
    /// <summary>
    /// Maximum number of concurrent sub-agents that can run simultaneously
    /// Prevents resource exhaustion
    /// </summary>
    public int MaxConcurrentSubAgents { get; set; } = 10;
    
    /// <summary>
    /// Maximum number of sub-agents a single parent agent can spawn
    /// </summary>
    public int MaxChildrenPerAgent { get; set; } = 5;
    
    /// <summary>
    /// Default timeout in seconds for sub-agent execution
    /// </summary>
    public int DefaultRunTimeoutSeconds { get; set; } = 300;
    
    /// <summary>
    /// Default model key for sub-agents (if not specified in spawn request).
    /// Must be a key from the <c>Models</c> appsettings section (e.g. "SubAgent", "FastModel").
    /// Null or empty means the primary LLM is used (same model as the parent agent).
    /// </summary>
    public string? DefaultModel { get; set; }
    
    /// <summary>
    /// Default thinking level ("low", "medium", "high")
    /// </summary>
    public string DefaultThinkingLevel { get; set; } = "high";
    
    /// <summary>
    /// Default maximum iterations for sub-agent execution
    /// </summary>
    public int DefaultMaxIterations { get; set; } = 10;
    
    /// <summary>
    /// Whether to inherit memory from parent agent
    /// </summary>
    public bool InheritParentMemory { get; set; } = true;
    
    /// <summary>
    /// Whether to inherit tools from parent agent
    /// </summary>
    public bool InheritParentTools { get; set; } = true;
    
    /// <summary>
    /// Whether to automatically clean up completed sub-agents
    /// </summary>
    public bool AutoCleanupCompleted { get; set; } = true;
    
    /// <summary>
    /// How long (in milliseconds) to wait before removing completed sub-agent from tracking
    /// </summary>
    public int CleanupDelayMilliseconds { get; set; } = 5000;
    
    /// <summary>
    /// Enable verbose logging for sub-agent operations
    /// </summary>
    public bool EnableVerboseLogging { get; set; } = false;
    
    /// <summary>
    /// Whether sub-agents can spawn their own sub-agents
    /// </summary>
    public bool AllowSubAgentNesting { get; set; } = true;
    
    /// <summary>
    /// Validate this configuration
    /// </summary>
    public ValidationResult Validate()
    {
        var errors = new List<string>();
        
        if (MaxSpawnDepth < -1)
            errors.Add("MaxSpawnDepth must be -1 or greater");
        
        if (MaxConcurrentSubAgents < 1)
            errors.Add("MaxConcurrentSubAgents must be at least 1");
        
        if (MaxChildrenPerAgent < 1)
            errors.Add("MaxChildrenPerAgent must be at least 1");
        
        if (DefaultRunTimeoutSeconds < 1)
            errors.Add("DefaultRunTimeoutSeconds must be at least 1");
        
        if (DefaultMaxIterations < 1)
            errors.Add("DefaultMaxIterations must be at least 1");
        
        if (CleanupDelayMilliseconds < 0)
            errors.Add("CleanupDelayMilliseconds cannot be negative");
        
        return new ValidationResult { IsValid = errors.Count == 0, Errors = errors };
    }
}

/// <summary>
/// Result of a validation operation
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; } = true;
    public List<string> Errors { get; set; } = new();
}
