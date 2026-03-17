using System.Text;
using AgentFox.Tools;

namespace AgentFox.Skills;

/// <summary>
/// Tool that lazily loads a skill's full guidance into the conversation context.
/// The agent calls this when it needs to use a specific skill.
/// Only one skill's full content is loaded at a time, keeping context lean.
/// </summary>
public class LoadSkillTool : BaseTool
{
    private readonly SkillRegistry _skillRegistry;

    public LoadSkillTool(SkillRegistry skillRegistry)
    {
        _skillRegistry = skillRegistry ?? throw new ArgumentNullException(nameof(skillRegistry));
    }

    public override string Name => "load_skill";

    public override string Description =>
        "Load a skill's full guidance and capabilities into context. " +
        "Call this before using a skill's tools to understand how to use them correctly. " +
        "Pass skill_name='list' to see all available skills.";

    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["skill_name"] = new ToolParameter
        {
            Type = "string",
            Description = "Name of the skill to load (e.g. 'git', 'docker', 'github'). Pass 'list' to enumerate all skills.",
            Required = true
        }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var skillName = arguments.TryGetValue("skill_name", out var v) ? v?.ToString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(skillName))
            return Task.FromResult(ToolResult.Fail("skill_name is required. Pass 'list' to see all available skills."));

        // Special case: list all skills
        if (skillName.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var manifests = _skillRegistry.GetSkillManifests();
            if (manifests.Count == 0)
                return Task.FromResult(ToolResult.Ok("No skills are currently registered."));

            var sb = new StringBuilder();
            sb.AppendLine("## Registered Skills");
            sb.AppendLine();
            sb.AppendLine("| Skill | Type | Tools | Description |");
            sb.AppendLine("|-------|------|-------|-------------|");
            foreach (var m in manifests)
            {
                sb.AppendLine($"| {m.Name} | {m.SkillType} | {m.ToolCount} | {m.Description} |");
            }
            sb.AppendLine();
            sb.AppendLine("Use load_skill(skill_name: \"<name>\") to load full guidance for any skill.");
            return Task.FromResult(ToolResult.Ok(sb.ToString()));
        }

        // Look up the skill
        var skill = _skillRegistry.Get(skillName);
        if (skill == null)
        {
            // Try case-insensitive match
            skill = _skillRegistry.GetAll()
                .FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
        }

        if (skill == null)
        {
            var available = string.Join(", ", _skillRegistry.GetAll().Select(s => s.Name));
            return Task.FromResult(ToolResult.Fail(
                $"Skill '{skillName}' not found. Available skills: {available}. " +
                $"Use load_skill(skill_name: 'list') to see all skills."));
        }

        // Build full guidance content
        var result = new StringBuilder();
        result.AppendLine($"# Skill: {skill.Name} (v{skill.Version})");
        result.AppendLine();
        result.AppendLine($"**Description:** {skill.Description}");
        result.AppendLine();

        // Tools this skill provides
        var tools = skill.GetTools();
        if (tools.Count > 0)
        {
            result.AppendLine("## Available Tools");
            foreach (var tool in tools)
            {
                result.AppendLine($"- **{tool.Name}**: {tool.Description}");
            }
            result.AppendLine();
        }

        // Full system prompts / guidance
        var prompts = skill.GetSystemPrompts();
        if (prompts.Count > 0)
        {
            result.AppendLine("## Skill Guidance");
            foreach (var prompt in prompts)
            {
                result.AppendLine(prompt.Trim());
                result.AppendLine();
            }
        }

        // Metadata extras
        if (skill.Metadata != null)
        {
            if (skill.Metadata.Capabilities.Count > 0)
            {
                result.AppendLine($"**Capabilities:** {string.Join(", ", skill.Metadata.Capabilities)}");
            }
            if (skill.Dependencies.Count > 0)
            {
                result.AppendLine($"**Dependencies:** {string.Join(", ", skill.Dependencies)}");
            }
        }

        return Task.FromResult(ToolResult.Ok(result.ToString()));
    }
}
