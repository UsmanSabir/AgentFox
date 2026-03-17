using AgentFox.LLM;
using AgentFox.Tools;
using Microsoft.Extensions.Logging;

namespace AgentFox.Skills;

/// <summary>
/// Dummy logger for when no real logger is available
/// </summary>
internal class DummyLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}

/// <summary>
/// Base class for all skills with dependency support and metadata
/// </summary>
public abstract class Skill
{
    public string Name { get; protected set; } = string.Empty;
    public string Description { get; protected set; } = string.Empty;
    public List<string> Dependencies { get; protected set; } = new();
    public string? Version { get; protected set; } = "1.0.0";
    
    /// <summary>
    /// Metadata about this skill's capabilities
    /// </summary>
    public virtual SkillMetadata? Metadata { get; protected set; }
    
    /// <summary>
    /// Initialize the skill
    /// </summary>
    public virtual Task InitializeAsync() => Task.CompletedTask;
    
    /// <summary>
    /// Get tools provided by this skill
    /// </summary>
    public abstract List<ITool> GetTools();
    
    /// <summary>
    /// Get system prompts provided by this skill
    /// </summary>
    public virtual List<string> GetSystemPrompts() => new();
    
    /// <summary>
    /// Execute a skill with the given parameters
    /// </summary>
    public virtual async Task<SkillExecutionResult> Execute(
        EnhancedSkillExecutionContext context,
        Dictionary<string, object> parameters)
    {
        // Default implementation - subclasses can override
        return await Task.FromResult(new SkillExecutionResult
        {
            Success = false,
            Message = "Skill execution not implemented"
        });
    }
}

/// <summary>
/// Skill registry for managing available skills with dependency resolution,
/// permissions, metrics, and plugin support
/// </summary>
public class SkillRegistry
{
    private readonly Dictionary<string, Skill> _skills = new();
    private readonly Dictionary<string, SkillPermission> _permissions = new();
    private readonly HashSet<string> _enabledSkills = new();
    private readonly ToolRegistry _toolRegistry;
    private readonly SkillMetricsCollector _metricsCollector;
    private readonly SystemPromptBuilder? _promptBuilder;
    private readonly ILogger<SkillRegistry>? _logger;
    private readonly ResilientSkillExecutor? _resilientExecutor;
    private readonly object _lock = new();
    
    /// <summary>
    /// Event hooks for skill lifecycle
    /// </summary>
    public ToolEventHookRegistry HookRegistry { get; } = new();
    
    public SkillRegistry(
        ToolRegistry toolRegistry,
        SystemPromptBuilder? promptBuilder = null,
        ILogger<SkillRegistry>? logger = null,
        int? maxMetricsToStore = null)
    {
        _toolRegistry = toolRegistry;
        _promptBuilder = promptBuilder;
        _logger = logger;
        _metricsCollector = new SkillMetricsCollector(maxMetricsToStore);
        
        // Create resilient executor with a dummy logger if none provided
        ILogger<ResilientSkillExecutor>? loggerForExecutor = null;
        if (logger is ILogger<ResilientSkillExecutor> typedLogger)
        {
            loggerForExecutor = typedLogger;
        }
        _resilientExecutor = new ResilientSkillExecutor(loggerForExecutor ?? new DummyLogger<ResilientSkillExecutor>());
        
        RegisterBuiltInSkills();
        
        // Register the lazy skill loader tool so the LLM can load skill guidance on demand
        _toolRegistry.Register(new LoadSkillTool(this));
    }
    
    private void RegisterBuiltInSkills()
    {
        // Register Composio dev skills
        Register(new GitSkill());
        Register(new DockerSkill());
        Register(new CodeReviewSkill());
        Register(new DebuggingSkill());
        Register(new APIIntegrationSkill());
        Register(new DatabaseSkill());
        Register(new TestingSkill());
        Register(new DeploymentSkill());
    }

