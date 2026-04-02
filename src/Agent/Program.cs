using AgentFox.Agents;
using AgentFox.LLM;
using AgentFox.MCP;
using AgentFox.Memory;
using AgentFox.Sessions;
using AgentFox.Skills;
using AgentFox.Tools;
using SystemPromptBuilder = AgentFox.LLM.SystemPromptBuilder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentFox;

/// <summary>
/// Simple console-based logger for Composio initialization
/// </summary>
internal class ConsoleLogger : ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var prefix = logLevel switch
        {
            LogLevel.Error => "[ERROR]",
            LogLevel.Warning => "[WARN]",
            LogLevel.Information => "[INFO]",
            LogLevel.Debug => "[DEBUG]",
            _ => "[LOG]"
        };
        Console.WriteLine($"{prefix} {message}");
        if (exception != null)
            Console.WriteLine($"  Exception: {exception.Message}");
    }
}

internal class ConsoleLogger<T> : ConsoleLogger, ILogger<T> where T : class { }

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
        var sessionConfig = new SessionConfig();
        configuration.GetSection("Sessions").Bind(sessionConfig);
        var sessionManager = new SessionManager(sessionConfig, workspaceManager);
        var toolRegistry = CreateToolRegistry(workspaceManager);
        var skillRegistry = await CreateSkillRegistryAsync(toolRegistry, configuration);
        var mcpClient = await CreateAndInitializeMcpClientAsync(toolRegistry, configuration);

        // Build system prompt with dynamic builder + skills index
        var systemPrompt = new SystemPromptBuilder()
            .WithPersona(SystemPromptConfig.AgentPrompts.DeveloperAssistant)
            .WithTools("shell", "read_file", "write_file", "list_files", "search_files", "make_directory", "delete", "add_memory", "search_memory", "get_all_memories")
            .WithSkillsIndex(skillRegistry.GetSkillManifests())
            .WithConstraints(
                "Always verify changes before executing destructive operations",
                "Prioritize security and best practices",
                "Ask for clarification when requirements are ambiguous",
                "Use add_memory to save important user facts or preferences to long-term memory.",
                "Use search_memory to recall past information or facts when requested.",
                "Use get_all_memories to retrieve everything stored in long-term memory."
            )
            .Build();

        var chatClient = LLMFactory.CreateFromConfiguration(configuration);
        var longTermMemory = MemoryBackendFactory.CreateLongTermStorage(configuration, workspaceManager);
        var memory = new HybridMemory(80, longTermMemory);
        var chatHistoryPath = workspaceManager.ResolvePath("ChatHistory");
        // MarkdownSessionStore: append-only markdown store with full message history (incl. tool calls).
        var sessionStore = new MarkdownSessionStore(chatHistoryPath);

        // Register memory tools into the registry specifically for this agent's memory
        toolRegistry.Register(new AddMemoryTool(memory));
        toolRegistry.Register(new SearchMemoryTool(memory));
        toolRegistry.Register(new GetAllMemoriesTool(memory));


        var agent = new AgentBuilder(toolRegistry)
            .WithName("AgentFox")
            .WithSystemPrompt(systemPrompt)
            .WithMemory(memory)
            .WithSkillsRegistry(skillRegistry)
            .WithMCPClient(mcpClient)
            .WithConversationStore(sessionStore)
            .WithHistoryProvider(sessionStore.HistoryProvider)
            .WithChatClient(chatClient)
            .WithWorkspaceManager(workspaceManager)
            .WithSessionManager(sessionManager)
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
        var sessionConfig = new SessionConfig();
        configuration.GetSection("Sessions").Bind(sessionConfig);
        var sessionManager = new SessionManager(sessionConfig, workspaceManager);
        var toolRegistry = CreateToolRegistry(workspaceManager);
        var skillRegistry = await CreateSkillRegistryAsync(toolRegistry, configuration);
        var mcpClient = await CreateAndInitializeMcpClientAsync(toolRegistry, configuration);

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
                "spawn_subagent: Spawn a sub-agent for complex tasks (waits for result)",
                "spawn_background_subagent: Spawn a background sub-agent that runs in a separate lane and announces results back",
                "load_skill: Load a skill's full guidance on demand",
                "add_memory: Save an important fact to memory",
                "search_memory: Search memory for a fact",
                "get_all_memories: Retrieve all stored long-term memories"
            )
            .WithSkillsIndex(manifests)
            .WithExecutionContext(
                "You are running in interactive mode and can help with:\n" +
                "- Code development and debugging\n" +
                "- File system operations\n" +
                "- System administration\n" +
                "- Architecture and design consultation\n" +
                "- Composio.dev integrations (GitHub, Slack, Jira, etc.)\n" +
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
                "Use search_memory to recall past information or facts when requested.",
                "Use get_all_memories to retrieve everything stored in long-term memory.",
                "For Composio integrations, provide clear examples and documentation on usage"
            )
            .Build();

        var chatClient = LLMFactory.CreateFromConfiguration(configuration);
        var longTermMemory = MemoryBackendFactory.CreateLongTermStorage(configuration, workspaceManager);
        var memory = new HybridMemory(100, longTermMemory);
        var chatHistoryPath = workspaceManager.ResolvePath("ChatHistory");
        // MarkdownSessionStore: append-only markdown store with full message history (incl. tool calls).
        var sessionStore = new MarkdownSessionStore(chatHistoryPath);

        // Register memory tools into the registry specifically for this agent's memory
        toolRegistry.Register(new AddMemoryTool(memory));
        toolRegistry.Register(new SearchMemoryTool(memory));
        toolRegistry.Register(new GetAllMemoriesTool(memory));

        var agent = new AgentBuilder(toolRegistry)
            .WithName("AgentFox")
            .WithSystemPrompt(systemPrompt)
            .WithMemory(memory)
            .WithLogger(new ConsoleLogger<FoxAgent>())
            .WithConversationStore(sessionStore)
            .WithHistoryProvider(sessionStore.HistoryProvider)
            .WithCompactionFromConfig(configuration)
            .WithSkillsRegistry(skillRegistry)
            .WithMCPClient(mcpClient)
            .WithWorkspaceManager(workspaceManager)
            .WithChatClient(chatClient)
            .WithSessionManager(sessionManager)
            .Build();

        // Register spawn sub-agent tool (requires the agent instance)
        toolRegistry.Register(new SpawnSubAgentTool(agent));

        // Set up SubAgentManager for background/long-running sub-agents
        var subAgentConfig = new SubAgentConfiguration
        {
            MaxSpawnDepth = 3,
            MaxConcurrentSubAgents = 10,
            MaxChildrenPerAgent = 5,
            DefaultRunTimeoutSeconds = 300,
            DefaultModel = "gpt-4",
            DefaultThinkingLevel = "high",
            AutoCleanupCompleted = true,
            CleanupDelayMilliseconds = 5000
        };

        // Create command queue and agent runtime for sub-agent management
        var commandQueue = new CommandQueue();
        var agentRuntime = new DefaultAgentRuntime(toolRegistry, null, new ConsoleLogger<DefaultAgentRuntime>());
        var subAgentManager = new SubAgentManager(commandQueue, agentRuntime, subAgentConfig, new ConsoleLogger<SubAgentManager>(), sessionManager);

        // Register callback for background sub-agent result announcements
        subAgentManager.RegisterResultCallback(async (task, result) =>
        {
            Console.WriteLine($"\n[BACKGROUND] Sub-agent '{task.SessionKey}' completed with status: {result.Status}");

            // Create a result announcement command to report back to the main agent
            // For now, just log the result - the full channel announcement system can be enhanced later
            return null; // Returning null since we're not wiring up the full command queue processing yet
        });

        // Register background sub-agent tool
        var consoleSessionId = sessionManager.GetOrCreateConsoleSession(agent.Id);
        toolRegistry.Register(new SpawnBackgroundSubAgentTool(
            subAgentManager,
            parentAgentId: agent.Id,
            parentSessionKey: consoleSessionId,
            parentSpawnDepth: 0,
            logger: new ConsoleLogger<SpawnBackgroundSubAgentTool>()
        ));

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

    private sealed class McpServerConfig
    {
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public int TimeoutSeconds { get; set; } = 30;
        public Dictionary<string, string> Headers { get; set; } = new();
    }

    static async Task<MCPClient> CreateAndInitializeMcpClientAsync(ToolRegistry toolRegistry, IConfiguration configuration)
    {
        var mcpClient = new MCPClient(toolRegistry);

        var servers = configuration
            .GetSection("MCP:Servers")
            .Get<List<McpServerConfig>>() ?? new List<McpServerConfig>();

        foreach (var serverConfig in servers.Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Url)))
        {
            try
            {
                var success = await mcpClient.AddServerAsync(
                    serverConfig.Name,
                    serverConfig.Url,
                    serverConfig.TimeoutSeconds <= 0 ? 30 : serverConfig.TimeoutSeconds,
                    serverConfig.Headers);
                if (!success)
                {
                    Console.WriteLine($"⚠ Warning: Failed to connect to MCP server '{serverConfig.Name}' at '{serverConfig.Url}'.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Warning: Exception while initializing MCP server '{serverConfig.Name}': {ex.Message}");
            }
        }

        if (servers.Count > 0)
        {
            Console.WriteLine($"MCP: Loaded configuration for {servers.Count} server(s).");
        }

        return mcpClient;
    }

    static async Task<SkillRegistry> CreateSkillRegistryAsync(ToolRegistry toolRegistry, IConfiguration configuration)
    {
        // SkillRegistry auto-registers all built-in skills (git, docker, code_review,
        // debugging, api_integration, database, testing, deployment) and also
        // registers LoadSkillTool into the toolRegistry for lazy on-demand loading.
        var skillRegistry = new SkillRegistry(toolRegistry);

        // Initialize Composio skills if API key is configured
        var composioApiKey = configuration["Composio:ApiKey"];
        if (!string.IsNullOrEmpty(composioApiKey) && !composioApiKey.Contains("your-composio"))
        {
            try
            {
                var logger = new ConsoleLogger<ComposioSkillProvider>();
                var composioProvider = new ComposioSkillProvider(
                    apiKey: composioApiKey,
                    skillRegistry: skillRegistry,
                    logger: logger
                );

                // Get toolkit filter if specified
                var toolkits = configuration.GetSection("Composio:Toolkits")
                    .Get<List<string>>() ?? new();

                if (toolkits.Any())
                {
                    await composioProvider.InitializeAsync(filterToolkitIds: toolkits.ToArray());
                }
                else
                {
                    await composioProvider.InitializeAsync();
                }

                Console.WriteLine("✓ Composio skills initialized successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Warning: Failed to initialize Composio skills: {ex.Message}");
                // Continue without Composio rather than failing startup
            }
        }

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
  - Use Composio.dev integrations: GitHub, Slack, Jira, Asana, etc.
    (e.g., 'load the github skill and help me create a repository')

Composio Integration:
  If COMPOSIO_API_KEY is configured, the agent has access to:
  - GitHub (create repos, manage issues, view code)
  - Slack (send messages, create channels)
  - Jira (manage tasks, create issues)
  - And many more integrations...
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
