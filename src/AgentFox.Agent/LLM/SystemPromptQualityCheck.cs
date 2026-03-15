using AgentFox.Skills;
using AgentFox.Tools;

namespace AgentFox.LLM;

/// <summary>
/// System Prompt Quality Check - Quick verification utility
/// Run this to validate and check all system prompts in the project
/// </summary>
public static class SystemPromptQualityCheck
{
    /// <summary>
    /// Run complete system prompt audit and display results
    /// </summary>
    public static async Task RunAuditAsync(SkillRegistry skillRegistry)
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║         System Prompt Quality Audit - Starting             ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        
        var manager = new SystemPromptManager(skillRegistry);
        
        // Run audit
        Console.WriteLine("Auditing all system prompts...");
        var auditResult = manager.AuditAllPrompts();
        Console.WriteLine();
        
        // Display results
        Console.WriteLine(manager.ExportAuditReport(auditResult));
        Console.WriteLine();
        
        // Display recommendations for failing prompts
        var failingSkills = auditResult.ValidationResults
            .Where(v => !v.ValidationResult.IsValid)
            .ToList();
        
        if (failingSkills.Count > 0)
        {
            Console.WriteLine("─────────────────────────────────────────────────────");
            Console.WriteLine("RECOMMENDATIONS FOR IMPROVEMENT:");
            Console.WriteLine("─────────────────────────────────────────────────────");
            
            foreach (var skillValidation in failingSkills)
            {
                var improvements = manager.GetImprovementRecommendations(skillValidation.SkillName);
                if (improvements.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"📌 {skillValidation.SkillName}:");
                    foreach (var improvement in improvements)
                    {
                        Console.WriteLine($"   {improvement}");
                    }
                }
            }
        }
        
        Console.WriteLine();
        Console.WriteLine("═════════════════════════════════════════════════════════════");
        Console.WriteLine($"Audit Complete: {auditResult.PassCount}/{auditResult.TotalSkills} skills passed");
        Console.WriteLine("═════════════════════════════════════════════════════════════");
    }
    
    /// <summary>
    /// Check a specific skill's prompt
    /// </summary>
    public static void CheckSkillPrompt(SkillRegistry skillRegistry, string skillName)
    {
        Console.WriteLine($"\nChecking prompt for skill: {skillName}");
        Console.WriteLine("─────────────────────────────────────────────────────");
        
        var manager = new SystemPromptManager(skillRegistry);
        var result = manager.GetSkillPrompt(skillName);
        
        if (!result.Success)
        {
            Console.WriteLine($"ERROR: {result.Error}");
            return;
        }
        
        Console.WriteLine($"Status: {result.ValidationResult?.IsValid}");
        Console.WriteLine($"Score: {result.ValidationResult?.Score:F1}/100");
        Console.WriteLine($"Length: {result.ValidationResult?.Length} characters");
        Console.WriteLine();
        Console.WriteLine("Validation Details:");
        
        foreach (var ruleResult in result.ValidationResult?.RuleResults ?? new())
        {
            var status = ruleResult.Passed ? "✓" : "✗";
            Console.WriteLine($"  {status} {ruleResult.RuleName}: {ruleResult.Description}");
        }
        
        Console.WriteLine();
        Console.WriteLine("Main Prompt Preview:");
        Console.WriteLine("─────────────────────────────────────────────────────");
        Console.WriteLine(result.MainPrompt?.Substring(0, Math.Min(500, result.MainPrompt.Length)) + "...");
    }
    
    /// <summary>
    /// Validate and display a custom prompt
    /// </summary>
    public static void ValidatePrompt(string prompt, string name = "Custom")
    {
        Console.WriteLine($"\nValidating prompt: {name}");
        Console.WriteLine("─────────────────────────────────────────────────────");
        
        var validator = new SystemPromptValidator();
        var result = validator.Validate(prompt);
        
        Console.WriteLine($"Result: {(result.IsValid ? "✓ VALID" : "✗ INVALID")}");
        Console.WriteLine($"Score: {result.Score:F1}/100");
        Console.WriteLine($"Length: {result.Length} characters ({result.LineCount} lines)");
        Console.WriteLine();
        Console.WriteLine("Rule Results:");
        
        foreach (var ruleResult in result.RuleResults)
        {
            var status = ruleResult.Passed ? "✓" : "✗";
            var severity = ruleResult.Severity.ToString().PadRight(7);
            Console.WriteLine($"  {status} [{severity}] {ruleResult.Description}");
            if (!ruleResult.Passed && ruleResult.Error != null)
                Console.WriteLine($"        Error: {ruleResult.Error}");
        }
    }
    
    /// <summary>
    /// Compare two prompts
    /// </summary>
    public static void ComparePrompts(string prompt1, string prompt2, string name1 = "Prompt1", string name2 = "Prompt2")
    {
        Console.WriteLine($"\nComparing prompts: {name1} vs {name2}");
        Console.WriteLine("═════════════════════════════════════════════════════════════");
        
        var validator = new SystemPromptValidator();
        var result1 = validator.Validate(prompt1);
        var result2 = validator.Validate(prompt2);
        
        Console.WriteLine($"\n{name1}:");
        Console.WriteLine($"  Score: {result1.Score:F1}/100");
        Console.WriteLine($"  Length: {result1.Length} chars");
        Console.WriteLine($"  Valid: {(result1.IsValid ? "✓ YES" : "✗ NO")}");
        
        Console.WriteLine($"\n{name2}:");
        Console.WriteLine($"  Score: {result2.Score:F1}/100");
        Console.WriteLine($"  Length: {result2.Length} chars");
        Console.WriteLine($"  Valid: {(result2.IsValid ? "✓ YES" : "✗ NO")}");
        
        var diff = result2.Score - result1.Score;
        var diffSymbol = diff > 0 ? "▲" : (diff < 0 ? "▼" : "═");
        Console.WriteLine($"\nDifference: {diffSymbol} {Math.Abs(diff):F1} points");
        
        // Show which rules each passes/fails
        Console.WriteLine("\nRule Comparison:");
        var allRules = result1.RuleResults.Select(r => r.RuleName).Union(
            result2.RuleResults.Select(r => r.RuleName)).Distinct();
        
        foreach (var rule in allRules)
        {
            var status1 = result1.RuleResults.FirstOrDefault(r => r.RuleName == rule)?.Passed ?? false;
            var status2 = result2.RuleResults.FirstOrDefault(r => r.RuleName == rule)?.Passed ?? false;
            
            var s1 = status1 ? "✓" : "✗";
            var s2 = status2 ? "✓" : "✗";
            Console.WriteLine($"  {s1} {s2}  {rule}");
        }
    }
    
    /// <summary>
    /// Generate prompt recommendations for a specific domain
    /// </summary>
    public static void ShowPromptTemplate(string domain)
    {
        var template = domain.ToLower() switch
        {
            "developer" => SystemPromptConfig.AgentPrompts.DeveloperAssistant,
            "analyst" => SystemPromptConfig.AgentPrompts.DataAnalyst,
            "engineer" => SystemPromptConfig.AgentPrompts.SystemEngineer,
            "codereview" => SystemPromptConfig.SkillPrompts.CodeReviewExpert,
            "debugging" => SystemPromptConfig.SkillPrompts.DebuggingExpert,
            "git" => SystemPromptConfig.SkillPrompts.GitExpert,
            "docker" => SystemPromptConfig.SkillPrompts.DockerExpert,
            "api" => SystemPromptConfig.SkillPrompts.APIIntegrationExpert,
            "database" => SystemPromptConfig.SkillPrompts.DatabaseExpert,
            "testing" => SystemPromptConfig.SkillPrompts.TestingExpert,
            "deployment" => SystemPromptConfig.SkillPrompts.DeploymentExpert,
            _ => null
        };
        
        if (template == null)
        {
            Console.WriteLine($"Unknown domain: {domain}");
            Console.WriteLine("Available domains: developer, analyst, engineer, codereview, debugging, git, docker, api, database, testing, deployment");
            return;
        }
        
        Console.WriteLine($"\n╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine($"║  System Prompt Template: {domain.ToUpper()}".PadRight(62) + "║");
        Console.WriteLine($"╚════════════════════════════════════════════════════════════╝\n");
        Console.WriteLine(template);
    }
}