    /// <summary>
    /// Register Composio.dev skills provider
    /// </summary>
    public async Task RegisterComposioSkillsAsync(
        string composioApiKey,
        IEnumerable<string>? filterIntegrationIds = null)
    {
        try
        {
            _logger?.LogInformation("Registering Composio.dev skills");
            
            var composioProvider = new ComposioSkillProvider(
                apiKey: composioApiKey,
                skillRegistry: this,
                logger: _logger as ILogger<ComposioSkillProvider>
            );
            
            await composioProvider.InitializeAsync(filterIntegrationIds);
            
            _logger?.LogInformation("Composio.dev skills registered successfully");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to register Composio.dev skills");
            throw;
        }
    }
    
    /// <summary>
    /// Register a skill
    /// </summary>
    public void Register(Skill skill)
    {
        lock (_lock)
        {
            _skills[skill.Name] = skill;
            
            // Create default permission if not exists
            if (!_permissions.ContainsKey(skill.Name))
            {
                _permissions[skill.Name] = new SkillPermission
                {
                    SkillName = skill.Name,
                    AllowedAgentRoles = new[] { "default", "admin" }.ToList()
                };
            }
        }
    }
    
    /// <summary>
    /// Register a skill permission
    /// </summary>
    public void RegisterPermission(SkillPermission permission)
    {
        lock (_lock)
        {
            _permissions[permission.SkillName] = permission;
        }
    }
    
    /// <summary>
    /// Unregister a skill
    /// </summary>
    public void Unregister(string name)
    {
        lock (_lock)
        {
            _skills.Remove(name);
            _permissions.Remove(name);
            _enabledSkills.Remove(name);
        }
    }
    
    /// <summary>
    /// Get a skill by name
    /// </summary>
    public Skill? Get(string name)
    {
        lock (_lock)
        {
            return _skills.TryGetValue(name, out var skill) ? skill : null;
        }
    }
    
    /// <summary>
    /// Get all registered skills
    /// </summary>
    public List<Skill> GetAll()
    {
        lock (_lock)
        {
            return _skills.Values.ToList();
        }
    }
    
    /// <summary>
    /// Get all currently enabled skills
    /// </summary>
    public List<Skill> GetEnabledSkills()
    {
        lock (_lock)
        {
            return _skills.Where(kv => _enabledSkills.Contains(kv.Key))
                          .Select(kv => kv.Value)
                          .ToList();
        }
    }
    
    /// <summary>
    /// Get available skills for an agent (with permission checks)
    /// </summary>
    public List<Skill> GetAvailableSkillsFor(AgentFox.Models.Agent agent)
    {
        // Default role when not specified in agent config
        var agentRole = "default";
        
        lock (_lock)
        {
            return _skills
                .Where(kv => _enabledSkills.Contains(kv.Key) &&  // Must be enabled
                    _permissions[kv.Key].AllowedAgentRoles.Contains(agentRole))  // Must have permission
                .Select(kv => kv.Value)
                .ToList();
        }
    }
    
    /// <summary>
    /// Get skills by capability (semantic discovery)
    /// </summary>
    public List<Skill> DiscoverSkillsByCapability(string capability)
    {
        var lowerCapability = capability.ToLower();
        
        lock (_lock)
        {
            return _skills
                .Where(kv => _enabledSkills.Contains(kv.Key) &&
                    (kv.Value.Metadata?.Capabilities.Any(c => 
                        c.Contains(lowerCapability, StringComparison.OrdinalIgnoreCase)) ?? false))
                .Select(kv => kv.Value)
                .ToList();
        }
    }
    
    /// <summary>
    /// Get skills by tag
    /// </summary>
    public List<Skill> DiscoverSkillsByTag(string tag)
    {
        var lowerTag = tag.ToLower();
        
        lock (_lock)
        {
            return _skills
                .Where(kv => _enabledSkills.Contains(kv.Key) &&
                    (kv.Value.Metadata?.Tags.Any(t => 
                        t.Contains(lowerTag, StringComparison.OrdinalIgnoreCase)) ?? false))
                .Select(kv => kv.Value)
                .ToList();
        }
    }
    
