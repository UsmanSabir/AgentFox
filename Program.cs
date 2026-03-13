using AgentFox.Agents;
using AgentFox.LLM;
using AgentFox.Memory;
using AgentFox.Models;
using AgentFox.Skills;
using AgentFox.Tools;
using SystemPromptBuilder = AgentFox.LLM.SystemPromptBuilder;
using Microsoft.Extensions.Configuration;

namespace AgentFox;

/// <summary>
/// AgentFox - Multi-agent framework in C#
/// A multi-agent framework with sub-agents, memory, MCP, skills, and channel integrations
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                  AgentFox - AI Agent Framework             ║");
        Console.WriteLine("║         Multi-agent system with memory & channels          ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        
        var envName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"; // Default to Production if not set

        // Build configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{envName}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        // Check for command line mode
        if (args.Length > 0)
        {
            return await RunCommandLineMode(args, configuration);
        }
        
        // Interactive mode
        return await RunInteractiveMode(configuration);
    }
    
    static async Task<int> RunCommandLineMode(string[] args, IConfiguration configuration)
    {
        var workspaceManager = new WorkspaceManager(configuration);
        var toolRegistry = CreateToolRegistry(workspaceManager);
        var skillRegistry = CreateSkillRegistry(toolRegistry);
        
        // Build system prompt with dynamic builder + skills index
        var systemPrompt = new SystemPromptBuilder()
            .WithPersona(SystemPromptConfig.AgentPrompts.DeveloperAssistant)
            .WithTools("shell", "read_file", "write_file", "list_files", "search_files", "make_directory", "delete", "add_memory", "search_memory")
            .WithSkillsIndex(skillRegistry.GetSkillManifests())
            .WithConstraints(
                "Always verify changes before executing destructive operations",
                "Prioritize security and best practices",
                "Ask for clarification when requirements are ambiguous",
                "Use add_memory to save important user facts or preferences to long-term memory.",
                "Use search_memory to recall past information or facts when requested."
            )
            .Build();
        
        var llmProvider = LLMFactory.CreateFromConfiguration(configuration);
        var memory = new HybridMemory(100, "memory.json");
        
        // Register memory tools into the registry specifically for this agent's memory
        toolRegistry.Register(new AddMemoryTool(memory));
        toolRegistry.Register(new SearchMemoryTool(memory));
        
        var agent = new AgentBuilder(toolRegistry)
            .WithName("AgentFox")
            .WithSystemPrompt(systemPrompt)
            .WithMemory(memory)
            .WithLLMProvider(llmProvider)
            .Build();
        
        var task = string.Join(" ", args);
        Console.WriteLine($"Executing: {task}");
        Console.WriteLine(new string('-', 50));
        
        var result = await agent.ExecuteAsync(task);
        
        Console.WriteLine("Result:");
        Console.WriteLine(result.Output);
        
        if (!string.IsNullOrEmpty(result.Error))
        {
            Console.WriteLine($"Error: {result.Error}");
            return 1;
        }
        
        return result.Success ? 0 : 1;
    }
    
    static async Task<int> RunInteractiveMode(IConfiguration configuration)
    {
        var workspaceManager = new WorkspaceManager(configuration);
        var toolRegistry = CreateToolRegistry(workspaceManager);
        var skillRegistry = CreateSkillRegistry(toolRegistry);
        
        // Print skills summary at startup
        var manifests = skillRegistry.GetSkillManifests();
        Console.WriteLine($"Skills loaded: {manifests.Count} skill(s) registered.");
        Console.WriteLine();
        
        // Build comprehensive system prompt with all available tools and a compact skills index
        var systemPrompt = new SystemPromptBuilder()
            .WithPersona(SystemPromptConfig.AgentPrompts.DeveloperAssistant)
            .WithTools(
                "shell: Execute shell commands",
                "read_file: Read file contents",
                "write_file: Write content to files",
                "list_files: List files in a directory",
                "search_files: Search for text in files",
                "make_directory: Create directories",
                "delete: Delete files or directories",
                "get_env_info: Get environment information",
                "spawn_subagent: Spawn a sub-agent for complex tasks",
                "load_skill: Load a skill's full guidance on demand",
                "add_memory: Save an important fact to memory",
                "search_memory: Search memory for a fact"
            )
            .WithSkillsIndex(manifests)
            .WithExecutionContext(
                "You are running in interactive mode and can help with:\n" +
                "- Code development and debugging\n" +
                "- File system operations\n" +
                "- System administration\n" +
                "- Architecture and design consultation\n" +
                "- Git, Docker, deployment, testing, and more via skills"
            )
            .WithConstraints(
                "Always verify changes before executing destructive operations",
                "Protect sensitive information (API keys, credentials, etc.)",
                "Test code in isolated environments when possible",
                "Explain your reasoning and approach clearly",
                "Ask for confirmation for high-risk operations",
                "Before using a skill's tools, always call load_skill to load the skill's guidance",
                "Use add_memory to save important user facts or preferences to long-term memory.",
                "Use search_memory to recall past information or facts when requested."
            )
            .Build();
        
        var llmProvider = LLMFactory.CreateFromConfiguration(configuration);
        var memory = new HybridMemory(100, "memory.json");
        
        // Register memory tools into the registry specifically for this agent's memory
        toolRegistry.Register(new AddMemoryTool(memory));
        toolRegistry.Register(new SearchMemoryTool(memory));
        
        var agent = new AgentBuilder(toolRegistry)
            .WithName("AgentFox")
            .WithSystemPrompt(systemPrompt)
            .WithMemory(memory)
            .WithLLMProvider(llmProvider)
            .Build();
        
        Console.WriteLine("Type 'help' for available commands, 'exit' to quit.");
        Console.WriteLine();
        
        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            
            if (string.IsNullOrWhiteSpace(input))
                continue;
            
            var trimmed = input.Trim();
            var lower = trimmed.ToLower();
            
            if (lower == "exit")
            {
                Console.WriteLine("Goodbye!");
                break;
            }
            
            if (lower == "help")
            {
                ShowHelp();
                continue;
            }
            
            if (lower == "status")
            {
                ShowStatus(agent);
                continue;
            }
            
            if (lower == "history")
            {
                ShowHistory(agent);
                continue;
            }
            
            if (lower == "clear")
            {
                agent.ClearHistory();
                Console.WriteLine("History cleared.");
                continue;
            }
            
            if (lower == "memory")
            {
                await ShowMemory(agent);
                continue;
            }
            
            if (lower == "tools")
            {
                ShowTools(toolRegistry);
                continue;
            }
            
            // skills — list all registered skills
            if (lower == "skills")
            {
                ShowSkills(skillRegistry);
                continue;
            }
            
            // skill <name> — show detail for a specific skill
            if (lower.StartsWith("skill "))
            {
                var skillName = trimmed.Substring("skill ".Length).Trim();
                ShowSkillDetail(skillRegistry, skillName);
                continue;
            }
            
            // Execute task
            Console.WriteLine();
            var result = await agent.ExecuteAsync(trimmed);
            
            Console.WriteLine(result.Output);
            Console.WriteLine();
            
            if (result.SpawnedSubAgents.Count > 0)
            {
                Console.WriteLine($"Spawned {result.SpawnedSubAgents.Count} sub-agent(s)");
            }
        }
        
        return 0;
    }
    
    static ToolRegistry CreateToolRegistry(WorkspaceManager workspaceManager)
    {
        var registry = new ToolRegistry();
        
        // Register built-in tools
        registry.Register(new ShellCommandTool(workspaceManager));
        registry.Register(new ReadFileTool(workspaceManager));
        registry.Register(new WriteFileTool(workspaceManager));
        registry.Register(new ListFilesTool(workspaceManager));
        registry.Register(new SearchFilesTool(workspaceManager));
        registry.Register(new MakeDirectoryTool(workspaceManager));
        registry.Register(new DeleteTool(workspaceManager));
        registry.Register(new GetEnvironmentInfoTool());
        
        // Register custom tools
        registry.Register(new WebSearchTool());
        registry.Register(new FetchUrlTool());
        registry.Register(new CalculatorTool());
        registry.Register(new UuidTool());
        registry.Register(new TimestampTool());
        
        return registry;
    }

    static SkillRegistry CreateSkillRegistry(ToolRegistry toolRegistry)
    {
        // SkillRegistry auto-registers all built-in skills (git, docker, code_review,
        // debugging, api_integration, database, testing, deployment) and also
        // registers LoadSkillTool into the toolRegistry for lazy on-demand loading.
        var skillRegistry = new SkillRegistry(toolRegistry);
        return skillRegistry;
    }
    
    static void ShowHelp()
    {
        Console.WriteLine(@"
Available commands:
  help           - Show this help message
  status         - Show agent status
  history        - Show conversation history
  memory         - Show agent memory
  tools          - List available tools
  skills         - List all registered skills (name, type, tool count, description)
  skill <name>   - Show detailed info for a specific skill
  clear          - Clear conversation history
  exit           - Exit the program

You can also ask the agent to:
  - Execute shell commands
  - Read/write files
  - Search for text in files
  - Spawn sub-agents for complex tasks
  - Remember information for later
  - Use skills: git, docker, deployment, testing, api_integration, etc.
    (ask the agent to 'load the git skill and help me commit my changes')
");
    }
    
    static void ShowStatus(FoxAgent agent)
    {
        var info = agent.GetInfo();
        Console.WriteLine($"""
            Agent Status:
            ─────────────
            Name:        {info.Name}
            ID:          {info.Id}
            Status:      {info.Status}
            Messages:    {info.MessageCount}
            Sub-agents:  {info.SubAgentCount}
            Tools:       {info.ToolCount}
            Memory:      {(info.HasMemory ? "Enabled" : "Disabled")}
            Created:     {info.CreatedAt:yyyy-MM-dd HH:mm:ss}
            Last Active: {info.LastActiveAt:yyyy-MM-dd HH:mm:ss}
            """);
    }
    
    static void ShowHistory(FoxAgent agent)
    {
        var history = agent.GetHistory();
        Console.WriteLine($"Conversation History ({history.Count} messages):");
        Console.WriteLine(new string('-', 50));
        
        foreach (var msg in history)
        {
            Console.WriteLine($"[{msg.Timestamp:HH:mm:ss}] {msg.Role}: {msg.Content}");
            Console.WriteLine();
        }
    }
    
    static async Task ShowMemory(FoxAgent agent)
    {
        if (agent.Memory == null)
        {
            Console.WriteLine("No memory configured.");
            return;
        }
        
        var memories = await agent.Memory.GetRecentAsync(10);
        Console.WriteLine($"Agent Memory ({memories.Count} recent entries):");
        Console.WriteLine(new string('-', 50));
        
        foreach (var mem in memories)
        {
            Console.WriteLine($"[{mem.Timestamp:HH:mm:ss}] {mem.Type}: {mem.Content}");
        }
    }
    
    static void ShowTools(ToolRegistry registry)
    {
        var tools = registry.GetAll();
        Console.WriteLine($"Available Tools ({tools.Count}):");
        Console.WriteLine(new string('-', 50));
        
        foreach (var tool in tools)
        {
            Console.WriteLine($"  {tool.Name}");
            Console.WriteLine($"    {tool.Description}");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Show all registered skills in a compact table
    /// </summary>
    static void ShowSkills(SkillRegistry skillRegistry)
    {
        var manifests = skillRegistry.GetSkillManifests();
        
        if (manifests.Count == 0)
        {
            Console.WriteLine("No skills registered.");
            return;
        }

        Console.WriteLine($"Registered Skills ({manifests.Count}):");
        Console.WriteLine(new string('─', 80));
        Console.WriteLine($"  {"Skill",-20} {"Type",-10} {"Tools",5}  Description");
        Console.WriteLine(new string('─', 80));

        foreach (var m in manifests)
        {
            var desc = m.Description.Length > 44 ? m.Description[..41] + "..." : m.Description;
            Console.WriteLine($"  {m.Name,-20} {m.SkillType,-10} {m.ToolCount,5}  {desc}");
        }

        Console.WriteLine(new string('─', 80));
        Console.WriteLine();
        Console.WriteLine("Use 'skill <name>' for detailed skill info.");
        Console.WriteLine("Ask the agent: \"load the <skill> skill\" to activate it during a conversation.");
        Console.WriteLine();
    }

    /// <summary>
    /// Show detailed info for a specific skill
    /// </summary>
    static void ShowSkillDetail(SkillRegistry skillRegistry, string skillName)
    {
        var skill = skillRegistry.Get(skillName);
        if (skill == null)
        {
            // Try case-insensitive
            skill = skillRegistry.GetAll()
                .FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
        }

        if (skill == null)
        {
            Console.WriteLine($"Skill '{skillName}' not found.");
            Console.WriteLine("Use 'skills' to list all registered skills.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  Skill: {skill.Name}  (v{skill.Version})");
        Console.WriteLine(new string('─', 60));
        Console.WriteLine($"  Description : {skill.Description}");

        if (skill.Dependencies.Count > 0)
            Console.WriteLine($"  Dependencies: {string.Join(", ", skill.Dependencies)}");

        if (skill.Metadata != null)
        {
            if (skill.Metadata.Capabilities.Count > 0)
                Console.WriteLine($"  Capabilities: {string.Join(", ", skill.Metadata.Capabilities)}");
            if (skill.Metadata.Tags.Count > 0)
                Console.WriteLine($"  Tags        : {string.Join(", ", skill.Metadata.Tags)}");
            Console.WriteLine($"  Complexity  : {skill.Metadata.ComplexityScore}/10");
        }

        var tools = skill.GetTools();
        if (tools.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Tools ({tools.Count}):");
            foreach (var tool in tools)
            {
                Console.WriteLine($"    • {tool.Name,-25} {tool.Description}");
            }
        }

        var isPlugin = skill is ISkillPlugin;
        Console.WriteLine();
        Console.WriteLine($"  Type: {(isPlugin ? "local" : "generic")} skill");
        Console.WriteLine();
        Console.WriteLine($"  To load full guidance: load_skill(skill_name: \"{skill.Name}\")");
        Console.WriteLine();
    }
}
