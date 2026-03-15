using AgentFox.Skills;
using AgentFox.Tools;

namespace AgentFox.LLM;

/// <summary>
/// System prompt integration utility
/// Manages system prompts across skills, agents, and validates quality
/// </summary>
public class SystemPromptManager
{
    private readonly SkillRegistry _skillRegistry;
    private readonly SystemPromptValidator _validator;
    private readonly Dictionary<string, List<string>> _cachedPrompts = new();
    
    public SystemPromptManager(SkillRegistry skillRegistry)
    {
        _skillRegistry = skillRegistry;
        _validator = new SystemPromptValidator();
    }
    
    /// <summary>
    /// Get system prompt for a skill with validation
    /// </summary>
    public SystemPromptResult GetSkillPrompt(string skillName)
    {
        var skill = _skillRegistry.Get(skillName);
        if (skill == null)
            return new SystemPromptResult { Error = $"Skill '{skillName}' not found" };
        
        var prompts = skill.GetSystemPrompts();
        if (prompts.Count == 0)
            return new SystemPromptResult 
            { 
                Error = $"Skill '{skillName}' has no system prompts defined" 
            };
        
        var mainPrompt = prompts[0];
        var validation = _validator.Validate(mainPrompt);
        
        return new SystemPromptResult
        {
            SkillName = skillName,
            Prompts = prompts,
            MainPrompt = mainPrompt,
            ValidationResult = validation,
            Success = true
        };
    }
    
    /// <summary>
    /// Get system prompts for all enabled skills
    /// </summary>
    public List<SystemPromptResult> GetEnabledSkillsPrompts()
    {
        var results = new List<SystemPromptResult>();
        var enabledSkills = _skillRegistry.GetEnabledSkills();
        
        foreach (var skill in enabledSkills)
        {
            var result = GetSkillPrompt(skill.Name);
            results.Add(result);
        }
        
        return results;
    }
    
    /// <summary>
    /// Audit all system prompts in the registry
    /// </summary>
    public SystemPromptAuditResult AuditAllPrompts()
    {
        var skillPrompts = new Dictionary<string, List<string>>();
        
        foreach (var skill in _skillRegistry.GetAll())
        {
            var prompts = skill.GetSystemPrompts();
            if (prompts.Count > 0)
            {
                skillPrompts[skill.Name] = prompts;
            }
        }
        
        return _validator.AuditSkillPrompts(skillPrompts);
    }
    
    /// <summary>
    /// Get recommendations for prompt improvements
    /// </summary>
    public List<PromptImprovement> GetImprovementRecommendations(string skillName)
    {
        var result = GetSkillPrompt(skillName);
        var improvements = new List<PromptImprovement>();
        
        if (!result.Success || result.ValidationResult == null)
            return improvements;
        
        var failedRules = result.ValidationResult.RuleResults
            .Where(r => !r.Passed && r.Severity == ValidationSeverity.Warning)
            .ToList();
        
        foreach (var failedRule in failedRules)
        {
            improvements.Add(new PromptImprovement
            {
                Issue = failedRule.Description,
                Recommendation = GetRecommendation(failedRule.RuleName),
                Priority = "Medium"
            });
        }
        
        return improvements;
    }
    
    private string GetRecommendation(string ruleName)
    {
        return ruleName switch
        {
            "ClearInstructions" => "Add imperative verbs like 'always', 'ensure', 'verify' to make instructions explicit",
            "NotTooVague" => "Replace vague terms with specific examples or context",
            "AvoidGeneric" => "Add specialization (e.g., 'expert developer' instead of just 'assistant')",
            "ReasonableLength" => "Consider splitting into sub-prompts or consolidating related instructions",
            _ => "Review and refine the prompt for clarity and specificity"
        };
    }
    
    /// <summary>
    /// Build a comprehensive system prompt for an agent with multiple skills
    /// </summary>
    public string BuildMultiSkillPrompt(string basePrompt, params string[] skillNames)
    {
        var builder = new SystemPromptBuilder()
            .WithPersona(basePrompt);
        
        var skillPrompts = new List<string>();
        foreach (var skillName in skillNames)
        {
            var result = GetSkillPrompt(skillName);
            if (result.Success && result.MainPrompt != null)
            {
                skillPrompts.Add($"## {skillName.ToUpper()}\n{result.MainPrompt}");
            }
        }
        
        if (skillPrompts.Count > 0)
        {
            builder.WithConstraints($"You have the following specialized skills:\n{string.Join("\n\n", skillPrompts)}");
        }
        
        return builder.Build();
    }
    
    /// <summary>
    /// Export audit report as formatted text
    /// </summary>
    public string ExportAuditReport(SystemPromptAuditResult audit)
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine("╔════════════════════════════════════════════════════╗");
        report.AppendLine("║     System Prompt Quality Audit Report             ║");
        report.AppendLine("╚════════════════════════════════════════════════════╝");
        report.AppendLine();
        report.AppendLine($"Audit Date: {audit.AuditTime:O}");
        report.AppendLine($"Total Skills: {audit.TotalSkills}");
        report.AppendLine($"Results: {audit.PassCount} PASS, {audit.FailCount} FAIL");
        report.AppendLine($"Average Score: {audit.AverageScore:F1}/100");
        report.AppendLine();
        report.AppendLine("DETAILED RESULTS:");
        report.AppendLine("─────────────────────────────────────────────────────");
        
        foreach (var skillValidation in audit.ValidationResults.OrderByDescending(v => v.ValidationResult.Score))
        {
            var status = skillValidation.ValidationResult.IsValid ? "✓" : "✗";
            report.AppendLine($"{status} {skillValidation.SkillName}: {skillValidation.ValidationResult.Score:F1}/100");
            
            var failedRules = skillValidation.ValidationResult.RuleResults
                .Where(r => !r.Passed && r.Severity >= ValidationSeverity.Warning)
                .ToList();
            
            foreach (var failed in failedRules)
            {
                report.AppendLine($"   [{failed.Severity}] {failed.Description}");
            }
        }
        
        return report.ToString();
    }
}

/// <summary>
/// Result of getting a system prompt
/// </summary>
public class SystemPromptResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? SkillName { get; set; }
    public List<string> Prompts { get; set; } = new();
    public string? MainPrompt { get; set; }
    public SystemPromptValidationResult? ValidationResult { get; set; }
    
    public override string ToString()
    {
        if (!Success)
            return $"ERROR: {Error}";
        
        return $"Skill: {SkillName}\nPrompts: {Prompts.Count}\nValidation: {ValidationResult}";
    }
}

/// <summary>
/// Recommended improvement for a prompt
/// </summary>
public class PromptImprovement
{
    public string Issue { get; set; } = string.Empty;
    public string Recommendation { get; set; } = string.Empty;
    public string Priority { get; set; } = "Medium";
    
    public override string ToString()
    {
        return $"[{Priority}] {Issue}\n  → {Recommendation}";
    }
}