    /// <summary>
    /// Check if a skill is currently enabled
    /// </summary>
    public bool IsSkillEnabled(string name)
    {
        lock (_lock)
        {
            return _enabledSkills.Contains(name);
        }
    }
    
    /// <summary>
    /// Enable a skill with automatic dependency resolution
    /// </summary>
    public async Task EnableSkillAsync(string name, ILogger? logger = null)
    {
        lock (_lock)
        {
            if (_enabledSkills.Contains(name))
                return;  // Already enabled
        }
        
        var skill = Get(name);
        if (skill == null)
            throw new InvalidOperationException($"Skill '{name}' not found");
        
        logger ??= _logger;
        logger?.LogInformation($"Enabling skill: {name}");
        
        // Invoke pre-enable hook
        await HookRegistry.InvokeSkillPreEnableAsync(name);
        
        try
        {
            // Resolve and enable dependencies first
            foreach (var depName in skill.Dependencies)
            {
                if (!IsSkillEnabled(depName))
                {
                    await EnableSkillAsync(depName, logger);  // Recursive dependency resolution
                }
            }
            
            // Initialize the skill
            await skill.InitializeAsync();
            
            // Register the skill's tools
            var tools = skill.GetTools();
            foreach (var tool in tools)
            {
                _toolRegistry.Register(tool);
            }
            
            // If skill is a plugin, call registration hook
            if (skill is ISkillPlugin plugin)
            {
                var loggerForContext = logger ?? (_logger ?? new DummyLogger<SkillRegistrationContext>() as ILogger);
                var context = new SkillRegistrationContext(loggerForContext, HookRegistry, _promptBuilder);
                await plugin.OnRegisterAsync(context);
            }
            
            // Mark as enabled
            lock (_lock)
            {
                _enabledSkills.Add(name);
            }
            
            // Invoke post-enable hook
            await HookRegistry.InvokeSkillPostEnableAsync(name, tools.Count);
            logger?.LogInformation($"Skill enabled successfully: {name} with {tools.Count} tools");
        }
        catch (Exception ex)
        {
            await HookRegistry.InvokeSkillErrorAsync(name, ex.Message);
            logger?.LogError($"Failed to enable skill {name}: {ex.Message}");
            throw;
        }
    }
    
    /// <summary>
    /// Disable a skill and optionally its dependents
    /// </summary>
    public async Task DisableSkillAsync(string name, bool disableDependents = false, ILogger? logger = null)
    {
        var skill = Get(name);
        if (skill == null)
            return;
        
        logger ??= _logger;
        logger?.LogInformation($"Disabling skill: {name}");
        
        // If disableDependents is true, find and disable skills that depend on this one
        if (disableDependents)
        {
            var dependents = _skills.Values
                .Where(s => s.Dependencies.Contains(name))
                .ToList();
            
            foreach (var dependent in dependents)
            {
                await DisableSkillAsync(dependent.Name, true, logger);
            }
        }
        
        // Unregister tools
        foreach (var tool in skill.GetTools())
        {
            _toolRegistry.Unregister(tool.Name);
        }
        
        lock (_lock)
        {
            _enabledSkills.Remove(name);
        }
        
        // Invoke disabled hook
        await HookRegistry.InvokeSkillDisabledAsync(name);
    }
    
    /// <summary>
    /// Disable a skill (synchronous, for backward compatibility)
    /// </summary>
    public void DisableSkill(string name)
    {
        var skill = Get(name);
        if (skill != null)
        {
            foreach (var tool in skill.GetTools())
            {
                _toolRegistry.Unregister(tool.Name);
            }
            
            lock (_lock)
            {
                _enabledSkills.Remove(name);
            }
        }
    }
    
    /// <summary>
    /// Get dependency tree for a skill
    /// </summary>
    public List<string> GetDependencyTree(string skillName)
    {
        var result = new HashSet<string> { skillName };
        var queue = new Queue<string>(new[] { skillName });
        
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var skill = Get(current);
            
            if (skill != null)
            {
                foreach (var dep in skill.Dependencies)
                {
                    if (result.Add(dep))
                        queue.Enqueue(dep);
                }
            }
        }
        
