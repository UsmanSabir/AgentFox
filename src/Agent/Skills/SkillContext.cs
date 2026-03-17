using AgentFox.Agents;
using AgentFox.Memory;
using AgentFox.Tools;
using Microsoft.Extensions.Logging;

namespace AgentFox.Skills;

/// <summary>
/// Result of skill execution
/// </summary>
public class SkillExecutionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, object> Details { get; set; } = new();
    public long ExecutionTimeMs { get; set; }
    public string? ErrorMessage { get; set; }
    
    public static SkillExecutionResult Ok(string message = "Success") => new() 
    { 
        Success = true, 
        Message = message 
    };
    
    public static SkillExecutionResult Fail(string error) => new() 
    { 
        Success = false, 
        Message = error 
    };
}

/// <summary>
/// Enhanced execution context for skills with full agent awareness
/// </summary>
public class EnhancedSkillExecutionContext
{
    public ILogger Logger { get; init; } = null!;
    public IAgentService AgentService { get; init; } = null!;
    
    // NEW: Agent context
    public string AgentId { get; init; } = string.Empty;                    // ID of executing agent
    public string AgentName { get; init; } = string.Empty;                  // Name of executing agent
    public FoxAgent? ParentAgent { get; init; }                                // Parent agent (if sub-agent)
    public string CurrentTask { get; init; } = string.Empty;                // Current task description
    public string UserId { get; init; } = string.Empty;                     // User context
    public IMemory? AgentMemory { get; init; }                              // Agent's shared memory
    public int ExecutionDepth { get; init; }                                // Sub-agent depth
    public CancellationToken CancellationToken { get; init; } = CancellationToken.None;  // For timeout support
    
    /// <summary>
    /// Create a context for a main agent
    /// </summary>
    public static EnhancedSkillExecutionContext ForAgent(
        ILogger logger,
        IAgentService agentService,
        string agentId,
        string agentName,
        string task,
        string userId,
        IMemory? memory = null,
        CancellationToken cancellationToken = default)
    {
        return new()
        {
            Logger = logger,
            AgentService = agentService,
            AgentId = agentId,
            AgentName = agentName,
            CurrentTask = task,
            UserId = userId,
            AgentMemory = memory,
            ExecutionDepth = 0,
            CancellationToken = cancellationToken
        };
    }
    
    /// <summary>
    /// Create a context for a sub-agent (child of another agent)
    /// </summary>
    public static EnhancedSkillExecutionContext ForSubAgent(
        EnhancedSkillExecutionContext parentContext,
        FoxAgent subAgent,
        string subAgentName)
    {
        return new()
        {
            Logger = parentContext.Logger,
            AgentService = parentContext.AgentService,
            AgentId = subAgent.Id,
            AgentName = subAgentName,
            ParentAgent = null,  // TODO: Access parent agent once FoxAgent exposes it
            CurrentTask = parentContext.CurrentTask,
            UserId = parentContext.UserId,
            AgentMemory = subAgent.Memory,
            ExecutionDepth = parentContext.ExecutionDepth + 1,
            CancellationToken = parentContext.CancellationToken
        };
    }
}

/// <summary>
/// Represents a permission for skill access
/// </summary>
public class SkillPermission
{
    public string SkillName { get; set; } = string.Empty;
    public List<string> AllowedAgentRoles { get; set; } = new();           // e.g., "admin", "developer"
    public List<string> AllowedChannels { get; set; } = new();             // e.g., "internal", "public"
    public int MaxConcurrentExecutions { get; set; } = 1;
    public TimeSpan? TimeoutDuration { get; set; }                         // Default timeout
    public bool RequiresApproval { get; set; }                             // For sensitive skills
    public Dictionary<string, object> CustomAttributes { get; set; } = new();
}

/// <summary>
/// Execution policy for skill error recovery and resilience
/// </summary>
public class SkillExecutionPolicy
{
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    public double ExponentialBackoffMultiplier { get; set; } = 2.0;
    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public List<string>? RetryableErrorPatterns { get; set; }              // Errors to retry on
    public Func<Exception, bool>? ShouldRetry { get; set; }                // Custom retry logic
    public bool PropagateOnTimeout { get; set; } = true;
    
    /// <summary>
    /// Default permissive policy (retries enabled)
    /// </summary>
    public static SkillExecutionPolicy Default { get; } = new();
    
    /// <summary>
    /// Strict policy (no retries, short timeout)
    /// </summary>
    public static SkillExecutionPolicy Strict { get; } = new()
    {
        MaxRetries = 0,
        ExecutionTimeout = TimeSpan.FromSeconds(30),
        PropagateOnTimeout = true
    };
    
