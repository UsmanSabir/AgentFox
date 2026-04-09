using AgentFox.Agents;
using AgentFox.Doctor;
using AgentFox.Doctor.Checks;
using AgentFox.Doctor.Onboarding;
using AgentFox.LLM;
using AgentFox.MCP;
using AgentFox.Memory;
using AgentFox.Models;
using AgentFox.Modules.Loaders;
using AgentFox.Plugins.Interfaces;
using AgentFox.Runtime.Services;
using AgentFox.Sessions;
using AgentFox.Skills;
using AgentFox.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Text;
using AgentFox.Helpers;
using SystemPromptBuilder = AgentFox.LLM.SystemPromptBuilder;

namespace AgentFox;

/// <summary>
/// AgentFox - Multi-agent framework in C#
/// A multi-agent framework with sub-agents, memory, MCP, skills, and channel integrations
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        if (!Console.IsInputRedirected)
            Console.OutputEncoding = Encoding.UTF8;
        
        // ── Service mode detection ────────────────────────────────────────────
        // Check if running in service mode before showing banner
        bool isServiceMode = ServiceHostMode.DetectServiceMode(args);
        
        // Show banner only if not in service mode
        if (!isServiceMode)
            ShowBanner();

        bool runDoctor    = args.Contains("--doctor");
        bool doctorFix    = args.Contains("--fix");
        bool runOnboarding = args.Contains("--onboarding");

        // Extract service management commands
        string? serviceCommand = args.FirstOrDefault(a => ServiceCommandHandler.IsServiceCommand(a));

        var taskArgs = args.Where(a => !a.StartsWith("--") && !ServiceCommandHandler.IsServiceCommand(a)).ToArray();

        // "agentfox onboarding ..." (positional) is also accepted
        if (!runOnboarding
            && taskArgs.Length > 0
            && taskArgs[0].Equals("onboarding", StringComparison.OrdinalIgnoreCase))
        {
            runOnboarding = true;
            taskArgs = taskArgs.Skip(1).ToArray();
        }

        // ── Web application builder (single DI container for the whole process) ─
        var builder       = WebApplication.CreateBuilder(args);
        var configuration = builder.Configuration;

        // ── Logging setup ─────────────────────────────────────────────────────
        // WebApplicationBuilder registers Console/Debug providers by default.
        // We replace the whole pipeline so neither framework internals nor our
        // own code emit anything through ILoggerFactory to the console.
        var loggingCfg = new LoggingConfig();
        configuration.GetSection("Logging").Bind(loggingCfg);

        builder.Logging.ClearProviders();   // kill Console, Debug, EventSource, etc.

        if (loggingCfg.UseFileLogger)
        {
            FileLogger.Configure(loggingCfg.FilePath, loggingCfg.MinLevel);
            FileLogger.DeleteOldLogs(loggingCfg.FilePath, loggingCfg.RetentionDays);

            // Route everything (ILogger<T> via DI + ILoggerFactory) to the file logger.
            builder.Logging.AddProvider(new FileLoggerProvider());
            builder.Services.AddSingleton(typeof(ILogger<>), typeof(FileLogger<>));
        }
        else
        {
            // Custom Spectre-based coloured console — explicit, not ASP.NET's default.
            builder.Logging.AddProvider(new ConsoleLoggerProvider());
            builder.Services.AddSingleton(typeof(ILogger<>), typeof(ConsoleLogger<>));
        }

        // Suppress noisy ASP.NET Core / Microsoft framework namespaces so only
        // Warning+ entries make it through unless the file logger's MinLevel is lower.
        builder.Logging.AddFilter("Microsoft",       LogLevel.Warning);
        builder.Logging.AddFilter("System",          LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

        // ── Handle service commands (--install-service, --uninstall-service, etc.) ─
        if (!string.IsNullOrEmpty(serviceCommand))
        {
            var tempLogger = new ConsoleLogger<Program>();
            var handler = ServiceCommandHandler.CreateFromConfiguration(configuration, tempLogger);
            var result = await handler.ProcessCommandAsync(serviceCommand);
            AnsiConsole.WriteLine(result.ToString());
            return result.Success ? 0 : 1;
        }

        // ── Onboarding wizard (--onboarding  or  agentfox onboarding ...) ────
        if (runOnboarding)
        {
            var appCfgPath = ResolveAppSettingsPath();
            var wizard     = new OnboardingWizard(appCfgPath);

            // Command mode: any LLM named args present alongside --onboarding
            bool commandMode = args.Any(a =>
                a.Equals("--provider",  StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--model",     StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--apikey",    StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--api-key",   StringComparison.OrdinalIgnoreCase));

            if (commandMode)
                await wizard.RunCommandModeAsync(args);
            else
                await wizard.RunInteractiveModeAsync();

            return 0;
        }

        // ── Pre-build async services ──────────────────────────────────────────
        // These need async init (Composio, MCP) so they are created before the host
        // and then registered as already-constructed singletons.
        var workspaceManager = new WorkspaceManager(configuration);
        var toolRegistry     = CreateToolRegistry(workspaceManager);
        SkillRegistry? skillRegistry = null;
        MCPClient?     mcpClient     = null;
        HybridMemory?  memory        = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots12)
            .SpinnerStyle(Style.Parse("dodgerblue1 bold"))
            .StartAsync("[bold]Initializing AgentFox[/] [dim]— loading tools, memory & integrations...[/]",
                async ctx =>
                {
                    ctx.Status("[dodgerblue1]Registering tools & workspace...[/]");
                    skillRegistry = await CreateSkillRegistryAsync(toolRegistry, configuration);
                    mcpClient     = await CreateAndInitializeMcpClientAsync(toolRegistry, configuration);

                    var longTermMemory = MemoryBackendFactory.CreateLongTermStorage(configuration, workspaceManager);
                    memory = new HybridMemory(100, longTermMemory);

                    toolRegistry.Register(new AddMemoryTool(memory));
                    toolRegistry.Register(new SearchMemoryTool(memory));
                    toolRegistry.Register(new GetAllMemoriesTool(memory));
                    ctx.Status("[green]Ready.[/]");
                });

        AnsiConsole.MarkupLine("[bold green]✓[/] AgentFox initialized successfully.");
        AnsiConsole.WriteLine();

        // ── --doctor mode (runs before web host, then exits) ──────────────────
        if (runDoctor)
        {
            var appCfgPath    = ResolveAppSettingsPath();
            var chatClient    = LLMFactory.CreateFromConfiguration(configuration);
            var doctorAgent   = new DoctorAgent(chatClient, appCfgPath);
            var workspacePath = workspaceManager.ResolvePath("");
            var ltMemory      = MemoryBackendFactory.CreateLongTermStorage(configuration, workspaceManager);

            var doctorRunner = new DoctorRunner(new IHealthCheckable[]
            {
                new ConfigHealthCheck(configuration, doctorAgent),
                new LlmHealthCheck(configuration),
                new EmbeddingHealthCheck(
                    EmbeddingServiceFactory.Create(configuration),
                    ltMemory as SqliteLongTermMemory,
                    configuration),
                new MemoryHealthCheck(ltMemory, configuration, workspacePath),
                new SessionHealthCheck(configuration, workspacePath),
                new SkillHealthCheck(skillRegistry!),
                new ToolHealthCheck(toolRegistry),
                new McpHealthCheck(mcpClient!, configuration, doctorAgent),
            });
            await doctorRunner.RunAsync(doctorFix);
            return 0;
        }

        // ── Single-shot command mode (runs before web host, then exits) ───────
        if (taskArgs.Length > 0)
            return await RunCommandLineMode(taskArgs, configuration, workspaceManager,
                toolRegistry, skillRegistry!, mcpClient!, memory!);

        // ── Register all services in the single DI container ─────────────────
        var uiCfg = new UIConfig();
        configuration.GetSection("UI").Bind(uiCfg);
        builder.Services.AddSingleton(uiCfg);

        // Service configuration
        var serviceCfg = new ServiceConfig();
        configuration.GetSection("Services").Bind(serviceCfg);
        if (string.IsNullOrWhiteSpace(serviceCfg.ServiceName))
            serviceCfg.ServiceName = "AgentFox";
        if (string.IsNullOrWhiteSpace(serviceCfg.LogPath))
            serviceCfg.LogPath = "{workspace}/logs/service.log";
        builder.Services.AddSingleton(serviceCfg);

        // Pre-built singletons
        builder.Services.AddSingleton(workspaceManager);
        builder.Services.AddSingleton(toolRegistry);
        builder.Services.AddSingleton(skillRegistry!);
        builder.Services.AddSingleton(mcpClient!);
        builder.Services.AddSingleton(memory!);

        // LLM
        builder.Services.AddSingleton(_ => LLMFactory.CreateFromConfiguration(configuration));

        // Session management
        builder.Services.AddSingleton(sp =>
        {
            var cfg = new SessionConfig();
            sp.GetRequiredService<IConfiguration>().GetSection("Sessions").Bind(cfg);
            return cfg;
        });
        builder.Services.AddSingleton(sp => new SessionManager(
            sp.GetRequiredService<SessionConfig>(),
            sp.GetRequiredService<WorkspaceManager>()));
        builder.Services.AddSingleton(sp =>
            new MarkdownSessionStore(sp.GetRequiredService<SessionManager>().SessionDirectory));

        // Sub-agent infrastructure
        builder.Services.AddSingleton<ICommandQueue, CommandQueue>();
        builder.Services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            string? defaultModel = cfg.GetSection("Models:SubAgent").Exists() ? "SubAgent" : null;
            return new SubAgentConfiguration
            {
                MaxSpawnDepth            = 3,
                MaxConcurrentSubAgents   = 10,
                MaxChildrenPerAgent      = 5,
                DefaultRunTimeoutSeconds = 300,
                DefaultModel             = defaultModel,
                DefaultThinkingLevel     = "high",
                AutoCleanupCompleted     = true,
                CleanupDelayMilliseconds = 5000
            };
        });
        builder.Services.AddSingleton<IAgentRuntime>(sp => new DefaultAgentRuntime(
            sp.GetRequiredService<ToolRegistry>(),
            executor: null,
            sp.GetRequiredService<ILogger<DefaultAgentRuntime>>()));
        builder.Services.AddSingleton(sp => new SubAgentManager(
            sp.GetRequiredService<ICommandQueue>(),
            sp.GetRequiredService<IAgentRuntime>(),
            sp.GetRequiredService<SubAgentConfiguration>(),
            sp.GetRequiredService<ILogger<SubAgentManager>>(),
            sp.GetRequiredService<SessionManager>()));
        builder.Services.AddSingleton(sp => new CommandProcessor(
            sp.GetRequiredService<ICommandQueue>(),
            CommandProcessorConfig.FromSubAgentConfig(sp.GetRequiredService<SubAgentConfiguration>()),
            sp.GetRequiredService<ILogger<CommandProcessor>>()));

        // Agent holder + channel manager holder + IAgentService (used by WebModule /chat)
        builder.Services.AddSingleton<FoxAgentHolder>();
        builder.Services.AddSingleton<ChannelManagerHolder>();
        builder.Services.AddSingleton<AgentFox.Plugins.Interfaces.IAgentService, FoxAgentService>();

        // AgentOrchestrator — builds the main agent, starts the command processor,
        // and connects channels. Runs in every mode (cli, web, api, service).
        builder.Services.AddHostedService<AgentOrchestrator>();

        // Service heartbeat (for periodic health checks when running as service)
        if (serviceCfg.Enabled && serviceCfg.HeartbeatIntervalSeconds > 0)
        {
            builder.Services.AddHostedService<ServiceHeartbeat>();
        }

        // ── Load modules ──────────────────────────────────────────────────────
        var enabledModules = configuration["Modules"]?.Split(',') ?? new[] { "cli", "web" };
        bool requiresWeb   = enabledModules.Contains("api") || enabledModules.Contains("web");
        var modules        = LoadPluginsAndModules(builder);

        // Expose module list for CliWorker plugin notification
        builder.Services.AddSingleton<IEnumerable<IAppModule>>(modules);

        foreach (var module in modules.Where(m => enabledModules.Contains(m.Name)))
            module.RegisterServices(builder.Services, configuration);

        // ── Build and configure the web application ───────────────────────────
        var app = builder.Build();

        if (requiresWeb)
        {
            app.UseRouting();
            app.UseDefaultFiles();
            app.UseStaticFiles();

            foreach (var module in modules.Where(m => enabledModules.Contains(m.Name)))
                module.MapEndpoints(app);
        }

        // Notify modules of startup (IAppModule.StartAsync)
        foreach (var module in modules.Where(m => enabledModules.Contains(m.Name)))
            await module.StartAsync(app.Services);

        // RunAsync starts all IHostedService instances (CliWorker, etc.) and the web server.
        await app.RunAsync();
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Banner
    // ─────────────────────────────────────────────────────────────────────────

    static void ShowBanner()
    {
        AnsiConsole.Write(new FigletText("AgentFox")
            .Centered()
            .Color(Color.DodgerBlue1));

        AnsiConsole.Write(new Rule("[bold blue] Multi-Agent AI Framework [/]")
        {
            Justification = Justify.Center,
            Style = Style.Parse("blue"),
        });

        AnsiConsole.MarkupLine("[dim]  Sub-agents · Memory · MCP · Skills · Channel Integrations[/]");
        AnsiConsole.WriteLine();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Configuration
    // ─────────────────────────────────────────────────────────────────────────

    static IConfiguration BuildConfiguration()
    {
        var envName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{envName}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Single-shot command mode (no interactive REPL)
    // ─────────────────────────────────────────────────────────────────────────

    static async Task<int> RunCommandLineMode(
        string[] taskArgs,
        IConfiguration configuration,
        WorkspaceManager workspaceManager,
        ToolRegistry toolRegistry,
        SkillRegistry skillRegistry,
        MCPClient mcpClient,
        HybridMemory memory)
    {
        var sessionCfg = new SessionConfig();
        configuration.GetSection("Sessions").Bind(sessionCfg);
        var sessionManager = new SessionManager(sessionCfg, workspaceManager);
        var sessionStore   = new MarkdownSessionStore(sessionManager.SessionDirectory);

        var subAgentConfig = new SubAgentConfiguration
        {
            MaxSpawnDepth            = 3,
            MaxConcurrentSubAgents   = 5,
            MaxChildrenPerAgent      = 3,
            DefaultRunTimeoutSeconds = 300,
            AutoCleanupCompleted     = true,
            CleanupDelayMilliseconds = 5000
        };
        var agentRuntime = new DefaultAgentRuntime(
            toolRegistry, executor: null, new ConsoleLogger<DefaultAgentRuntime>());
        var commandQueue    = new CommandQueue();
        var subAgentManager = new SubAgentManager(
            commandQueue, agentRuntime, subAgentConfig,
            new ConsoleLogger<SubAgentManager>(), sessionManager);

        FoxAgent? agentRef    = null;
        var spawnTool         = new SpawnSubAgentTool(() => agentRef!);
        toolRegistry.Register(spawnTool);
        var spawnBgTool       = new SpawnBackgroundSubAgentTool(subAgentManager);
        toolRegistry.Register(spawnBgTool);

        var systemPrompt = new SystemPromptBuilder()
            .WithPersona(SystemPromptConfig.AgentPrompts.DeveloperAssistant)
            .WithAllTools(toolRegistry)
            .WithSkillsIndex(skillRegistry.GetSkillManifests())
            .WithConstraints(
                "Always verify changes before executing destructive operations",
                "Prioritize security and best practices",
                "Ask for clarification when requirements are ambiguous",
                "Use add_memory to save important user facts or preferences to long-term memory.",
                "Use search_memory to recall past information or facts when requested.",
                "Use get_all_memories to retrieve everything stored in long-term memory.")
            .Build();

        var chatClient = LLMFactory.CreateFromConfiguration(configuration);
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
            .WithCompactionFromConfig(configuration)
            .Build();
        agentRef = agent;

        var cliSessionId = sessionManager.GetOrCreateConsoleSession(agent.Id);
        spawnBgTool.Initialize(
            parentAgentId:   agent.Id,
            parentSessionKey: cliSessionId,
            parentSpawnDepth: 0);

        var task = string.Join(" ", taskArgs);

        AnsiConsole.Write(new Rule("[bold]Task[/]") { Justification = Justify.Left, Style = Style.Parse("blue") });
        AnsiConsole.MarkupLine($"[italic]{Markup.Escape(task)}[/]");
        AnsiConsole.Write(new Rule() { Style = Style.Parse("blue dim") });
        AnsiConsole.WriteLine();

        AgentResult result = null!;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots12)
            .SpinnerStyle(Style.Parse("blue"))
            .StartAsync("[blue]Agent is working...[/]", async _ =>
            {
                result = await agent.ProcessAsync(task, cliSessionId);
            });

        AnsiConsole.Write(new Rule("[bold green]Result[/]") { Justification = Justify.Left, Style = Style.Parse("green") });
        AnsiConsole.WriteLine(result.Output);

        if (!string.IsNullOrEmpty(result.Error))
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] {Markup.Escape(result.Error)}");
            return 1;
        }

        return result.Success ? 0 : 1;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Config path helper
    // ─────────────────────────────────────────────────────────────────────────

    static string ResolveAppSettingsPath()
    {
        var cwdPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        return File.Exists(cwdPath) ? cwdPath : Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Factory helpers
    // ─────────────────────────────────────────────────────────────────────────

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
        var servers = configuration.GetSection("MCP:Servers").Get<List<McpServerConfig>>() ?? new();

        foreach (var serverConfig in servers.Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Url)))
        {
            try
            {
                var success = await mcpClient.AddServerAsync(
                    serverConfig.Name, serverConfig.Url,
                    serverConfig.TimeoutSeconds <= 0 ? 30 : serverConfig.TimeoutSeconds,
                    serverConfig.Headers);
                if (!success)
                    AnsiConsole.MarkupLine($"[bold yellow]⚠[/]  MCP server [dim]{Markup.Escape(serverConfig.Name)}[/]: connection failed.");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[bold yellow]⚠[/]  MCP server [dim]{Markup.Escape(serverConfig.Name)}[/]: {Markup.Escape(ex.Message)}");
            }
        }

        if (servers.Count > 0)
            AnsiConsole.MarkupLine($"[bold green]✓[/]  MCP: {servers.Count} server(s) configured.");

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
                    logger: new ConsoleLogger<ComposioSkillProvider>());

                var toolkits = configuration.GetSection("Composio:Toolkits").Get<List<string>>() ?? new();

                if (toolkits.Any())
                    await composioProvider.InitializeAsync(filterToolkitIds: toolkits.ToArray());
                else
                    await composioProvider.InitializeAsync();

                AnsiConsole.MarkupLine("[bold green]✓[/]  Composio skills initialized.");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[bold yellow]⚠[/]  Composio skills: {Markup.Escape(ex.Message)}");
            }
        }

        return skillRegistry;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Plugin / module loader
    // ─────────────────────────────────────────────────────────────────────────

    private static List<IAppModule> LoadPluginsAndModules(WebApplicationBuilder builder)
    {
        var pluginFolder = Path.Combine(AppContext.BaseDirectory, "plugins");
        Directory.CreateDirectory(pluginFolder);

        // Create a temporary service provider for tool instantiation
        var tempServices = new ServiceCollection();
        tempServices.AddSingleton(builder.Configuration.GetSection("Plugins"));
        var tempProvider = tempServices.BuildServiceProvider();

        var pluginLoader = new PluginLoader(tempProvider);
        var toolLoader = new ToolLoader(tempProvider);

        var pluginModules = pluginLoader.LoadModules(pluginFolder);
        // Register modules
        var allModules = ModuleLoader.LoadModules();
        allModules.AddRange(pluginModules);

        var pluginTools = toolLoader.LoadTools(pluginFolder);

        // Register tools
        foreach (var tool in pluginTools)
        {
            builder.Services.AddSingleton(typeof(ITool), tool);
        }

        return allModules;
    }
}