        return result.ToList();
    }
    
    /// <summary>
    /// Get metrics collector
    /// </summary>
    public SkillMetricsCollector GetMetricsCollector() => _metricsCollector;
    
    /// <summary>
    /// Get resilient executor
    /// </summary>
    public ResilientSkillExecutor GetResilientExecutor() => _resilientExecutor!;

    /// <summary>
    /// Get lightweight manifests for all registered skills (used for system prompt skills index).
    /// Does NOT load full skill content.
    /// </summary>
    public List<SkillManifest> GetSkillManifests()
    {
        lock (_lock)
        {
            return _skills.Values.Select(s =>
            {
                // If skill implements ISkillPlugin it can return a custom manifest
                if (s is ISkillPlugin plugin)
                    return plugin.GetManifest();

                // Fallback for skills that don't implement ISkillPlugin
                return new SkillManifest(
                    s.Name,
                    s.Description,
                    s.GetTools().Count,
                    "local");
            }).ToList();
        }
    }
}

/// <summary>
/// Git operations skill (Composio dev skill)
/// </summary>
public class GitSkill : Skill, ISkillPlugin
{
    public GitSkill()
    {
        Name = "git";
        Description = "Git version control operations - commit, push, pull, branch, merge, etc.";
        Version = "1.0.0";
        
        Metadata = new SkillMetadata
        {
            SkillName = "git",
            Version = "1.0.0",
            Capabilities = new[] { "vcs", "versioning", "version-control" }.ToList(),
            Tags = new[] { "devops", "required" }.ToList(),
            InputType = "code",
            OutputType = "status",
            ComplexityScore = 6,
            IsCompositional = true,
            RelatedSkills = new[] { "docker", "deployment" }.ToList()
        };
    }

    public SkillManifest GetManifest() => new(Name, Description, 6, "local");
    
    public override List<ITool> GetTools()
    {
        return new List<ITool>
        {
            new GitCommitTool(),
            new GitPushTool(),
            new GitPullTool(),
            new GitBranchTool(),
            new GitStatusTool(),
            new GitLogTool()
        };
    }
    
    public override List<string> GetSystemPrompts()
    {
        return new List<string>
        {
            SystemPromptConfig.SkillPrompts.GitExpert
        };
    }
    
    public async Task OnRegisterAsync(ISkillRegistrationContext context)
    {
        // Register all git tools (NOTE: full system prompt is NOT injected here;
        // the LLM loads it on-demand via load_skill tool)
        foreach (var tool in GetTools())
        {
            context.RegisterTool(tool);
        }

//         // Inject git usage guidance into agent's system prompt
//         context.PrependSystemContext(@"
// ## Git Workflow Best Practices

// When using Git tools:
// 1. Always create feature branches before making changes: `git_branch create feature-name`
// 2. Check current status before committing: `git_status`
// 3. Use descriptive commit messages (format: type(scope): description): `git_commit message:""type(scope): description""`
// 4. Pull before pushing to avoid conflicts: `git_pull`
// 5. Group related changes into a single commit when possible
// 6. For important releases, create and checkout appropriate branches
// 7. Review git log to track changes: `git_log count:10`
//         ");
    }
}

/// <summary>
/// Docker operations skill
/// </summary>
public class DockerSkill : Skill, ISkillPlugin
{
    public DockerSkill()
    {
        Name = "docker";
        Description = "Docker container operations - build, run, stop, logs, etc.";
        Version = "1.0.0";
        Dependencies = new[] { "git" }.ToList();  // Often used with git
        
        Metadata = new SkillMetadata
        {
            SkillName = "docker",
            Version = "1.0.0",
            Capabilities = new[] { "container", "deployment", "devops" }.ToList(),
            Tags = new[] { "docker", "containers", "devops" }.ToList(),
            InputType = "container",
            OutputType = "container-status",
            ComplexityScore = 7,
            IsCompositional = true,
            RelatedSkills = new[] { "git", "deployment" }.ToList()
        };
    }

