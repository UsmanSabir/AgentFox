using AgentFox.Agents;
using AgentFox.Models;
using Microsoft.Extensions.Logging;

namespace AgentFox.Tools;

/// <summary>
/// Tool for spawning background sub-agents that run in separate lanes and announce results back.
/// Use this for long-running tasks that should not block the main agent.
/// Register this tool before the FoxAgent is built, then call Initialize() once the agent
/// and console session are available — this breaks the circular dependency.
/// </summary>
public class SpawnBackgroundSubAgentTool : BaseTool
{
    private readonly SubAgentManager _subAgentManager;
    private readonly ILogger? _logger;
    private string _parentAgentId = string.Empty;
    private string _parentSessionKey = string.Empty;
    private int _parentSpawnDepth;

    public SpawnBackgroundSubAgentTool(
        SubAgentManager subAgentManager,
        ILogger? logger = null)
    {
        _subAgentManager = subAgentManager;
        _logger = logger;
    }

    /// <summary>
    /// Wire up the agent-specific identifiers after the FoxAgent has been built.
    /// Must be called before the tool is first invoked.
    /// </summary>
    public void Initialize(string parentAgentId, string parentSessionKey, int parentSpawnDepth = 0)
    {
        _parentAgentId = parentAgentId;
        _parentSessionKey = parentSessionKey;
        _parentSpawnDepth = parentSpawnDepth;
    }

    public override string Name => "spawn_background_subagent";
    
    public override string Description => 
        "Spawn a background sub-agent that runs in a separate lane and announces results back when complete. " +
        "Use this for long-running tasks like research, code analysis, or building complex features. " +
        "The sub-agent runs asynchronously and will report results back to this agent.";

    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["name"] = new() 
        { 
            Type = "string", 
            Description = "Name for the background sub-agent (e.g., 'Researcher', 'CodeAnalyzer', 'BuildRunner')", 
            Required = true 
        },
        ["description"] = new() 
        { 
            Type = "string", 
            Description = "Brief description of what the background sub-agent should do", 
            Required = true 
        },
        ["task"] = new() 
        { 
            Type = "string", 
            Description = "The specific task or question to give to the background sub-agent", 
            Required = true 
        },
        ["model"] = new() 
        { 
            Type = "string", 
            Description = "Optional model to use (default: gpt-4)", 
            Required = false 
        },
        ["thinking_level"] = new() 
        { 
            Type = "string", 
            Description = "Thinking level: 'low', 'medium', or 'high' (default: high)", 
            Required = false 
        },
        ["timeout_seconds"] = new() 
        { 
            Type = "integer", 
            Description = "Timeout in seconds for the sub-agent to complete (default: 300)", 
            Required = false 
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        try
        {
            // Extract parameters
            var name = arguments.GetValueOrDefault("name")?.ToString();
            var description = arguments.GetValueOrDefault("description")?.ToString();
            var task = arguments.GetValueOrDefault("task")?.ToString();
            var model = arguments.GetValueOrDefault("model")?.ToString();
            var thinkingLevel = arguments.GetValueOrDefault("thinking_level")?.ToString();
            var timeoutSeconds = arguments.GetValueOrDefault("timeout_seconds") as int? ?? 300;

            // Validate required parameters
            if (string.IsNullOrWhiteSpace(name))
                return ToolResult.Fail("Sub-agent name is required");
            if (string.IsNullOrWhiteSpace(description))
                return ToolResult.Fail("Sub-agent description is required");
            if (string.IsNullOrWhiteSpace(task))
                return ToolResult.Fail("Sub-agent task is required");

            // Validate thinking level
            if (!string.IsNullOrEmpty(thinkingLevel) && 
                !new[] { "low", "medium", "high" }.Contains(thinkingLevel.ToLower()))
            {
                return ToolResult.Fail("thinking_level must be 'low', 'medium', or 'high'");
            }

            // Build a detailed task message that includes the sub-agent's purpose
            var fullTaskMessage = $"""
                [BACKGROUND TASK] {name}
                
                Description: {description}
                
                Your Task: {task}
                
                Execute this task thoroughly and report your findings/results when complete.
                """;

            // Spawn the background sub-agent
            var spawnResult = await _subAgentManager.SpawnSubAgentAsync(
                parentSessionKey: _parentSessionKey,
                parentAgentId: _parentAgentId,
                taskMessage: fullTaskMessage,
                parentSpawnDepth: _parentSpawnDepth,
                model: model,
                thinkingLevel: thinkingLevel,
                timeoutSeconds: timeoutSeconds
            );

            if (!spawnResult.Success)
            {
                return ToolResult.Fail($"Failed to spawn background sub-agent: {spawnResult.Error}");
            }

            // Format the response
            var response = $"""
                ✓ Background sub-agent '{name}' spawned successfully!
                
                Description: {description}
                Task: {task}
                Run ID: {spawnResult.RunId}
                Session Key: {spawnResult.SubAgentSessionKey}
                Timeout: {timeoutSeconds}s
                
                The sub-agent is running in a background lane and will announce results when complete.
                You can continue with other tasks while it runs.
                """;

            _logger?.LogInformation("Background sub-agent spawned: {Name}, RunId: {RunId}", name, spawnResult.RunId);

            return ToolResult.Ok(response);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error spawning background sub-agent");
            return ToolResult.Fail($"Error spawning background sub-agent: {ex.Message}");
        }
    }
}
