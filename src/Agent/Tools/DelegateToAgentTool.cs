using System.Text.Json;
using AgentFox.Plugins.Interfaces;

namespace AgentFox.Tools;

/// <summary>Single controlled delegation boundary from the main agent to plugin specialists.</summary>
public sealed class DelegateToAgentTool : BaseTool
{
    private readonly IAgentRegistry _registry;

    public DelegateToAgentTool(IAgentRegistry registry) => _registry = registry;

    public override string Name => "delegate_to_agent";

    public override string Description
    {
        get
        {
            var agents = _registry.GetDescriptors();
            var available = agents.Count == 0
                ? "No specialist agents are registered."
                : string.Join("; ", agents.Select(a => $"{a.Id}: {a.Description}"));
            return "Delegate a domain-specific request to an isolated persistent specialist agent. " + available;
        }
    }

    public override Dictionary<string, ToolParameter> Parameters => new()
    {
        ["agent_id"] = new()
        {
            Type = "string",
            Description = "Registered specialist agent id.",
            Required = true,
            EnumValues = _registry.GetDescriptors().Select(x => x.Id).ToList()
        },
        ["task"] = new()
        {
            Type = "string",
            Description = "Complete request to give the specialist.",
            Required = true
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var id = arguments.GetValueOrDefault("agent_id")?.ToString();
        var task = arguments.GetValueOrDefault("task")?.ToString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(task))
            return ToolResult.Fail("agent_id and task are required.");

        var result = await _registry.RunAsync(id, task, conversationId: null);
        return ToolResult.Ok(JsonSerializer.Serialize(new { agent_id = id, response = result }));
    }
}