    public SkillManifest GetManifest() => new(Name, Description, 5, "local");
    
    public override List<ITool> GetTools()
    {
        return new List<ITool>
        {
            new DockerBuildTool(),
            new DockerRunTool(),
            new DockerStopTool(),
            new DockerLogsTool(),
            new DockerPSTool()
        };
    }
    
    public override List<string> GetSystemPrompts()
    {
        return new List<string>
        {
            SystemPromptConfig.SkillPrompts.DockerExpert
        };
    }
    
    public async Task OnRegisterAsync(ISkillRegistrationContext context)
    {
        foreach (var tool in GetTools())
        {
            context.RegisterTool(tool);
        }
        // Full guidance loaded on-demand via load_skill tool
//         context.PrependSystemContext(@"
// ## Docker Best Practices

// When using Docker tools:
// 1. Always build with descriptive tags: `docker_build tag:""myapp:v1.0""`
// 2. Use meaningful container names for tracking: `docker_run name:""my-container""`
// 3. Check running containers before operations: `docker_ps all:true`
// 4. Monitor logs for debugging: `docker_logs tail:100`
// 5. Stop containers gracefully before removing
// 6. Use environment-specific tags for deployment artifacts
// 7. Keep images small and focused on single responsibilities
//         ");
    }
}

/// <summary>
/// Code review skill
/// </summary>
public class CodeReviewSkill : Skill
{
    public CodeReviewSkill()
    {
        Name = "code_review";
        Description = "Automated code review and quality analysis";
        Version = "1.0.0";
        
        Metadata = new SkillMetadata
        {
            SkillName = "code_review",
            Version = "1.0.0",
            Capabilities = new[] { "code_review", "quality", "analysis" }.ToList(),
            Tags = new[] { "code-quality", "review", "standards" }.ToList(),
            ComplexityScore = 5
        };
    }
    
    public override List<ITool> GetTools()
    {
        return new List<ITool>
        {
            new CodeReviewTool()
        };
    }
    
    public override List<string> GetSystemPrompts()
    {
        return new List<string>
        {
            SystemPromptConfig.SkillPrompts.CodeReviewExpert
        };
    }
}

/// <summary>
/// Debugging skill
/// </summary>
public class DebuggingSkill : Skill
{
    public DebuggingSkill()
    {
        Name = "debugging";
        Description = "Debug and diagnose application issues";
        Version = "1.0.0";
        
        Metadata = new SkillMetadata
        {
            SkillName = "debugging",
            Version = "1.0.0",
            Capabilities = new[] { "debugging", "diagnostics", "tracing", "profiling" }.ToList(),
            Tags = new[] { "debugging", "troubleshooting", "performance" }.ToList(),
            ComplexityScore = 7
        };
    }
    
    public override List<ITool> GetTools()
    {
        return new List<ITool>
        {
            new DebugTool(),
            new TraceTool(),
            new ProfileTool()
        };
    }
    
    public override List<string> GetSystemPrompts()
    {
        return new List<string>
        {
            SystemPromptConfig.SkillPrompts.DebuggingExpert
        };
    }
}

/// <summary>
/// API integration skill
/// </summary>
public class APIIntegrationSkill : Skill, ISkillPlugin
{
    public APIIntegrationSkill()
    {
        Name = "api_integration";
        Description = "API integration and REST/GraphQL operations";
        Version = "1.0.0";
        
        Metadata = new SkillMetadata
        {
            SkillName = "api_integration",
            Version = "1.0.0",
            Capabilities = new[] { "api_integration", "rest", "graphql", "http" }.ToList(),
            Tags = new[] { "api", "integration", "external-services" }.ToList(),
            InputType = "api",
            OutputType = "json",
            ComplexityScore = 6,
            IsCompositional = true
        };
    }

    public SkillManifest GetManifest() => new(Name, Description, 2, "local");
    