    /// <summary>
    /// Lenient policy (many retries, long timeout)
    /// </summary>
    public static SkillExecutionPolicy Lenient { get; } = new()
    {
        MaxRetries = 5,
        ExecutionTimeout = TimeSpan.FromMinutes(10),
        RetryDelay = TimeSpan.FromMilliseconds(500)
    };
}

/// <summary>
/// Executes skills with retry logic, timeout handling, and error recovery
/// </summary>
public class ResilientSkillExecutor
{
    private readonly ILogger<ResilientSkillExecutor> _logger;
    
    public ResilientSkillExecutor(ILogger<ResilientSkillExecutor> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Execute skill with automatic retry and timeout handling
    /// </summary>
    public async Task<SkillExecutionResult> ExecuteWithRetryAsync(
        Skill skill,
        EnhancedSkillExecutionContext context,
        Dictionary<string, object> parameters,
        SkillExecutionPolicy? policy = null,
        CancellationToken? cancellationToken = null)
    {
        policy ??= SkillExecutionPolicy.Default;
        var ct = cancellationToken ?? context.CancellationToken;
        
        for (int attempt = 0; attempt <= policy.MaxRetries; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(policy.ExecutionTimeout);
                
                _logger.LogInformation(
                    $"Executing skill '{skill.Name}' for agent '{context.AgentName}' (attempt {attempt + 1}/{policy.MaxRetries + 1})");
                
                var result = await skill.Execute(context, parameters);
                
                if (result.Success)
                {
                    _logger.LogInformation($"Skill '{skill.Name}' succeeded on attempt {attempt + 1}");
                    return result;
                }
                
                // Check if failure is retryable
                if (!IsRetryable(result, policy))
                {
                    _logger.LogWarning($"Skill '{skill.Name}' failed with non-retryable error: {result.Message}");
                    return result;
                }
                
                _logger.LogWarning($"Skill '{skill.Name}' failed (retryable): {result.Message}");
            }
            catch (OperationCanceledException)
            {
                if (attempt == policy.MaxRetries)
                {
                    _logger.LogError($"Skill '{skill.Name}' timeout after {policy.MaxRetries + 1} attempts");
                    
                    if (policy.PropagateOnTimeout)
                        throw;
                    
                    return new SkillExecutionResult
                    {
                        Success = false,
                        Message = $"Skill execution timeout after {policy.ExecutionTimeout.TotalSeconds}s"
                    };
                }
            }
            catch (Exception ex)
            {
                if (policy.ShouldRetry?.Invoke(ex) == false)
                {
                    _logger.LogError($"Skill '{skill.Name}' failed with non-retryable exception: {ex.Message}");
                    return new SkillExecutionResult { Success = false, Message = ex.Message };
                }
                
                _logger.LogWarning($"Skill '{skill.Name}' failed with exception (retryable): {ex.Message}");
            }
            
            // Exponential backoff
            if (attempt < policy.MaxRetries)
            {
                var delay = TimeSpan.FromMilliseconds(
                    policy.RetryDelay.TotalMilliseconds * 
                    Math.Pow(policy.ExponentialBackoffMultiplier, attempt)
                );
                
                _logger.LogInformation(
                    $"Skill '{skill.Name}' backing off for {delay.TotalMilliseconds}ms before retry {attempt + 2}");
                
                await Task.Delay(delay, ct);
            }
        }
        
        return new SkillExecutionResult
        {
            Success = false,
            Message = $"Skill execution failed after {policy.MaxRetries + 1} attempts"
        };
    }
    
    private bool IsRetryable(SkillExecutionResult result, SkillExecutionPolicy policy)
    {
        if (result.Success)
            return false;
        
        if (policy.RetryableErrorPatterns == null || policy.RetryableErrorPatterns.Count == 0)
            return true;  // Retry all failures if no patterns specified
        
        return policy.RetryableErrorPatterns.Any(pattern =>
            result.Message?.Contains(pattern, StringComparison.OrdinalIgnoreCase) == true);
    }
}

/// <summary>
/// Interface for IAgentService - required for skill context
/// </summary>
public interface IAgentService
{
    Task SendMessageToUser(string userId, string message);
    Task<FoxAgent?> GetAgentByIdAsync(string agentId);
    Task<FoxAgent?> FindSubAgentByCapabilityAsync(string agentId, List<string> requiredCapabilities);
}
