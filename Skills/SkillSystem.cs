using AgentFox.Tools;

namespace AgentFox.Skills;

/// <summary>
/// Base class for all skills with dependency support
/// </summary>
public abstract class Skill
{
    public string Name { get; protected set; } = string.Empty;
    public string Description { get; protected set; } = string.Empty;
    public List<string> Dependencies { get; protected set; } = new();
    public string? Version { get; protected set; } = null;
    
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
}

/// <summary>
/// Skill registry for managing available skills with dependency resolution
/// </summary>
public class SkillRegistry
{
    private readonly Dictionary<string, Skill> _skills = new();
    private readonly HashSet<string> _enabledSkills = new();
    private readonly ToolRegistry _toolRegistry;
    private readonly object _lock = new();
    
    /// <summary>
    /// Event hooks for skill lifecycle
    /// </summary>
    public ToolEventHookRegistry HookRegistry { get; } = new();
    
    public SkillRegistry(ToolRegistry toolRegistry)
    {
        _toolRegistry = toolRegistry;
        RegisterBuiltInSkills();
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
    
    public void Register(Skill skill)
    {
        lock (_lock)
        {
            _skills[skill.Name] = skill;
        }
    }
    
    public void Unregister(string name)
    {
        lock (_lock)
        {
            _skills.Remove(name);
            _enabledSkills.Remove(name);
        }
    }
    
    public Skill? Get(string name)
    {
        lock (_lock)
        {
            return _skills.TryGetValue(name, out var skill) ? skill : null;
        }
    }
    
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
    public async Task EnableSkillAsync(string name)
    {
        lock (_lock)
        {
            if (_enabledSkills.Contains(name))
                return;  // Already enabled
        }
        
        var skill = Get(name);
        if (skill == null)
            throw new InvalidOperationException($"Skill '{name}' not found");
        
        // Invoke pre-enable hook
        await HookRegistry.InvokeSkillPreEnableAsync(name);
        
        try
        {
            // Resolve and enable dependencies first
            foreach (var depName in skill.Dependencies)
            {
                if (!IsSkillEnabled(depName))
                {
                    await EnableSkillAsync(depName);  // Recursive dependency resolution
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
            
            // Mark as enabled
            lock (_lock)
            {
                _enabledSkills.Add(name);
            }
            
            // Invoke post-enable hook
            await HookRegistry.InvokeSkillPostEnableAsync(name, tools.Count);
        }
        catch (Exception ex)
        {
            await HookRegistry.InvokeSkillErrorAsync(name, ex.Message);
            throw;
        }
    }
    
    /// <summary>
    /// Disable a skill and optionally its dependents
    /// </summary>
    public async Task DisableSkillAsync(string name, bool disableDependents = false)
    {
        var skill = Get(name);
        if (skill == null)
            return;
        
        // If disableDependents is true, find and disable skills that depend on this one
        if (disableDependents)
        {
            var dependents = _skills.Values
                .Where(s => s.Dependencies.Contains(name))
                .ToList();
            
            foreach (var dependent in dependents)
            {
                await DisableSkillAsync(dependent.Name, true);
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
}

/// <summary>
/// Git operations skill (Composio dev skill)
/// </summary>
public class GitSkill : Skill
{
    public GitSkill()
    {
        Name = "git";
        Description = "Git version control operations - commit, push, pull, branch, merge, etc.";
    }
    
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
}

/// <summary>
/// Docker operations skill
/// </summary>
public class DockerSkill : Skill
{
    public DockerSkill()
    {
        Name = "docker";
        Description = "Docker container operations - build, run, stop, logs, etc.";
    }
    
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
            "You are an expert code reviewer. Analyze code for: bugs, security vulnerabilities, performance issues, code smells, and best practices."
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
}

/// <summary>
/// API integration skill
/// </summary>
public class APIIntegrationSkill : Skill
{
    public APIIntegrationSkill()
    {
        Name = "api_integration";
        Description = "API integration and REST/GraphQL operations";
    }
    
    public override List<ITool> GetTools()
    {
        return new List<ITool>
        {
            new RESTClientTool(),
            new GraphQLTool()
        };
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
    }
    
    public override List<ITool> GetTools()
    {
        return new List<ITool>
        {
            new DBQueryTool(),
            new DBMigrationTool()
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
    }
    
    public override List<ITool> GetTools()
    {
        return new List<ITool>
        {
            new RunTestsTool(),
            new CoverageTool()
        };
    }
}

/// <summary>
/// Deployment skill
/// </summary>
public class DeploymentSkill : Skill
{
    public DeploymentSkill()
    {
        Name = "deployment";
        Description = "Application deployment and CI/CD";
    }
    
    public override List<ITool> GetTools()
    {
        return new List<ITool>
        {
            new DeployTool(),
            new CICDPipelineTool()
        };
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
