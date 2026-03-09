namespace AgentFox.Tools;

/// <summary>
/// OpenClaw-inspired event hooks for tool and skill lifecycle
/// Enables extensible event-driven architecture for monitoring and customization
/// </summary>

/// <summary>
/// Hook event triggered before a tool is executed
/// </summary>
/// <param name="toolName">Name of the tool being invoked</param>
/// <param name="arguments">Arguments passed to the tool</param>
/// <param name="executionId">Unique ID for this execution (for correlation)</param>
public delegate Task ToolPreExecuteHook(string toolName, Dictionary<string, object?> arguments, string executionId);

/// <summary>
/// Hook event triggered after a tool successfully executes
/// </summary>
/// <param name="toolName">Name of the tool that executed</param>
/// <param name="result">The result returned by the tool</param>
/// <param name="executionTimeMs">Time taken to execute in milliseconds</param>
/// <param name="executionId">Unique ID for this execution</param>
public delegate Task ToolPostExecuteHook(string toolName, ToolResult result, long executionTimeMs, string executionId);

/// <summary>
/// Hook event triggered when tool execution fails
/// </summary>
/// <param name="toolName">Name of the tool that failed</param>
/// <param name="error">Error message or exception</param>
/// <param name="executionTimeMs">Time taken before failure</param>
/// <param name="executionId">Unique ID for this execution</param>
public delegate Task ToolErrorHook(string toolName, string error, long executionTimeMs, string executionId);

/// <summary>
/// Hook event triggered when a tool is registered
/// </summary>
/// <param name="name">Tool name</param>
/// <param name="description">Tool description</param>
public delegate Task ToolRegisteredHook(string name, string description);

/// <summary>
/// Hook event triggered when a tool is unregistered
/// </summary>
/// <param name="name">Tool name</param>
public delegate Task ToolUnregisteredHook(string name);

/// <summary>
/// Hook event triggered before a skill is enabled
/// </summary>
/// <param name="skillName">Name of the skill</param>
public delegate Task SkillPreEnableHook(string skillName);

/// <summary>
/// Hook event triggered after a skill is successfully enabled
/// </summary>
/// <param name="skillName">Name of the skill</param>
/// <param name="toolCount">Number of tools provided by this skill</param>
public delegate Task SkillPostEnableHook(string skillName, int toolCount);

/// <summary>
/// Hook event triggered when skill enablement fails
/// </summary>
/// <param name="skillName">Name of the skill</param>
/// <param name="error">Error message</param>
public delegate Task SkillErrorHook(string skillName, string error);

/// <summary>
/// Hook event triggered when a skill is disabled
/// </summary>
/// <param name="skillName">Name of the skill</param>
public delegate Task SkillDisabledHook(string skillName);

/// <summary>
/// Central event hook registry for OpenClaw-inspired lifecycle events
/// Allows agents to subscribe to tool and skill events for observability and customization
/// </summary>
public class ToolEventHookRegistry
{
    private readonly object _hookLock = new();
    
    // Tool hooks
    public event ToolPreExecuteHook? OnToolPreExecute;
    public event ToolPostExecuteHook? OnToolPostExecute;
    public event ToolErrorHook? OnToolError;
    public event ToolRegisteredHook? OnToolRegistered;
    public event ToolUnregisteredHook? OnToolUnregistered;
    
    // Skill hooks
    public event SkillPreEnableHook? OnSkillPreEnable;
    public event SkillPostEnableHook? OnSkillPostEnable;
    public event SkillErrorHook? OnSkillError;
    public event SkillDisabledHook? OnSkillDisabled;
    
    /// <summary>
    /// Invoke pre-execute hook for a tool
    /// </summary>
    public async Task InvokeToolPreExecuteAsync(string toolName, Dictionary<string, object?> arguments, string executionId)
    {
        if (OnToolPreExecute != null)
        {
            try
            {
                await OnToolPreExecute(toolName, arguments, executionId);
            }
            catch
            {
                // Silently swallow hook errors to avoid breaking tool execution
            }
        }
    }
    
    /// <summary>
    /// Invoke post-execute hook for a tool
    /// </summary>
    public async Task InvokeToolPostExecuteAsync(string toolName, ToolResult result, long executionTimeMs, string executionId)
    {
        if (OnToolPostExecute != null)
        {
            try
            {
                await OnToolPostExecute(toolName, result, executionTimeMs, executionId);
            }
            catch
            {
                // Silently swallow hook errors
            }
        }
    }
    
    /// <summary>
    /// Invoke error hook for a tool
    /// </summary>
    public async Task InvokeToolErrorAsync(string toolName, string error, long executionTimeMs, string executionId)
    {
        if (OnToolError != null)
        {
            try
            {
                await OnToolError(toolName, error, executionTimeMs, executionId);
            }
            catch
            {
                // Silently swallow hook errors
            }
        }
    }
    
    /// <summary>
    /// Invoke tool registered hook
    /// </summary>
    public async Task InvokeToolRegisteredAsync(string name, string description)
    {
        if (OnToolRegistered != null)
        {
            try
            {
                await OnToolRegistered(name, description);
            }
            catch
            {
                // Silently swallow hook errors
            }
        }
    }
    
    /// <summary>
    /// Invoke tool unregistered hook
    /// </summary>
    public async Task InvokeToolUnregisteredAsync(string name)
    {
        if (OnToolUnregistered != null)
        {
            try
            {
                await OnToolUnregistered(name);
            }
            catch
            {
                // Silently swallow hook errors
            }
        }
    }
    
    /// <summary>
    /// Invoke skill pre-enable hook
    /// </summary>
    public async Task InvokeSkillPreEnableAsync(string skillName)
    {
        if (OnSkillPreEnable != null)
        {
            try
            {
                await OnSkillPreEnable(skillName);
            }
            catch
            {
                // Silently swallow hook errors
            }
        }
    }
    
    /// <summary>
    /// Invoke skill post-enable hook
    /// </summary>
    public async Task InvokeSkillPostEnableAsync(string skillName, int toolCount)
    {
        if (OnSkillPostEnable != null)
        {
            try
            {
                await OnSkillPostEnable(skillName, toolCount);
            }
            catch
            {
                // Silently swallow hook errors
            }
        }
    }
    
    /// <summary>
    /// Invoke skill error hook
    /// </summary>
    public async Task InvokeSkillErrorAsync(string skillName, string error)
    {
        if (OnSkillError != null)
        {
            try
            {
                await OnSkillError(skillName, error);
            }
            catch
            {
                // Silently swallow hook errors
            }
        }
    }
    
    /// <summary>
    /// Invoke skill disabled hook
    /// </summary>
    public async Task InvokeSkillDisabledAsync(string skillName)
    {
        if (OnSkillDisabled != null)
        {
            try
            {
                await OnSkillDisabled(skillName);
            }
            catch
            {
                // Silently swallow hook errors
            }
        }
    }
    
    /// <summary>
    /// Clear all hooks
    /// </summary>
    public void ClearAllHooks()
    {
        lock (_hookLock)
        {
            OnToolPreExecute = null;
            OnToolPostExecute = null;
            OnToolError = null;
            OnToolRegistered = null;
            OnToolUnregistered = null;
            OnSkillPreEnable = null;
            OnSkillPostEnable = null;
            OnSkillError = null;
            OnSkillDisabled = null;
        }
    }
}