    public override List<ITool> GetTools()
    {
        return new List<ITool>
        {
            new RESTClientTool(),
            new GraphQLTool()
        };
    }
    
    public override List<string> GetSystemPrompts()
    {
        return new List<string>
        {
            SystemPromptConfig.SkillPrompts.APIIntegrationExpert
        };
    }
    
    public async Task OnRegisterAsync(ISkillRegistrationContext context)
    {
        foreach (var tool in GetTools())
        {
            context.RegisterTool(tool);
        }
        // Full guidance loaded on-demand via load_skill tool
//         context.PrependSystemContext(@"
// ## API Integration Best Practices

// When using API tools:
// 1. Always validate the endpoint URL before making the call
// 2. Include required authentication headers and tokens
// 3. For POST/PUT requests, validate JSON body structure
// 4. Check response status codes before processing data
// 5. Implement appropriate error handling and retry logic
// 6. Rate-limit API calls to respect service quotas
// 7. For paginated results, implement proper next-page logic
// 8. Cache results when appropriate to reduce API calls
//         ");
    }
}

/// <summary>
/// Database skill
/// </summary>
public class DatabaseSkill : Skill
{
    public DatabaseSkill()
    {
        Name = "database";
        Description = "Database operations and migrations";
        Version = "1.0.0";
        
        Metadata = new SkillMetadata
        {
            SkillName = "database",
            Version = "1.0.0",
            Capabilities = new[] { "database", "sql", "migrations", "queries" }.ToList(),
            Tags = new[] { "database", "persistence", "data" }.ToList(),
            ComplexityScore = 6
        };
    }
    
    public override List<ITool> GetTools()
    {
        return new List<ITool>
        {
            new DBQueryTool(),
            new DBMigrationTool()
        };
    }
    
    public override List<string> GetSystemPrompts()
    {
        return new List<string>
        {
            SystemPromptConfig.SkillPrompts.DatabaseExpert
        };
    }
}

/// <summary>
/// Testing skill
/// </summary>
public class TestingSkill : Skill
{
    public TestingSkill()
    {
        Name = "testing";
        Description = "Test creation and execution";
        Version = "1.0.0";
        
        Metadata = new SkillMetadata
        {
            SkillName = "testing",
            Version = "1.0.0",
            Capabilities = new[] { "testing", "quality", "coverage" }.ToList(),
            Tags = new[] { "testing", "qa", "quality-assurance" }.ToList(),
            ComplexityScore = 5
        };
    }
    
    public override List<ITool> GetTools()
    {
        return new List<ITool>
        {
            new RunTestsTool(),
            new CoverageTool()
        };
    }
    
    public override List<string> GetSystemPrompts()
    {
        return new List<string>
        {
            SystemPromptConfig.SkillPrompts.TestingExpert
        };
    }
}

/// <summary>
/// Deployment skill
/// </summary>
public class DeploymentSkill : Skill, ISkillPlugin
{
    public DeploymentSkill()
    {
        Name = "deployment";
        Description = "Application deployment and CI/CD";
        Version = "1.0.0";
        Dependencies = new[] { "git", "docker" }.ToList();  // Requires git and docker
        
        Metadata = new SkillMetadata
        {
            SkillName = "deployment",
            Version = "1.0.0",
            Capabilities = new[] { "deployment", "devops", "cicd", "release" }.ToList(),
            Tags = new[] { "deployment", "ci-cd", "production" }.ToList(),
            InputType = "application",
            OutputType = "deployment-status",
            ComplexityScore = 8,
            IsCompositional = true,
            RelatedSkills = new[] { "git", "docker", "testing" }.ToList()
        };
    }

    public SkillManifest GetManifest() => new(Name, Description, 2, "local");
    
    public override List<ITool> GetTools()
    {
        return new List<ITool>
        {
            new DeployTool(),
            new CICDPipelineTool()
        };
    }
    
    public override List<string> GetSystemPrompts()
    {
        return new List<string>
        {
            SystemPromptConfig.SkillPrompts.DeploymentExpert
        };
    }
    
