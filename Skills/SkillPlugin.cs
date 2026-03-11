using System.Text.RegularExpressions;
using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using AgentFox.Tools;

namespace AgentFox.Skills;

/// <summary>
/// Metadata about a skill's capabilities and usage
/// </summary>
public class SkillMetadata
{
    public string SkillName { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public List<string> Capabilities { get; set; } = new();                // e.g., ["vcs", "versioning"]
    public List<string> Tags { get; set; } = new();                        // e.g., ["devops", "required"]
    public string InputType { get; set; } = "any";                         // Domain: code, db, api, etc.
    public string OutputType { get; set; } = "any";                        // What it returns
    public int ComplexityScore { get; set; } = 5;                          // 1-10
    public bool IsCompositional { get; set; }                              // Can chain with others
    public List<string> RelatedSkills { get; set; } = new();               // Natural combinations
    public string? Author { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Context provided to skills during plugin registration
/// </summary>
public interface ISkillRegistrationContext
{
    void RegisterTool(ITool tool);
    void PrependSystemContext(string guidance);
    void AppendSystemContext(string guidance);
    ToolEventHookRegistry HookRegistry { get; }
    ILogger Logger { get; }
}

/// <summary>
/// Implementation of skill registration context
/// </summary>
public class SkillRegistrationContext : ISkillRegistrationContext
{
    private readonly List<string> _prependedContexts = new();
    private readonly List<string> _appendedContexts = new();
    private readonly List<ITool> _tools = new();
    private readonly SystemPromptBuilder? _promptBuilder;
    
    public ToolEventHookRegistry HookRegistry { get; }
    public ILogger Logger { get; }
    
    public IReadOnlyList<string> PrependedContexts => _prependedContexts.AsReadOnly();
    public IReadOnlyList<string> AppendedContexts => _appendedContexts.AsReadOnly();
    public IReadOnlyList<ITool> Tools => _tools.AsReadOnly();
    
    public SkillRegistrationContext(
        ILogger logger,
        ToolEventHookRegistry? hooks = null,
        SystemPromptBuilder? promptBuilder = null)
    {
        Logger = logger;
        HookRegistry = hooks ?? new ToolEventHookRegistry();
        _promptBuilder = promptBuilder;
    }
    
    public void RegisterTool(ITool tool)
    {
        _tools.Add(tool);
    }
    
    public void PrependSystemContext(string guidance)
    {
        _prependedContexts.Add(guidance);
        _promptBuilder?.PrependSystemContext(guidance);
    }
    
    public void AppendSystemContext(string guidance)
    {
        _appendedContexts.Add(guidance);
        _promptBuilder?.AppendSystemContext(guidance);
    }
}

/// <summary>
/// Plugin interface for skills to customize registration behavior
/// </summary>
public interface ISkillPlugin
{
    /// <summary>
    /// Called when skill is registered - plugins can inject tools and guidance
    /// </summary>
    Task OnRegisterAsync(ISkillRegistrationContext context);
}

/// <summary>
/// Descriptor for a skill from a skill.md/skill.json file
/// </summary>
public class SkillDescriptor
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public List<string> Dependencies { get; set; } = new();
    public List<string> Capabilities { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public string? SkillAssemblyType { get; set; }                         // Type name if assembly-based
    public Dictionary<string, object> Configuration { get; set; } = new();
}

/// <summary>
/// Loads skills from a directory structure with skill.md and skill.json files
/// </summary>
public class SkillLoader
{
    private readonly string _skillsDirectory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SkillLoader> _logger;
    
    public SkillLoader(string skillsDirectory, IServiceProvider serviceProvider, ILogger<SkillLoader> logger)
    {
        _skillsDirectory = skillsDirectory;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }
    
    /// <summary>
    /// Load all skills from the skills directory
    /// </summary>
    public async Task<List<Skill>> LoadSkillsFromDirectoryAsync()
    {
        var skills = new List<Skill>();
        
        if (!Directory.Exists(_skillsDirectory))
        {
            _logger.LogWarning($"Skills directory not found: {_skillsDirectory}");
            return skills;
        }
        
        var skillDirs = Directory.GetDirectories(_skillsDirectory);
        _logger.LogInformation($"Found {skillDirs.Length} skill directories");
        
        foreach (var skillDir in skillDirs)
        {
            try
            {
                var skillName = Path.GetFileName(skillDir);
                var descriptorPath = Path.Combine(skillDir, "skill.md");
                var configPath = Path.Combine(skillDir, "skill.json");
                
                if (!File.Exists(configPath))
                {
                    _logger.LogWarning($"skill.json not found for skill '{skillName}', skipping");
                    continue;
                }
                
                // Parse skill descriptor
                var descriptor = await ParseSkillDescriptor(descriptorPath, configPath);
                
                // Try to instantiate skill
                var skill = await InstantiateSkillAsync(descriptor, skillDir);
                if (skill != null)
                {
                    skills.Add(skill);
                    _logger.LogInformation($"Loaded skill: {skill.Name} (v{skill.Version})");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load skill from {skillDir}: {ex.Message}");
            }
        }
        
        return skills;
    }
    
    /// <summary>
    /// Parse skill metadata from skill.json (and optionally extract from skill.md)
    /// </summary>
    private async Task<SkillDescriptor> ParseSkillDescriptor(string mdPath, string jsonPath)
    {
        var jsonContent = await File.ReadAllTextAsync(jsonPath);
        var descriptor = JsonSerializer.Deserialize<SkillDescriptor>(jsonContent)
            ?? throw new InvalidOperationException("Failed to parse skill.json");
        
        // Extract additional metadata from skill.md if present
        if (File.Exists(mdPath))
        {
            var mdContent = await File.ReadAllTextAsync(mdPath);
            ExtractCapabilitiesFromMarkdown(mdContent, descriptor);
        }
        
        return descriptor;
    }
    
    /// <summary>
    /// Extract capabilities from markdown "## Capabilities" section
    /// </summary>
    private void ExtractCapabilitiesFromMarkdown(string markdown, SkillDescriptor descriptor)
    {
        // Look for "## Capabilities" or "### Capabilities" section
        var capabilityMatch = Regex.Match(markdown, @"##+ Capabilities\s*\n([\s\S]*?)(?=\n##|\Z)", RegexOptions.IgnoreCase);
        if (capabilityMatch.Success)
        {
            var capabilitiesSection = capabilityMatch.Groups[1].Value;
            var capabilities = Regex.Matches(capabilitiesSection, @"^- (.+?)$", RegexOptions.Multiline)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value.Trim())
                .ToList();
            
            descriptor.Capabilities.AddRange(capabilities);
        }
    }
    
    /// <summary>
    /// Instantiate a skill from its descriptor
    /// </summary>
    private async Task<Skill?> InstantiateSkillAsync(SkillDescriptor descriptor, string skillDir)
    {
        Skill? skill = null;
        
        // Try to load from assembly-based type
        if (!string.IsNullOrEmpty(descriptor.SkillAssemblyType))
        {
            skill = TryLoadAssemblySkill(descriptor.SkillAssemblyType, descriptor);
        }
        
        // If no assembly type or failed, return default generic skill
        if (skill == null)
        {
            skill = new GenericSkill(descriptor);
        }
        
        // Apply configuration if needed
        if (descriptor.Configuration.Count > 0)
        {
            await skill.InitializeAsync();
        }
        
        return skill;
    }
    
    /// <summary>
    /// Try to load a skill from its assembly type name
    /// </summary>
    private Skill? TryLoadAssemblySkill(string typeName, SkillDescriptor descriptor)
    {
        try
        {
            var type = Type.GetType(typeName);
            if (type == null || !typeof(Skill).IsAssignableFrom(type))
            {
                _logger.LogWarning($"Skill type not found or not a Skill: {typeName}");
                return null;
            }
            
            var ctor = type.GetConstructor(Type.EmptyTypes);
            if (ctor == null)
            {
                _logger.LogWarning($"Skill type has no parameterless constructor: {typeName}");
                return null;
            }
            
            return (Skill?)Activator.CreateInstance(type);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to instantiate skill type {typeName}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Generic skill implementation for descriptor-based skills without custom code
/// </summary>
public class GenericSkill : Skill
{
    private readonly SkillDescriptor _descriptor;
    
    public GenericSkill(SkillDescriptor descriptor)
    {
        _descriptor = descriptor;
        Name = descriptor.Name;
        Description = descriptor.Description;
        Version = descriptor.Version;
        Dependencies = descriptor.Dependencies;
        
        // Create metadata
        Metadata = new SkillMetadata
        {
            SkillName = descriptor.Name,
            Version = descriptor.Version,
            Capabilities = descriptor.Capabilities,
            Tags = descriptor.Tags
        };
    }
    
    public new SkillMetadata? Metadata { get; set; }
    
    public override List<ITool> GetTools() => new();  // No tools for generic skill
    
    public override List<string> GetSystemPrompts() => new();
}
