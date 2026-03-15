using System.Text.RegularExpressions;

namespace AgentFox.LLM;

/// <summary>
/// System prompt validation and quality assessment tool
/// Ensures all prompts meet OpenCLAW standards
/// </summary>
public class SystemPromptValidator
{
    private readonly List<ValidationRule> _rules = new();
    
    public SystemPromptValidator()
    {
        InitializeDefaultRules();
    }
    
    private void InitializeDefaultRules()
    {
        // Rule 1: Prompt must have role definition
        _rules.Add(new ValidationRule
        {
            Name = "RoleDefinition",
            Description = "Prompt must define a clear role",
            Check = prompt => prompt.Contains("You are") || prompt.Contains("You will"),
            Severity = ValidationSeverity.Error
        });
        
        // Rule 2: Prompt should have clear instructions
        _rules.Add(new ValidationRule
        {
            Name = "ClearInstructions",
            Description = "Prompt should use imperative verbs for instructions",
            Check = prompt =>
            {
                var imperatives = new[] { "must", "should", "always", "never", "ensure", "verify", "check" };
                return imperatives.Any(verb => prompt.ToLower().Contains(verb));
            },
            Severity = ValidationSeverity.Warning
        });
        
        // Rule 3: Reasonable length
        _rules.Add(new ValidationRule
        {
            Name = "ReasonableLength",
            Description = "Prompt should not exceed 8000 characters",
            Check = prompt => prompt.Length <= 8000,
            Severity = ValidationSeverity.Warning
        });
        
        // Rule 4: Not too vague
        _rules.Add(new ValidationRule
        {
            Name = "NotTooVague",
            Description = "Avoid vague terms like 'helpful' without context",
            Check = prompt =>
            {
                var vagueTerms = new[] { " helpful ", " nice ", " good " };
                var hasVague = vagueTerms.Any(term => prompt.ToLower().Contains(term));
                if (!hasVague) return true;
                
                // Check if vague terms are refined
                return prompt.Contains("specifically") || prompt.Contains("particularly") ||
                       prompt.Contains("especially");
            },
            Severity = ValidationSeverity.Warning
        });
        
        // Rule 5: Has context or constraints
        _rules.Add(new ValidationRule
        {
            Name = "HasContext",
            Description = "Should provide context or constraints for the task",
            Check = prompt => prompt.Length > 100,  // Implies some substance
            Severity = ValidationSeverity.Info
        });
        
        // Rule 6: No generic "assistant" without specialization
        _rules.Add(new ValidationRule
        {
            Name = "AvoidGeneric",
            Description = "Should avoid generic 'assistant' without specialization",
            Check = prompt =>
            {
                if (!prompt.Contains("assistant")) return true;
                
                var specializations = new[] { "expert", "specialist", "professional", "developer", 
                    "engineer", "architect", "analyst", "reviewer", "debugger" };
                return specializations.Any(s => prompt.ToLower().Contains(s));
            },
            Severity = ValidationSeverity.Info
        });
    }
    
    /// <summary>
    /// Validate a system prompt and return detailed results
    /// </summary>
    public SystemPromptValidationResult Validate(string prompt)
    {
        var result = new SystemPromptValidationResult
        {
            Prompt = prompt,
            Length = prompt.Length,
            LineCount = prompt.Split('\n').Length,
            Timestamp = DateTime.UtcNow
        };
        
        foreach (var rule in _rules)
        {
            try
            {
                var passed = rule.Check(prompt);
                result.RuleResults.Add(new RuleResult
                {
                    RuleName = rule.Name,
                    Description = rule.Description,
                    Passed = passed,
                    Severity = rule.Severity
                });
            }
            catch (Exception ex)
            {
                result.RuleResults.Add(new RuleResult
                {
                    RuleName = rule.Name,
                    Description = rule.Description,
                    Passed = false,
                    Severity = ValidationSeverity.Error,
                    Error = ex.Message
                });
            }
        }
        
        // Calculate overall score
        var errors = result.RuleResults.Where(r => r.Severity == ValidationSeverity.Error && !r.Passed).ToList();
        var warnings = result.RuleResults.Where(r => r.Severity == ValidationSeverity.Warning && !r.Passed).ToList();
        
        result.Score = CalculateScore(errors.Count, warnings.Count, _rules.Count);
        result.IsValid = errors.Count == 0;
        
        return result;
    }
    