    public async Task OnRegisterAsync(ISkillRegistrationContext context)
    {
        foreach (var tool in GetTools())
        {
            context.RegisterTool(tool);
        }
        // Full guidance loaded on-demand via load_skill tool
//         context.PrependSystemContext(@"
// ## Deployment Best Practices

// When using Deployment tools:
// 1. Always ensure all tests pass before deploying: `run_tests coverage:true`
// 2. Create a release commit before deployment
// 3. Use appropriate environment-specific settings
// 4. Verify that dependencies (git, docker) are working first
// 5. Run CI/CD pipeline in dry-run mode before actual deployment
// 6. Monitor deployment status and rollback if needed
// 7. Document deployment steps and create runbooks
// 8. Use blue-green or canary deployment strategies when possible
//         ");
    }
}

// Git Tools
public class GitCommitTool : BaseTool
{
    public override string Name => "git_commit";
    public override string Description => "Create a git commit with a message";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["message"] = new() { Type = "string", Description = "Commit message", Required = true },
        ["all"] = new() { Type = "boolean", Description = "Stage all changes", Required = false, Default = true }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("git commit executed (simulated)"));
}

public class GitPushTool : BaseTool
{
    public override string Name => "git_push";
    public override string Description => "Push commits to remote repository";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["remote"] = new() { Type = "string", Description = "Remote name", Required = false, Default = "origin" },
        ["branch"] = new() { Type = "string", Description = "Branch name", Required = false }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("git push executed (simulated)"));
}

public class GitPullTool : BaseTool
{
    public override string Name => "git_pull";
    public override string Description => "Pull changes from remote";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["remote"] = new() { Type = "string", Description = "Remote name", Required = false, Default = "origin" },
        ["branch"] = new() { Type = "string", Description = "Branch name", Required = false }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("git pull executed (simulated)"));
}

public class GitBranchTool : BaseTool
{
    public override string Name => "git_branch";
    public override string Description => "List, create, or delete branches";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["action"] = new() { Type = "string", Description = "Action: list, create, delete", Required = false, Default = "list" },
        ["name"] = new() { Type = "string", Description = "Branch name", Required = false }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Branches listed (simulated)"));
}

public class GitStatusTool : BaseTool
{
    public override string Name => "git_status";
    public override string Description => "Show working tree status";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new();
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Status: clean (simulated)"));
}

public class GitLogTool : BaseTool
{
    public override string Name => "git_log";
    public override string Description => "Show commit history";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["count"] = new() { Type = "number", Description = "Number of commits", Required = false, Default = 10 }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Commit log (simulated)"));
}

// Docker Tools
public class DockerBuildTool : BaseTool
{
    public override string Name => "docker_build";
    public override string Description => "Build a Docker image";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["tag"] = new() { Type = "string", Description = "Image tag", Required = true },
        ["path"] = new() { Type = "string", Description = "Dockerfile path", Required = false, Default = "." }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Docker image built (simulated)"));
}

public class DockerRunTool : BaseTool
{
    public override string Name => "docker_run";
    public override string Description => "Run a Docker container";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["image"] = new() { Type = "string", Description = "Image name", Required = true },
        ["name"] = new() { Type = "string", Description = "Container name", Required = false },
        ["detach"] = new() { Type = "boolean", Description = "Run in background", Required = false, Default = true }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Container started (simulated)"));
}

public class DockerStopTool : BaseTool
{
    public override string Name => "docker_stop";
    public override string Description => "Stop a Docker container";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["container"] = new() { Type = "string", Description = "Container name or ID", Required = true }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Container stopped (simulated)"));
}

public class DockerLogsTool : BaseTool
{
    public override string Name => "docker_logs";
    public override string Description => "Fetch container logs";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["container"] = new() { Type = "string", Description = "Container name or ID", Required = true },
        ["tail"] = new() { Type = "number", Description = "Number of lines", Required = false, Default = 100 }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Container logs (simulated)"));
}

