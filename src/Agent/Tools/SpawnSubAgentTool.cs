using AgentFox.Agents;
using AgentFox.Plugins.Interfaces;
using ToolParameter = AgentFox.Models.ToolParameter;

namespace AgentFox.Tools;

/// <summary>
/// Tool for spawning sub-agents to handle complex or specialized tasks.
/// Accepts a Func&lt;FoxAgent&gt; factory so it can be registered in the ToolRegistry
/// before the FoxAgent is built, breaking the circular dependency.
/// </summary>
public class SpawnSubAgentTool : BaseTool
{
    private readonly Func<FoxAgent> _agentFactory;

    public SpawnSubAgentTool(Func<FoxAgent> agentFactory)
    {
        _agentFactory = agentFactory;
    }

    public override string Name => "spawn_subagent";
    
    public override string Description => 
        "Spawn a specialized sub-agent to handle complex tasks. " +
        "Use this when a task requires different expertise, extended context, or parallel execution. " +
        "The sub-agent will have access to the parent's tools and can collaborate through shared memory.";

    public override Dictionary<string, Plugins.Interfaces.ToolParameter> Parameters { get; } = new()
    {
        ["name"] = new() 
        { 
            Type = "string", 
            Description = "Name for the sub-agent (e.g., 'CodeReviewer', 'Researcher', 'Debugger')", 
            Required = true 
        },
        ["description"] = new() 
        { 
            Type = "string", 
            Description = "Brief description of what the sub-agent should do", 
            Required = true 
        },
        ["task"] = new() 
        { 
            Type = "string", 
            Description = "The specific task or question to give to the sub-agent", 
            Required = true 
        },
        ["system_prompt"] = new() 
        { 
            Type = "string", 
            Description = "Optional custom system prompt for the sub-agent (defaults to parent's prompt with modifications)", 
            Required = false 
        },
        ["max_iterations"] = new() 
        { 
            Type = "integer", 
            Description = "Maximum number of iterations the sub-agent can run (default: 10)", 
            Required = false 
        },
        ["inherit_tools"] = new() 
        { 
            Type = "boolean", 
            Description = "Whether the sub-agent inherits parent's tools (default: true)", 
            Required = false 
        },
        ["inherit_memory"] = new() 
        { 
            Type = "boolean", 
            Description = "Whether the sub-agent inherits access to parent's memory (default: true)", 
            Required = false 
        },
        ["inherit_skills"] = new() 
        { 
            Type = "boolean", 
            Description = "Whether the sub-agent inherits parent's enabled skills (default: true)", 
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
            var systemPrompt = arguments.GetValueOrDefault("system_prompt")?.ToString();
            var maxIterations = arguments.GetValueOrDefault("max_iterations") as int? ?? 10;
            var inheritTools = arguments.GetValueOrDefault("inherit_tools") as bool? ?? true;
            var inheritMemory = arguments.GetValueOrDefault("inherit_memory") as bool? ?? true;
            var inheritSkills = arguments.GetValueOrDefault("inherit_skills") as bool? ?? true;

            // Validate required parameters
            if (string.IsNullOrWhiteSpace(name))
                return ToolResult.Fail("Sub-agent name is required");
            if (string.IsNullOrWhiteSpace(description))
                return ToolResult.Fail("Sub-agent description is required");
            if (string.IsNullOrWhiteSpace(task))
                return ToolResult.Fail("Sub-agent task is required");

            // Resolve the agent at execution time (not construction time)
            var agent = _agentFactory();

            // Inject a planning preamble so the sub-agent breaks the work into steps
            // before acting, making task handling more structured and reliable.
            var plannedTask = $"""
                ## Task
                {task}

                ## Instructions
                You are a specialized sub-agent named '{name}'. Your role: {description}

                Before taking any action, briefly outline your plan as a numbered list of steps.
                Then execute each step in order, using your available tools.
                After completing all steps, provide a concise summary of what was done and the final result.
                If you encounter an error or blocker, report it clearly and explain what you tried.
                """;

            // Create spawn config
            var config = new AgentSpawnConfig
            {
                Name = name,
                Description = description,
                SystemPrompt = systemPrompt,
                MaxIterations = maxIterations,
                InheritTools = inheritTools,
                InheritMemory = inheritMemory,
                InheritEnabledSkills = inheritSkills
            };

            // Spawn the sub-agent
            var subAgent = agent.SpawnSubAgent(config);

            // Execute the task with the sub-agent.
            // Runs inside the Main lane (tool call); re-enqueueing to Subagent lane would deadlock the serial Main lane.
            var result = await subAgent.ExecuteAsync(plannedTask);

            // Format the response
            var response = $"""
                Sub-agent '{name}' spawned successfully.
                
                Description: {description}
                
                Task: {task}
                
                Result:
                {result.Output}
                
                Success: {result.Success}
                """;

            return ToolResult.Ok(response);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Error spawning sub-agent: {ex.Message}");
        }
    }
}