    /// <summary>
    /// Validate multiple prompts from skills
    /// </summary>
    public SystemPromptAuditResult AuditSkillPrompts(Dictionary<string, List<string>> skillPrompts)
    {
        var auditResult = new SystemPromptAuditResult
        {
            AuditTime = DateTime.UtcNow,
            TotalSkills = skillPrompts.Count
        };
        
        foreach (var kvp in skillPrompts)
        {
            var skillName = kvp.Key;
            var prompts = kvp.Value;
            
            foreach (var prompt in prompts)
            {
                var validation = Validate(prompt);
                auditResult.ValidationResults.Add(new SkillPromptValidation
                {
                    SkillName = skillName,
                    ValidationResult = validation
                });
            }
        }
        
        auditResult.AverageScore = auditResult.ValidationResults.Count > 0
            ? auditResult.ValidationResults.Average(v => v.ValidationResult.Score)
            : 0;
        
        return auditResult;
    }
    
    private double CalculateScore(int errorCount, int warningCount, int totalRules)
    {
        // Score out of 100
        // 30 points deducted per error, 10 points deducted per warning
        var score = 100.0 - (errorCount * 30) - (warningCount * 10);
        return Math.Max(0, Math.Min(100, score));
    }
}

/// <summary>
/// Validation rule definition
/// </summary>
public class ValidationRule
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Func<string, bool> Check { get; set; } = _ => true;
    public ValidationSeverity Severity { get; set; } = ValidationSeverity.Warning;
}

/// <summary>
/// Severity levels for validation issues
/// </summary>
public enum ValidationSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

/// <summary>
/// Individual rule result
/// </summary>
public class RuleResult
{
    public string RuleName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public ValidationSeverity Severity { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Complete validation result for a single prompt
/// </summary>
public class SystemPromptValidationResult
{
    public string Prompt { get; set; } = string.Empty;
    public int Length { get; set; }
    public int LineCount { get; set; }
    public double Score { get; set; }
    public bool IsValid { get; set; }
    public DateTime Timestamp { get; set; }
    public List<RuleResult> RuleResults { get; set; } = new();
    
    public override string ToString()
    {
        var status = IsValid ? "✓ PASS" : "✗ FAIL";
        var summary = $"{status} | Score: {Score:F1}/100 | {Length} chars | {LineCount} lines";
        
        var issues = RuleResults.Where(r => !r.Passed).ToList();
        if (issues.Count == 0)
            return summary;
        
        var details = string.Join("\n  ", issues.Select(i => $"- [{i.Severity}] {i.Description}"));
        return $"{summary}\n  {details}";
    }
}

/// <summary>
/// Skill-specific prompt validation
/// </summary>
public class SkillPromptValidation
{
    public string SkillName { get; set; } = string.Empty;
    public SystemPromptValidationResult ValidationResult { get; set; } = new();
}

/// <summary>
/// Audit result for all skill prompts
/// </summary>
public class SystemPromptAuditResult
{
    public DateTime AuditTime { get; set; }
    public int TotalSkills { get; set; }
    public double AverageScore { get; set; }
    public List<SkillPromptValidation> ValidationResults { get; set; } = new();
    
    public int PassCount => ValidationResults.Count(v => v.ValidationResult.IsValid);
    public int FailCount => ValidationResults.Count(v => !v.ValidationResult.IsValid);
    
    public override string ToString()
    {
        var summary = $"=== System Prompt Audit ===\n" +
                     $"Timestamp: {AuditTime:O}\n" +
                     $"Total Skills: {TotalSkills}\n" +
                     $"Pass: {PassCount} | Fail: {FailCount}\n" +
                     $"Average Score: {AverageScore:F1}/100\n\n";
        
        var details = string.Join("\n", ValidationResults.Select(v =>
            $"{v.SkillName}: {v.ValidationResult}"));
        
        return summary + details;
    }
}