public class DockerPSTool : BaseTool
{
    public override string Name => "docker_ps";
    public override string Description => "List running containers";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["all"] = new() { Type = "boolean", Description = "Show all containers", Required = false, Default = false }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Running containers (simulated)"));
}

// Code Review Tool
public class CodeReviewTool : BaseTool
{
    public override string Name => "code_review";
    public override string Description => "Perform automated code review";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["path"] = new() { Type = "string", Description = "Path to code to review", Required = true },
        ["language"] = new() { Type = "string", Description = "Programming language", Required = false }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Code review complete (simulated)"));
}

// Debugging Tools
public class DebugTool : BaseTool
{
    public override string Name => "debug";
    public override string Description => "Debug an application";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["target"] = new() { Type = "string", Description = "Application to debug", Required = true }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Debug session started (simulated)"));
}

public class TraceTool : BaseTool
{
    public override string Name => "trace";
    public override string Description => "Trace application execution";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["target"] = new() { Type = "string", Description = "Application to trace", Required = true }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Trace enabled (simulated)"));
}

public class ProfileTool : BaseTool
{
    public override string Name => "profile";
    public override string Description => "Profile application performance";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["target"] = new() { Type = "string", Description = "Application to profile", Required = true },
        ["duration"] = new() { Type = "number", Description = "Duration in seconds", Required = false, Default = 30 }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Profiling complete (simulated)"));
}

// API Tools
public class RESTClientTool : BaseTool
{
    public override string Name => "rest_call";
    public override string Description => "Make REST API calls";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["method"] = new() { Type = "string", Description = "HTTP method", Required = true },
        ["url"] = new() { Type = "string", Description = "API URL", Required = true },
        ["body"] = new() { Type = "string", Description = "Request body", Required = false }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("REST call executed (simulated)"));
}

public class GraphQLTool : BaseTool
{
    public override string Name => "graphql_query";
    public override string Description => "Execute GraphQL queries";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["query"] = new() { Type = "string", Description = "GraphQL query", Required = true },
        ["endpoint"] = new() { Type = "string", Description = "GraphQL endpoint", Required = true }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("GraphQL query executed (simulated)"));
}

// Database Tools
public class DBQueryTool : BaseTool
{
    public override string Name => "db_query";
    public override string Description => "Execute database queries";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["query"] = new() { Type = "string", Description = "SQL query", Required = true },
        ["connection"] = new() { Type = "string", Description = "Database connection string", Required = false }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Query executed (simulated)"));
}

public class DBMigrationTool : BaseTool
{
    public override string Name => "db_migrate";
    public override string Description => "Run database migrations";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["direction"] = new() { Type = "string", Description = "up or down", Required = false, Default = "up" }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Migration executed (simulated)"));
}

// Testing Tools
public class RunTestsTool : BaseTool
{
    public override string Name => "run_tests";
    public override string Description => "Run test suites";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["pattern"] = new() { Type = "string", Description = "Test pattern", Required = false },
        ["coverage"] = new() { Type = "boolean", Description = "Generate coverage report", Required = false, Default = false }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Tests passed (simulated)"));
}

public class CoverageTool : BaseTool
{
    public override string Name => "coverage";
    public override string Description => "Generate code coverage report";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["format"] = new() { Type = "string", Description = "Report format", Required = false, Default = "html" }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Coverage report generated (simulated)"));
}

// Deployment Tools
public class DeployTool : BaseTool
{
    public override string Name => "deploy";
    public override string Description => "Deploy application";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["target"] = new() { Type = "string", Description = "Deployment target", Required = true },
        ["environment"] = new() { Type = "string", Description = "Environment", Required = false, Default = "production" }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("Deployment complete (simulated)"));
}

public class CICDPipelineTool : BaseTool
{
    public override string Name => "cicd_run";
    public override string Description => "Run CI/CD pipeline";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["pipeline"] = new() { Type = "string", Description = "Pipeline name", Required = true }
    };
    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments) =>
        Task.FromResult(ToolResult.Ok("CI/CD pipeline executed (simulated)"));
}
