using AgentFox.Agents;
using AgentFox.LLM;
using AgentFox.MCP;
using AgentFox.Memory;
using AgentFox.Sessions;
using AgentFox.Skills;
using AgentFox.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SystemPromptBuilder = AgentFox.LLM.SystemPromptBuilder;

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

        var configuration = BuildConfiguration();
        var serviceProvider = await BuildServiceProviderAsync(configuration);

        if (args.Length > 0)
            return await RunCommandLineMode(args, serviceProvider);

        return await RunInteractiveMode(serviceProvider);
    }

    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------

    static IConfiguration BuildConfiguration()
    {
        var envName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{envName}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();
    }

    // -------------------------------------------------------------------------
    // DI container
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds and returns the application's service provider.
    /// Services that require async initialization are pre-created and registered as instances.
    /// </summary>
    static async Task<IServiceProvider> BuildServiceProviderAsync(IConfiguration configuration)
    {
        var services = new ServiceCollection();

        // Logging — open-generic registration covers ILogger<T> for any T
        services.AddSingleton(typeof(ILogger<>), typeof(ConsoleLogger<>));

        // Configuration
        services.AddSingleton(configuration);

        // ── Pre-create services with async / ordering dependencies ──────────
        // ToolRegistry must exist before SkillRegistry and MCPClient (async init).
        var workspaceManager = new WorkspaceManager(configuration);
        var toolRegistry = CreateToolRegistry(workspaceManager);

        var skillRegistry = await CreateSkillRegistryAsync(toolRegistry, configuration);
        var mcpClient = await CreateAndInitializeMcpClientAsync(toolRegistry, configuration);

        var longTermMemory = MemoryBackendFactory.CreateLongTermStorage(configuration, workspaceManager);
        var memory = new HybridMemory(100, longTermMemory);

        toolRegistry.Register(new AddMemoryTool(memory));
        toolRegistry.Register(new SearchMemoryTool(memory));
        toolRegistry.Register(new GetAllMemoriesTool(memory));

        // Register pre-created instances
        services.AddSingleton(workspaceManager);
        services.AddSingleton(toolRegistry);
        services.AddSingleton(skillRegistry);
        services.AddSingleton(mcpClient);
        services.AddSingleton(memory);

        // ── Services resolved lazily by the container ────────────────────────
        services.AddSingleton(sp =>
        {
            var cfg = new SessionConfig();
            sp.GetRequiredService<IConfiguration>().GetSection("Sessions").Bind(cfg);
            return cfg;
        });

        services.AddSingleton(sp => new SessionManager(
            sp.GetRequiredService<SessionConfig>(),
            sp.GetRequiredService<WorkspaceManager>()));

        services.AddSingleton(_ => LLMFactory.CreateFromConfiguration(configuration));

        services.AddSingleton(sp =>
            new MarkdownSessionStore(sp.GetRequiredService<SessionManager>().SessionDirectory));

        // ── Sub-agent infrastructure ─────────────────────────────────────────
        services.AddSingleton<ICommandQueue, CommandQueue>();

        services.AddSingleton(_ => new SubAgentConfiguration
        {
            MaxSpawnDepth = 3,
            MaxConcurrentSubAgents = 10,
            MaxChildrenPerAgent = 5,
            DefaultRunTimeoutSeconds = 300,
            DefaultModel = "gpt-4",
            DefaultThinkingLevel = "high",
            AutoCleanupCompleted = true,
            CleanupDelayMilliseconds = 5000
        });

        services.AddSingleton<IAgentRuntime>(sp => new DefaultAgentRuntime(
            sp.GetRequiredService<ToolRegistry>(),
            executor: null,
            sp.GetRequiredService<ILogger<DefaultAgentRuntime>>()));

        services.AddSingleton(sp => new SubAgentManager(
            sp.GetRequiredService<ICommandQueue>(),
            sp.GetRequiredService<IAgentRuntime>(),
            sp.GetRequiredService<SubAgentConfiguration>(),
            sp.GetRequiredService<ILogger<SubAgentManager>>(),
            sp.GetRequiredService<SessionManager>()));

        services.AddSingleton(sp => new CommandProcessor(
            sp.GetRequiredService<ICommandQueue>(),
            CommandProcessorConfig.FromSubAgentConfig(sp.GetRequiredService<SubAgentConfiguration>()),
            sp.GetRequiredService<ILogger<CommandProcessor>>()));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds a FoxAgent from the DI container and a pre-built system prompt.
    /// </summary>
    static FoxAgent BuildAgent(
        IServiceProvider sp,
        string systemPrompt,
        bool withLogger = false) =>
        BuildAgentWithClient(sp, systemPrompt, sp.GetRequiredService<IChatClient>(), withLogger);

    /// <summary>
    /// Builds a FoxAgent with a specific <see cref="IChatClient"/> (used for model overrides in sub-agents).
    /// </summary>
    static FoxAgent BuildAgentWithClient(
        IServiceProvider sp,
        string systemPrompt,
        IChatClient chatClient,
        bool withLogger = false)
    {
        var builder = new AgentBuilder(sp.GetRequiredService<ToolRegistry>())
            .WithName("AgentFox")
            .WithSystemPrompt(systemPrompt)
            .WithMemory(sp.GetRequiredService<HybridMemory>())
            .WithSkillsRegistry(sp.GetRequiredService<SkillRegistry>())
            .WithMCPClient(sp.GetRequiredService<MCPClient>())
            .WithConversationStore(sp.GetRequiredService<MarkdownSessionStore>())
            .WithHistoryProvider(sp.GetRequiredService<MarkdownSessionStore>().HistoryProvider)
            .WithChatClient(chatClient)
            .WithWorkspaceManager(sp.GetRequiredService<WorkspaceManager>())
            .WithSessionManager(sp.GetRequiredService<SessionManager>())
            .WithCompactionFromConfig(sp.GetRequiredService<IConfiguration>());

        if (withLogger)
            builder = builder.WithLogger(sp.GetRequiredService<ILogger<FoxAgent>>());

        return builder.Build();
    }

    // -------------------------------------------------------------------------
    // Execution modes
    // -------------------------------------------------------------------------

    static async Task<int> RunCommandLineMode(string[] args, IServiceProvider sp)
    {
        var skillRegistry = sp.GetRequiredService<SkillRegistry>();

        var systemPrompt = new SystemPromptBuilder()
            .WithPersona(SystemPromptConfig.AgentPrompts.DeveloperAssistant)
            .WithTools("shell", "read_file", "write_file", "list_files", "search_files",
                       "make_directory", "delete", "add_memory", "search_memory", "get_all_memories")
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

        var agent = BuildAgent(sp, systemPrompt);

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

    static async Task<int> RunInteractiveMode(IServiceProvider sp)
    {
        var skillRegistry = sp.GetRequiredService<SkillRegistry>();
        var toolRegistry = sp.GetRequiredService<ToolRegistry>();
        var sessionManager = sp.GetRequiredService<SessionManager>();
        var subAgentManager = sp.GetRequiredService<SubAgentManager>();
        var commandProcessor = sp.GetRequiredService<CommandProcessor>();

        var manifests = skillRegistry.GetSkillManifests();
        Console.WriteLine($"Skills loaded: {manifests.Count} skill(s) registered.");
        Console.WriteLine();

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

        var agent = BuildAgent(sp, systemPrompt, withLogger: true);

        // Upgrade runtime to use the real LLM-backed executor now that FoxAgent exists.
        // Sub-agents with an AgentCommand.Model override get a freshly built FoxAgent
        // wired to the resolved IChatClient; all other settings are shared.
        var agentRuntime = sp.GetRequiredService<IAgentRuntime>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        agentRuntime.SetExecutor(new FoxAgentExecutor(
            defaultAgent: agent,
            agentFactory: client => BuildAgentWithClient(sp, systemPrompt, client),
            modelResolver: model => LLMFactory.CreateWithModelOverride(configuration, model),
            logger: sp.GetRequiredService<ILogger<FoxAgentExecutor>>()
        ));

        // Agent-dependent tools registered after agent creation (circular dependency)
        toolRegistry.Register(new SpawnSubAgentTool(agent));

        var consoleSessionId = sessionManager.GetOrCreateConsoleSession(agent.Id);
        toolRegistry.Register(new SpawnBackgroundSubAgentTool(
            subAgentManager,
            parentAgentId: agent.Id,
            parentSessionKey: consoleSessionId,
            parentSpawnDepth: 0,
            logger: sp.GetRequiredService<ILogger<SpawnBackgroundSubAgentTool>>()
        ));

        // --- Subagent lane handler ---
        // Delegates to agentRuntime which uses FoxAgentExecutor for real LLM execution.
        // Model overrides in AgentCommand.Model are resolved automatically.
        commandProcessor.RegisterLaneHandler(CommandLane.Subagent, async (command, ct) =>
        {
            if (command is not AgentCommand agentCmd) return;

            var runId = agentCmd.RunId;
            subAgentManager.OnSubAgentStarted(runId);

            try
            {
                var subResult = await agentRuntime.ExecuteAsync(agentCmd, ct);

                var completion = subResult.Success
                    ? SubAgentCompletionResult.Success(subResult.Output)
                    : SubAgentCompletionResult.Failure(subResult.Error ?? "Sub-agent returned no output");

                subAgentManager.OnSubAgentCompleted(runId, completion);
            }
            catch (OperationCanceledException)
            {
                subAgentManager.OnSubAgentCompleted(runId, SubAgentCompletionResult.Cancelled());
            }
            catch (Exception ex)
            {
                subAgentManager.OnSubAgentCompleted(runId, SubAgentCompletionResult.Failure(ex.Message));
            }
        });

        // --- Main lane handler ---
        commandProcessor.RegisterLaneHandler(CommandLane.Main, async (command, ct) =>
        {
            if (command is not ResultAnnouncementCommand announcement) return;

            if (announcement.RequesterChannel != null && !announcement.SuppressChannelNotification)
            {
                try
                {
                    await announcement.RequesterChannel.SendMessageAsync(announcement.FormatMessage());
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to send result to channel: {ex.Message}");
                }
                return;
            }

            if (!string.IsNullOrEmpty(announcement.ParentSessionKey))
            {
                var notification = $"[Background sub-agent result]\n{announcement.FormatMessage()}";

                Console.WriteLine();
                Console.WriteLine($"[SUB-AGENT] Reporting result to parent agent (session: {announcement.ParentSessionKey})...");

                try
                {
                    var parentResponse = await agent.ProcessAsync(notification, announcement.ParentSessionKey, ct);
                    Console.WriteLine();
                    Console.WriteLine($"[AGENT] {parentResponse.Output}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to deliver sub-agent result to parent agent: {ex.Message}");
                }

                Console.Write("\n> ");
            }
        });

        // --- Result callback ---
        subAgentManager.RegisterResultCallback(async (task, result) =>
        {
            Console.WriteLine($"\n[BACKGROUND] Sub-agent '{task.SessionKey}' finished — status: {result.Status}");

            if (task.OriginatingChannel != null)
            {
                return ResultAnnouncementCommand.CreateChannelAnnouncement(
                    result,
                    task.OriginatingChannel,
                    task.OriginatingMessageId ?? string.Empty,
                    task.CorrelationId,
                    task.OriginatingChannelId ?? string.Empty);
            }

            if (!string.IsNullOrEmpty(task.ParentSessionKey))
            {
                return ResultAnnouncementCommand.CreateParentAgentAnnouncement(
                    result,
                    task.CorrelationId,
                    task.ParentSessionKey,
                    task.SessionKey);
            }

            return ResultAnnouncementCommand.CreateLocalAnnouncement(
                result, task.CorrelationId, task.SessionKey);
        });

        commandProcessor.Start();

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
                await commandProcessor.StopAsync(TimeSpan.FromSeconds(10));
                break;
            }

            Console.WriteLine();
            var result = await agent.ExecuteAsync(trimmed);

            Console.WriteLine(result.Output);
            Console.WriteLine();

            if (result.SpawnedSubAgents.Count > 0)
                Console.WriteLine($"Spawned {result.SpawnedSubAgents.Count} sub-agent(s)");
        }

        return 0;
    }

    // -------------------------------------------------------------------------
    // Factory helpers
    // -------------------------------------------------------------------------

    static ToolRegistry CreateToolRegistry(WorkspaceManager workspaceManager)
    {
        var registry = new ToolRegistry();

        registry.Register(new ShellCommandTool(workspaceManager));
        registry.Register(new ReadFileTool(workspaceManager));
        registry.Register(new WriteFileTool(workspaceManager));
        registry.Register(new ListFilesTool(workspaceManager));
        registry.Register(new SearchFilesTool(workspaceManager));
        registry.Register(new MakeDirectoryTool(workspaceManager));
        registry.Register(new DeleteTool(workspaceManager));
        registry.Register(new GetEnvironmentInfoTool());
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
                    Console.WriteLine($"⚠ Warning: Failed to connect to MCP server '{serverConfig.Name}' at '{serverConfig.Url}'.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Warning: Exception while initializing MCP server '{serverConfig.Name}': {ex.Message}");
            }
        }

        if (servers.Count > 0)
            Console.WriteLine($"MCP: Loaded configuration for {servers.Count} server(s).");

        return mcpClient;
    }

    static async Task<SkillRegistry> CreateSkillRegistryAsync(ToolRegistry toolRegistry, IConfiguration configuration)
    {
        var skillRegistry = new SkillRegistry(toolRegistry);

        var composioApiKey = configuration["Composio:ApiKey"];
        if (!string.IsNullOrEmpty(composioApiKey) && !composioApiKey.Contains("your-composio"))
        {
            try
            {
                var composioProvider = new ComposioSkillProvider(
                    apiKey: composioApiKey,
                    skillRegistry: skillRegistry,
                    logger: new ConsoleLogger<ComposioSkillProvider>()
                );

                var toolkits = configuration.GetSection("Composio:Toolkits").Get<List<string>>() ?? [];

                if (toolkits.Any())
                    await composioProvider.InitializeAsync(filterToolkitIds: toolkits.ToArray());
                else
                    await composioProvider.InitializeAsync();

                Console.WriteLine("✓ Composio skills initialized successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ Warning: Failed to initialize Composio skills: {ex.Message}");
            }
        }

        return skillRegistry;
    }

    // -------------------------------------------------------------------------
    // Console display helpers
    // -------------------------------------------------------------------------

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

    static void ShowSkillDetail(SkillRegistry skillRegistry, string skillName)
    {
        var skill = skillRegistry.Get(skillName)
            ?? skillRegistry.GetAll()
                .FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));

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
                Console.WriteLine($"    • {tool.Name,-25} {tool.Description}");
        }

        Console.WriteLine();
        Console.WriteLine($"  Type: {(skill is ISkillPlugin ? "local" : "generic")} skill");
        Console.WriteLine();
        Console.WriteLine($"  To load full guidance: load_skill(skill_name: \"{skill.Name}\")");
        Console.WriteLine();
    }
}
