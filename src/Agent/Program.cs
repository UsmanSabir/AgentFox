using AgentFox.Agents;
using AgentFox.Channels;
using AgentFox.Doctor;
using AgentFox.Doctor.Checks;
using AgentFox.LLM;
using AgentFox.MCP;
using AgentFox.Memory;
using AgentFox.Models;
using AgentFox.Sessions;
using AgentFox.Skills;
using AgentFox.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text;
using System.Threading.Channels;
using SysChannel = System.Threading.Channels.Channel;
using SystemPromptBuilder = AgentFox.LLM.SystemPromptBuilder;

namespace AgentFox;

// ─────────────────────────────────────────────────────────────────────────────
// UI / Logging configuration models
// ─────────────────────────────────────────────────────────────────────────────

internal class UIConfig
{
    /// <summary>When false, hides the reasoning panel and shows a spinner instead.</summary>
    public bool RenderReasoning { get; set; } = true;
}

internal class LoggingConfig
{
    public bool UseFileLogger { get; set; } = true;
    public string FilePath { get; set; } = "logs/agentfox.log";
    public LogLevel MinLevel { get; set; } = LogLevel.Warning;
    /// <summary>Log files older than this many days are deleted on startup. 0 = disabled.</summary>
    public int RetentionDays { get; set; } = 3;
}

// ─────────────────────────────────────────────────────────────────────────────
// Loggers
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Spectre.Console-backed logger for structured, colorized console output.
/// </summary>
internal class ConsoleLogger : ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var (prefix, style) = logLevel switch
        {
            LogLevel.Error => ("[[ERR]]", "bold red"),
            LogLevel.Warning => ("[[WARN]]", "bold yellow"),
            LogLevel.Debug => ("[[DBG]]", "grey"),
            _ => ("[[INF]]", "dim blue"),
        };
        AnsiConsole.MarkupLine($"[{style}]{prefix}[/] {Markup.Escape(message)}");
        if (exception != null)
            AnsiConsole.MarkupLine($"  [red]↳ {Markup.Escape(exception.Message)}[/]");
    }
}

internal class ConsoleLogger<T> : ConsoleLogger, ILogger<T> where T : class { }

/// <summary>
/// Thread-safe file logger. Call <see cref="Configure"/> once at startup before DI registration.
/// </summary>
internal class FileLogger : ILogger
{
    private static readonly object _fileLock = new();
    private static string _filePath = "logs/agentfox.log";
    private static LogLevel _minLevel = LogLevel.Warning;

    public static void Configure(string filePath, LogLevel minLevel)
    {
        _filePath = filePath;
        _minLevel = minLevel;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public static void DeleteOldLogs(string filePath, int retentionDays)
    {
        if (retentionDays <= 0) return;
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(dir, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch { /* best-effort */ }
        }
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        var prefix = logLevel switch
        {
            LogLevel.Error => "[ERR] ",
            LogLevel.Warning => "[WARN]",
            LogLevel.Information => "[INF] ",
            LogLevel.Debug => "[DBG] ",
            _ => "[TRC] ",
        };
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {prefix} {message}";
        if (exception != null)
            line += $"\n       ↳ {exception}";
        lock (_fileLock)
            File.AppendAllText(_filePath, line + "\n");
    }
}

internal class FileLogger<T> : FileLogger, ILogger<T> where T : class { }


/// <summary>
/// AgentFox - Multi-agent framework in C#
/// A multi-agent framework with sub-agents, memory, MCP, skills, and channel integrations
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        ShowBanner();

        bool runDoctor = args.Contains("--doctor");
        bool doctorAutoFix = args.Contains("--fix");

        var configuration = BuildConfiguration();

        IServiceProvider serviceProvider = null!;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots12)
            .SpinnerStyle(Style.Parse("dodgerblue1 bold"))
            .StartAsync("[bold]Initializing AgentFox[/] [dim]— loading tools, memory & integrations...[/]",
                async ctx =>
                {
                    ctx.Status("[dodgerblue1]Registering tools & workspace...[/]");
                    serviceProvider = await BuildServiceProviderAsync(configuration);
                    ctx.Status("[green]Ready.[/]");
                });

        AnsiConsole.MarkupLine("[bold green]✓[/] AgentFox initialized successfully.");
        AnsiConsole.WriteLine();

        if (runDoctor)
        {
            var workspaceManager = serviceProvider.GetRequiredService<WorkspaceManager>();
            var toolRegistry     = serviceProvider.GetRequiredService<ToolRegistry>();
            var skillRegistry    = serviceProvider.GetRequiredService<SkillRegistry>();
            var mcpClient        = serviceProvider.GetRequiredService<MCPClient>();
            var longTermMemory   = MemoryBackendFactory.CreateLongTermStorage(configuration, workspaceManager);
            var workspacePath    = workspaceManager.ResolvePath("");

            var startupConfigFilePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json")))
                startupConfigFilePath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            var startupDoctorAgent = new DoctorAgent(serviceProvider.GetRequiredService<IChatClient>(), startupConfigFilePath);

            var doctorRunner = new DoctorRunner(new IHealthCheckable[]
            {
                new ConfigHealthCheck(configuration, startupDoctorAgent),
                new LlmHealthCheck(configuration),
                new EmbeddingHealthCheck(
                    EmbeddingServiceFactory.Create(configuration),
                    longTermMemory as SqliteLongTermMemory,
                    configuration),
                new MemoryHealthCheck(longTermMemory, configuration, workspacePath),
                new SessionHealthCheck(configuration, workspacePath),
                new SkillHealthCheck(skillRegistry),
                new ToolHealthCheck(toolRegistry),
                new McpHealthCheck(mcpClient, configuration, startupDoctorAgent),
            });
            await doctorRunner.RunAsync(doctorAutoFix);
            return 0;
        }

        if (args.Length > 0)
            return await RunCommandLineMode(args, serviceProvider);

        return await RunInteractiveMode(serviceProvider);
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
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{envName}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DI container
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds and returns the application's service provider.
    /// Services that require async initialization are pre-created and registered as instances.
    /// </summary>
    static async Task<IServiceProvider> BuildServiceProviderAsync(IConfiguration configuration)
    {
        var services = new ServiceCollection();

        // ── Logging config ───────────────────────────────────────────────────
        var loggingCfg = new LoggingConfig();
        configuration.GetSection("Logging").Bind(loggingCfg);

        if (loggingCfg.UseFileLogger)
        {
            FileLogger.Configure(loggingCfg.FilePath, loggingCfg.MinLevel);
            FileLogger.DeleteOldLogs(loggingCfg.FilePath, loggingCfg.RetentionDays);
            services.AddSingleton(typeof(ILogger<>), typeof(FileLogger<>));
        }
        else
        {
            services.AddSingleton(typeof(ILogger<>), typeof(ConsoleLogger<>));
        }

        // ── UI config ────────────────────────────────────────────────────────
        var uiCfg = new UIConfig();
        configuration.GetSection("UI").Bind(uiCfg);
        services.AddSingleton(uiCfg);

        // Configuration
        services.AddSingleton(configuration);

        // ── Pre-create services with async / ordering dependencies ──────────
        // ToolRegistry must exist before SkillRegistry and MCPClient (async init).
        var workspaceManager = new WorkspaceManager(configuration);
        var toolRegistry = CreateToolRegistry(workspaceManager);

        var skillRegistry = await CreateSkillRegistryAsync(toolRegistry, configuration);
        var mcpClient = await CreateAndInitializeMcpClientAsync(toolRegistry, configuration);

        var appConfigPath = ResolveAppSettingsPath();
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

        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();

            // Resolve the default sub-agent model from the Models section.
            // Prefer Models:SubAgent; fall back to null so FoxAgentExecutor uses the primary LLM.
            string? defaultModel = cfg.GetSection("Models:SubAgent").Exists() ? "SubAgent" : null;

            return new SubAgentConfiguration
            {
                MaxSpawnDepth = 3,
                MaxConcurrentSubAgents = 10,
                MaxChildrenPerAgent = 5,
                DefaultRunTimeoutSeconds = 300,
                DefaultModel = defaultModel,
                DefaultThinkingLevel = "high",
                AutoCleanupCompleted = true,
                CleanupDelayMilliseconds = 5000
            };
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

    // ─────────────────────────────────────────────────────────────────────────
    // Execution modes
    // ─────────────────────────────────────────────────────────────────────────

    static async Task<int> RunCommandLineMode(string[] args, IServiceProvider sp)
    {
        var skillRegistry = sp.GetRequiredService<SkillRegistry>();
        var toolRegistry = sp.GetRequiredService<ToolRegistry>();
        var subAgentManager = sp.GetRequiredService<SubAgentManager>();
        var sessionManager = sp.GetRequiredService<SessionManager>();

        // Register tools before building the system prompt so WithAllTools captures
        // real names and descriptions from the registry.
        FoxAgent? agentRef = null;
        var spawnSubAgentTool = new SpawnSubAgentTool(() => agentRef!);
        toolRegistry.Register(spawnSubAgentTool);

        var spawnBgTool = new SpawnBackgroundSubAgentTool(subAgentManager);
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
                "Use get_all_memories to retrieve everything stored in long-term memory."
            )
            .Build();

        var agent = BuildAgent(sp, systemPrompt);
        agentRef = agent;

        var cliSessionId = sessionManager.GetOrCreateConsoleSession(agent.Id);
        spawnBgTool.Initialize(
            parentAgentId: agent.Id,
            parentSessionKey: cliSessionId,
            parentSpawnDepth: 0
        );

        var task = string.Join(" ", args);

        AnsiConsole.Write(new Rule("[bold]Task[/]") { Justification = Justify.Left, Style = Style.Parse("blue") });
        AnsiConsole.MarkupLine($"[italic]{Markup.Escape(task)}[/]");
        AnsiConsole.Write(new Rule() { Style = Style.Parse("blue dim") });
        AnsiConsole.WriteLine();

        // cliSessionId was already obtained above for spawnBgTool.Initialize()

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

    static async Task<int> RunInteractiveMode(IServiceProvider sp)
    {
        var skillRegistry = sp.GetRequiredService<SkillRegistry>();
        var toolRegistry = sp.GetRequiredService<ToolRegistry>();
        var sessionManager = sp.GetRequiredService<SessionManager>();
        var subAgentManager = sp.GetRequiredService<SubAgentManager>();
        var commandProcessor = sp.GetRequiredService<CommandProcessor>();
        var commandQueue = sp.GetRequiredService<ICommandQueue>();
        var uiCfg = sp.GetRequiredService<UIConfig>();

        var manifests = skillRegistry.GetSkillManifests();
        AnsiConsole.MarkupLine($"[bold green]✓[/] [dim]{manifests.Count} skill(s) registered.[/]");
        AnsiConsole.WriteLine();

        // Register tools before building the system prompt so WithAllTools captures
        // real names and descriptions from the registry.
        //
        // Circular dependency is broken by:
        //   • SpawnSubAgentTool  – receives a Func<FoxAgent> resolved after Build()
        //   • SpawnBackgroundSubAgentTool – receives agent/session ids via Initialize()
        FoxAgent? agentRef = null;
        var spawnSubAgentTool = new SpawnSubAgentTool(() => agentRef!);
        toolRegistry.Register(spawnSubAgentTool);

        var spawnBgTool = new SpawnBackgroundSubAgentTool(
            subAgentManager,
            logger: sp.GetRequiredService<ILogger<SpawnBackgroundSubAgentTool>>()
        );
        toolRegistry.Register(spawnBgTool);

        toolRegistry.Register(new ManageMCPTool(
            sp.GetRequiredService<MCPClient>(),
            ResolveAppSettingsPath(),
            sp.GetRequiredService<ILogger<ManageMCPTool>>()));

        var systemPrompt = new SystemPromptBuilder()
            .WithPersona(SystemPromptConfig.AgentPrompts.DeveloperAssistant)
            .WithAllTools(toolRegistry)
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
        agentRef = agent; // resolve the lazy reference used by SpawnSubAgentTool

        // Upgrade runtime to use the real LLM-backed executor now that FoxAgent exists.
        var agentRuntime = sp.GetRequiredService<IAgentRuntime>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        agentRuntime.SetExecutor(new FoxAgentExecutor(
            defaultAgent: agent,
            agentFactory: client => BuildAgentWithClient(sp, systemPrompt, client),
            modelResolver: model => LLMFactory.CreateWithModelOverride(configuration, model),
            logger: sp.GetRequiredService<ILogger<FoxAgentExecutor>>()
        ));

        // ── Load and register channels ────────────────────────────────────────
        // Must happen after agent is built so SendToChannelTool can reference the agent.
        // We always create a ChannelManager so channels can be added at runtime via ManageChannelTool.
        var appConfigPath  = ResolveAppSettingsPath();
        var channelManager = await LoadChannelsFromConfigAsync(sp, agent, configuration);

        toolRegistry.Register(new SendToChannelTool(
            channelManager,
            sp.GetRequiredService<ILogger<SendToChannelTool>>()));

        toolRegistry.Register(new ManageChannelTool(
            channelManager,
            appConfigPath,
            sp.GetRequiredService<ILogger<ManageChannelTool>>()));

        if (channelManager.Channels.Count > 0)
            AnsiConsole.MarkupLine($"[bold green]✓[/]  {channelManager.Channels.Count} channel(s) connected.");
        else
            AnsiConsole.MarkupLine("[dim]No channels configured. Use manage_channel to add one at runtime.[/]");

        // Snapshot interrupted sessions BEFORE creating/getting this run's console session,
        // so we know which Active sessions are left over from a previous process.
        var interruptedSessions = sessionManager.GetInterruptedActiveSessions();

        var consoleSessionId = sessionManager.GetOrCreateConsoleSession(agent.Id);
        spawnBgTool.Initialize(
            parentAgentId: agent.Id,
            parentSessionKey: consoleSessionId,
            parentSpawnDepth: 0
        );

        // --- Subagent lane handler ---
        commandProcessor.RegisterLaneHandler(CommandLane.Subagent, async (command, ct) =>
        {
            if (command is not AgentCommand agentCmd) return;

            var runId = agentCmd.RunId;
            var subTask = subAgentManager.GetSubAgentTask(runId);

            if (subTask != null)
                await subTask.PauseGate.WhenResumedAsync(ct);

            subAgentManager.OnSubAgentStarted(runId);

            using var linked = subTask != null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct, subTask.CancellationTokenSource.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                var subResult = await agentRuntime.ExecuteAsync(agentCmd, linked.Token);

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
            // ── 1. User / injected agent turn ────────────────────────────────
            if (command is AgentCommand agentCmd)
            {
                AgentResult result;
                try
                {
                    result = await agentRuntime.ExecuteAsync(agentCmd, ct);
                }
                catch (Exception ex)
                {
                    result = new AgentResult { Success = false, Error = ex.Message };
                }

                agentCmd.ResultSource?.TrySetResult(result);
                return;
            }

            // ── 2. Sub-agent result announcement ─────────────────────────────
            if (command is not ResultAnnouncementCommand announcement) return;

            if (announcement.RequesterChannel != null && !announcement.SuppressChannelNotification)
            {
                try
                {
                    await announcement.RequesterChannel.SendMessageAsync(announcement.FormatMessage());
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[bold red][[ERR]][/] Failed to send result to channel: {Markup.Escape(ex.Message)}");
                }
                return;
            }

            if (!string.IsNullOrEmpty(announcement.ParentSessionKey))
            {
                var notification = $"[Background sub-agent result]\n{announcement.FormatMessage()}";
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[bold blue][[SUB-AGENT]][/] Reporting result to parent agent [dim](session: {Markup.Escape(announcement.ParentSessionKey)})[/]...");

                var notifyCmd = AgentCommand.CreateMainCommand(
                    announcement.ParentSessionKey,
                    agentId: agent.Id,
                    message: notification);

                try
                {
                    var parentResult = await agentRuntime.ExecuteAsync(notifyCmd, ct);
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[bold cyan][[AGENT]][/]");
                    AnsiConsole.WriteLine(parentResult.Output);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[bold red][[ERR]][/] Failed to deliver sub-agent result to parent agent: {Markup.Escape(ex.Message)}");
                }

                AnsiConsole.Markup("\n[bold dodgerblue1]>[/] ");
            }
        });

        // --- Result callback ---
        subAgentManager.RegisterResultCallback(async (task, result) =>
        {
            AnsiConsole.MarkupLine($"\n[bold blue][[BG]][/] Sub-agent [dim]{Markup.Escape(task.SessionKey)}[/] finished — status: [bold]{Markup.Escape(result.Status.ToString())}[/]");

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

        // ── Startup recovery ─────────────────────────────────────────────────
        await RecoverInterruptedSessionsAsync(
            sessionManager,
            sp.GetRequiredService<MarkdownSessionStore>(),
            commandQueue,
            agent,
            consoleSessionId,
            interruptedSessions);

        AnsiConsole.MarkupLine("[dim]Type [bold white]help[/] for commands, [bold white]exit[/] to quit. [bold white]Shift+Enter[/] for multi-line input.[/]");
        AnsiConsole.WriteLine();

        // ── DoctorAgent ───────────────────────────────────────────────────────
        var doctorAgent = new DoctorAgent(sp.GetRequiredService<IChatClient>(), appConfigPath);

        while (true)
        {
            var input = ReadMultilineInput();

            if (string.IsNullOrWhiteSpace(input))
                continue;

            var trimmed = input.Trim();
            var lower = trimmed.ToLower();

            // ── Built-in REPL commands ────────────────────────────────────────
            if (lower is "exit")
            {
                AnsiConsole.MarkupLine("[bold green]Goodbye![/]");
                if (channelManager != null)
                    await channelManager.DisconnectAllAsync();
                await commandProcessor.StopAsync(TimeSpan.FromSeconds(10));
                break;
            }

            if (lower is "help" or "?")
            {
                ShowHelp();
                continue;
            }

            if (lower == "status")
            {
                ShowStatus(agent);
                continue;
            }

            if (lower == "doctor" || lower == "doctor fix")
            {
                var autoFix = lower == "doctor fix";
                var workspaceManager = sp.GetRequiredService<WorkspaceManager>();
                var workspacePath    = workspaceManager.ResolvePath("");
                var ltMemory         = MemoryBackendFactory.CreateLongTermStorage(configuration, workspaceManager);
                var replMcpClient    = sp.GetRequiredService<MCPClient>();
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
                    new SkillHealthCheck(skillRegistry),
                    new ToolHealthCheck(toolRegistry),
                    new McpHealthCheck(replMcpClient, configuration, doctorAgent),
                });
                await doctorRunner.RunAsync(autoFix);
                continue;
            }

            if (lower.StartsWith("doctor config ") || lower.StartsWith("doctor configure "))
            {
                var request = lower.StartsWith("doctor config ")
                    ? trimmed["doctor config ".Length..].Trim()
                    : trimmed["doctor configure ".Length..].Trim();
                if (string.IsNullOrWhiteSpace(request))
                {
                    AnsiConsole.MarkupLine("[yellow]Usage: doctor config <your request>[/]");
                    AnsiConsole.MarkupLine("[dim]Example: doctor config set LLM provider to Anthropic with claude-3-5-sonnet[/]");
                }
                else
                {
                    var doctorConfigResult = await doctorAgent.ProcessRequestAsync(request);
                    AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(doctorConfigResult)}[/]");
                }
                continue;
            }

            if (lower == "tools")
            {
                ShowTools(toolRegistry);
                continue;
            }

            if (lower == "skills")
            {
                ShowSkills(skillRegistry);
                continue;
            }

            if (lower.StartsWith("skill "))
            {
                ShowSkillDetail(skillRegistry, trimmed[6..].Trim());
                continue;
            }

            // ── Agent management commands ─────────────────────────────────────
            if (lower is "agents" or "agents list")
            {
                ShowAgents(subAgentManager);
                continue;
            }

            if (lower == "agents stats")
            {
                ShowAgentStats(subAgentManager, commandProcessor);
                continue;
            }

            if (lower.StartsWith("agents pause "))
            {
                HandleAgentPause(subAgentManager, trimmed[13..].Trim());
                continue;
            }

            if (lower.StartsWith("agents resume "))
            {
                HandleAgentResume(subAgentManager, trimmed[14..].Trim());
                continue;
            }

            if (lower.StartsWith("agents stop "))
            {
                await HandleAgentStopAsync(subAgentManager, trimmed[12..].Trim());
                continue;
            }

            if (lower.StartsWith("agents kill"))
            {
                var target = lower.Length > 11 ? trimmed[11..].Trim() : "all";
                HandleAgentKill(subAgentManager, target);
                continue;
            }

            AnsiConsole.WriteLine();

            // Route the user turn through the Main lane so the CommandProcessor
            // enforces serial execution and the FoxAgentExecutor handles it uniformly.
            var tcs = new TaskCompletionSource<AgentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cmd = AgentCommand.CreateMainCommand(
                sessionKey: consoleSessionId,
                agentId: agent.Id,
                message: trimmed);
            cmd.ResultSource = tcs;

            // Unified stream channel: bool flag distinguishes reasoning (true) from response tokens (false).
            var streamChannel = SysChannel.CreateUnbounded<(bool IsReasoning, string Text)>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            var sb = new StringBuilder();
            var sbReasoning = new StringBuilder();
            Task<string>? liveDisplayTask = null;

            //if (uiCfg.RenderReasoning)
            {
                // ── Render reasoning inline with throttled refresh ────────────
                // We only rebuild the Panel when content actually changed and at
                // most once per ~80 ms to avoid the "Thinking..." flicker caused
                // by Spectre repainting on every single token.
                //const int RefreshIntervalMs = 80;

                IRenderable BuildDisplay()
                {
                    var responseText = sb.ToString();
                    if (sbReasoning.Length == 0)
                    {
                        //return new Rows(new Panel(string.Empty).NoBorder(), new Text(Markup.Escape(responseText)));
                        var table = new Table().NoBorder();
                        table.AddColumn(new TableColumn(""));
                        table.AddRow(Markup.Escape(responseText));
                        return table;
                        //return new Text(responseText.Trim());
                    }

                    // Limit visible reasoning to the last 20 lines to keep the
                    // panel from growing taller than the terminal.
                    var reasoningLines = sbReasoning.ToString().Split('\n');
                    var visibleReasoning = reasoningLines.Length > 20
                        ? string.Join('\n', reasoningLines[^20..])
                        : sbReasoning.ToString();

                    var thinkingPanel = new Panel(
                            new Markup($"[italic dim yellow]{Markup.Escape(visibleReasoning.TrimEnd('\n'))}[/]"))
                        .Header("[dim yellow]Thinking...[/]")
                        .BorderColor(Color.Yellow)
                        .Expand();

                    return sb.Length == 0
                        ? thinkingPanel
                        : new Rows(thinkingPanel, new Text(responseText));
                }

                cmd.Streaming = new StreamingCallbacks
                {
                    OnStart = () =>
                    {
                        if (!uiCfg.RenderReasoning)
                        {
                            //https://github.com/spectreconsole/spectre.console/issues/2076
                            var table = new Table().NoBorder();
                            table.AddColumn(new TableColumn(""));
                            table.AddRow("Working...");
                            liveDisplayTask = AnsiConsole.Live(table)
                                .AutoClear(false)
                                .Overflow(VerticalOverflow.Ellipsis)
                                .Cropping(VerticalOverflowCropping.Top)
                                .StartAsync(async ctx =>
                                {
                                    await foreach (var (isReasoning, text) in streamChannel.Reader.ReadAllAsync())
                                    {
                                        if (!isReasoning)
                                        {
                                            sb.Append(text);
                                            var responseChunk = sb.ToString();
                                            if (!string.IsNullOrWhiteSpace(responseChunk))
                                            {
                                                table.Rows.Clear();
                                                table.AddRow(Markup.Escape(responseChunk));
                                                ctx.Refresh();
                                            }
                                        }
                                    }
                                    
                                    return string.Empty;
                                });
                            #region commented
                            
                            //liveDisplayTask = AnsiConsole.Status()
                            //    .Spinner(Spinner.Known.Dots12)
                            //    .SpinnerStyle(Style.Parse("dodgerblue1 bold"))
                            //    .StartAsync(
                            //        "[bold]Working...[/]",
                            //        async ctx =>
                            //        {
                            //            bool spinnerUpdated = false;
                            //            //ctx.Status("[dodgerblue1]Processing...[/]");
                            //            await foreach (var (isReasoning, text) in streamChannel.Reader.ReadAllAsync())
                            //            {
                            //                if (!isReasoning)
                            //                {
                            //                    sb.Append(text);
                            //                    var responseChunk = sb.ToString();
                            //                    if (!string.IsNullOrWhiteSpace(responseChunk))
                            //                    {
                            //                        if (!spinnerUpdated)
                            //                        {
                            //                            ctx.Spinner(Spinner.Known.Star);
                            //                            spinnerUpdated = true;
                            //                        } 
                            //                        ctx.Status(Markup.Escape(responseChunk));
                            //                    }
                            //                }
                            //                //else
                            //                //    sbReasoning.Append(text);
                            //            }
                            //            ctx.Status(Markup.Escape("done"));
                            //            var response = sb.ToString();
                            //            //if (!string.IsNullOrWhiteSpace(response))
                            //            //    AnsiConsole.MarkupLine(Markup.Escape(response));
                            //            return response;
                            //        }); 
                            #endregion
                        }
                        else
                        {
                            //https://github.com/spectreconsole/spectre.console/issues/2076
                            liveDisplayTask = AnsiConsole.Live(new Text(string.Empty))
                                .AutoClear(false)
                                .Overflow(VerticalOverflow.Ellipsis)
                                .Cropping(VerticalOverflowCropping.Top)
                                .StartAsync(async ctx =>
                                {
                                    //var lastRefresh = DateTime.UtcNow;
                                    //bool dirty = false;

                                    await foreach (var (isReasoning, text) in streamChannel.Reader.ReadAllAsync())
                                    {
                                        if (isReasoning) sbReasoning.Append(text);
                                        else sb.Append(text);
                                        //dirty = true;

                                        //var elapsed = (DateTime.UtcNow - lastRefresh).TotalMilliseconds;
                                        //if (elapsed >= RefreshIntervalMs)
                                        {
                                            //table.Rows.Clear();
                                            //var reasoningLines = sbReasoning.ToString().Split('\n');
                                            //var visibleReasoning = reasoningLines.Length > 20
                                            //    ? string.Join('\n', reasoningLines[^20..])
                                            //    : sbReasoning.ToString();
                                            //table.AddRow(visibleReasoning);
                                            ctx.UpdateTarget(BuildDisplay());
                                            ctx.Refresh();
                                            //lastRefresh = DateTime.UtcNow;
                                            //dirty = false;
                                        }
                                        //table.Rows.Update(status.Rows.Count - 1, 1, new Text("[green]Complete[/]"));
                                        //ctx.Refresh();
                                    }

                                    // Final flush
                                    //if (dirty)
                                    //{
                                    //    ctx.UpdateTarget(BuildDisplay());
                                    //    ctx.Refresh();
                                    //}
                                    return string.Empty; //sb.ToString();
                                });
                        }

                        return Task.CompletedTask;
                    },

                    OnReasoning = !uiCfg.RenderReasoning ? null : async chunk => await streamChannel.Writer.WriteAsync((true, chunk)),
                    OnToken = async chunk => await streamChannel.Writer.WriteAsync((false, chunk)),

                    OnComplete = async () =>
                    {
                        streamChannel.Writer.TryComplete();
                        if (liveDisplayTask != null)
                        {
                            var response = await liveDisplayTask;
                            if (!string.IsNullOrWhiteSpace(response))
                            {
                                AnsiConsole.MarkupLine(Markup.Escape(response));
                            }

                        }
                    },
                };
            }

            commandQueue.Enqueue(cmd);

            var result = await tcs.Task;

            // Insert a blank line after the streamed block (or after a silent tool-only turn).
            AnsiConsole.WriteLine();

            if (result.SpawnedSubAgents.Count > 0)
                AnsiConsole.MarkupLine($"[bold blue]↳[/] Spawned [bold]{result.SpawnedSubAgents.Count}[/] sub-agent(s)");
        }

        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Channel loading
    // ─────────────────────────────────────────────────────────────────────────

    static async Task<ChannelManager> LoadChannelsFromConfigAsync(
        IServiceProvider sp,
        FoxAgent agent,
        IConfiguration configuration)
    {
        var sessionManager = sp.GetRequiredService<SessionManager>();
        var commandQueue   = sp.GetRequiredService<ICommandQueue>();
        var logger         = sp.GetRequiredService<ILogger<ChannelManager>>();
        var manager        = new ChannelManager(agent, sessionManager, commandQueue, logger);

        var channelSection = configuration.GetSection("Channels");
        if (!channelSection.Exists()) return manager;

        // ── Telegram ──────────────────────────────────────────────────────────
        var tgSection = channelSection.GetSection("Telegram");
        if (tgSection.Exists() && tgSection.GetValue<bool>("Enabled"))
        {
            var token = tgSection["BotToken"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token) || token.Contains("your-telegram"))
            {
                AnsiConsole.MarkupLine("[bold yellow]⚠[/]  Telegram: BotToken is not configured — skipping.");
            }
            else
            {
                var pollingTimeout = tgSection.GetValue<int>("PollingTimeoutSeconds", 30);
                var (telegramChannel, error) = ChannelFactory.Create("Telegram", new Dictionary<string, string>()
                {
                    ["BotToken"] = token,
                    ["PollingTimeoutSeconds"] = pollingTimeout.ToString()
                });
                if (telegramChannel != null)
                {
                    //manager.AddChannel(new TelegramChannel(
                    //    token,
                    //    pollingTimeout,
                    //    sp.GetRequiredService<ILogger<TelegramChannel>>()));
                    //AnsiConsole.MarkupLine("[bold green]✓[/]  Telegram channel added [dim](long-polling)[/]");
                    manager.AddChannel(telegramChannel);
                }
                else
                {
                    AnsiConsole.MarkupLine($"[bold yellow]⚠[/]  Telegram: {error}");
                }
            }
        }

        if (manager.Channels.Count == 0) return manager;

        AnsiConsole.MarkupLine("[dim]Connecting channels...[/]");
        await manager.ConnectAllAsync();
        return manager;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Config path helper
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the path to appsettings.json, preferring the current working directory
    /// (which is the project root when running with `dotnet run`) over the output directory.
    /// </summary>
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
                    logger: new ConsoleLogger<ComposioSkillProvider>()
                );

                var toolkits = configuration.GetSection("Composio:Toolkits").Get<List<string>>() ?? [];

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
    // Console display helpers
    // ─────────────────────────────────────────────────────────────────────────

    static void ShowHelp()
    {
        var grid = new Grid().AddColumn().AddColumn();

        void AddSection(string header, string[][] rows)
        {
            var table = new Table()
                .Border(TableBorder.None)
                .HideHeaders()
                .AddColumn(new TableColumn("").Width(30))
                .AddColumn(new TableColumn(""));

            foreach (var row in rows)
                table.AddRow($"[bold white]{Markup.Escape(row[0])}[/]", $"[dim]{Markup.Escape(row[1])}[/]");

            AnsiConsole.MarkupLine($"\n[bold dodgerblue1]{header}[/]");
            AnsiConsole.Write(table);
        }

        AddSection("General Commands", new[]
        {
            new[] { "help",        "Show this help message" },
            new[] { "status",      "Show agent status" },
            new[] { "tools",       "List available tools" },
            new[] { "skills",      "List all registered skills" },
            new[] { "skill <name>","Show detailed info for a specific skill" },
            new[] { "doctor",              "Run health checks (config, LLM, memory, MCP, skills, tools)" },
            new[] { "doctor fix",          "Run health checks and attempt automatic fixes" },
            new[] { "doctor config <req>", "Ask DoctorAgent to modify appsettings.json (e.g. change LLM provider)" },
            new[] { "exit",                "Exit the program" },
        });

        AddSection("Startup Flags", new[]
        {
            new[] { "--doctor",    "Run health checks on startup and exit" },
            new[] { "--fix",       "Combined with --doctor: attempt automatic fixes" },
        });

        AddSection("Agent Management", new[]
        {
            new[] { "agents",               "List active sub-agents" },
            new[] { "agents list",          "List active sub-agents (alias)" },
            new[] { "agents stats",         "Show processor/queue statistics" },
            new[] { "agents pause <id>",    "Pause a sub-agent (blocks before next turn)" },
            new[] { "agents resume <id>",   "Resume a paused sub-agent" },
            new[] { "agents stop <id>",     "Gracefully stop a sub-agent" },
            new[] { "agents kill <id>",     "Force-kill a sub-agent immediately" },
            new[] { "agents kill all",      "Force-kill every active sub-agent" },
        });

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]You can also ask the agent to execute commands, read/write files, spawn sub-agents, use skills (git, docker, etc.), and more.[/]");
        AnsiConsole.WriteLine();
    }

    static void ShowStatus(FoxAgent agent)
    {
        var info = agent.GetInfo();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .HideHeaders()
            .AddColumn(new TableColumn("[bold]Property[/]").Width(14))
            .AddColumn(new TableColumn("[bold]Value[/]"));

        table.AddRow("[dim]Name[/]", $"[bold white]{Markup.Escape(info.Name)}[/]");
        table.AddRow("[dim]ID[/]", $"[grey]{Markup.Escape(info.Id)}[/]");
        table.AddRow("[dim]Status[/]", $"[bold green]{Markup.Escape(info.Status.ToString())}[/]");
        table.AddRow("[dim]Messages[/]", info.MessageCount.ToString());
        table.AddRow("[dim]Sub-agents[/]", info.SubAgentCount.ToString());
        table.AddRow("[dim]Tools[/]", info.ToolCount.ToString());
        table.AddRow("[dim]Memory[/]", info.HasMemory ? "[green]Enabled[/]" : "[dim]Disabled[/]");
        table.AddRow("[dim]Created[/]", $"[grey]{info.CreatedAt:yyyy-MM-dd HH:mm:ss}[/]");
        table.AddRow("[dim]Last Active[/]", $"[grey]{info.LastActiveAt:yyyy-MM-dd HH:mm:ss}[/]");

        AnsiConsole.Write(new Panel(table)
        {
            Header = new PanelHeader("[bold] Agent Status [/]", Justify.Left),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("blue"),
            Padding = new Padding(1, 0),
        });

        AnsiConsole.WriteLine();
    }

    static void ShowTools(ToolRegistry registry)
    {
        var tools = registry.GetAll();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .Title($"[bold] Available Tools ({tools.Count}) [/]")
            .AddColumn(new TableColumn("[bold]Name[/]").Width(26))
            .AddColumn(new TableColumn("[bold]Description[/]"));

        foreach (var tool in tools)
            table.AddRow(
                $"[bold white]{Markup.Escape(tool.Name)}[/]",
                $"[dim]{Markup.Escape(tool.Description)}[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    static void ShowSkills(SkillRegistry skillRegistry)
    {
        var manifests = skillRegistry.GetSkillManifests();

        if (manifests.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No skills registered.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .Title($"[bold] Registered Skills ({manifests.Count}) [/]")
            .AddColumn(new TableColumn("[bold]Skill[/]").Width(22))
            .AddColumn(new TableColumn("[bold]Type[/]").Width(10))
            .AddColumn(new TableColumn("[bold]Tools[/]").Width(7).RightAligned())
            .AddColumn(new TableColumn("[bold]Description[/]"));

        foreach (var m in manifests)
        {
            var desc = m.Description.Length > 50 ? m.Description[..47] + "..." : m.Description;
            table.AddRow(
                $"[bold white]{Markup.Escape(m.Name)}[/]",
                $"[dim]{Markup.Escape(m.SkillType)}[/]",
                $"[dodgerblue1]{m.ToolCount}[/]",
                $"[dim]{Markup.Escape(desc)}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Use [bold white]skill <name>[/] for details. Ask the agent: \"load the <skill> skill\" to activate it.[/]");
        AnsiConsole.WriteLine();
    }

    static void ShowSkillDetail(SkillRegistry skillRegistry, string skillName)
    {
        var skill = skillRegistry.Get(skillName)
            ?? skillRegistry.GetAll()
                .FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));

        if (skill == null)
        {
            AnsiConsole.MarkupLine($"[bold red]Skill '{Markup.Escape(skillName)}' not found.[/] Use [bold white]skills[/] to list all registered skills.");
            return;
        }

        var content = new Rows(
            new Markup($"[dim]Description:[/]  {Markup.Escape(skill.Description)}"),
            skill.Dependencies.Count > 0
                ? new Markup($"[dim]Dependencies:[/] {Markup.Escape(string.Join(", ", skill.Dependencies))}")
                : new Markup(""),
            skill.Metadata?.Capabilities.Count > 0
                ? new Markup($"[dim]Capabilities:[/] {Markup.Escape(string.Join(", ", skill.Metadata.Capabilities))}")
                : new Markup(""),
            skill.Metadata?.Tags.Count > 0
                ? new Markup($"[dim]Tags:[/]         {Markup.Escape(string.Join(", ", skill.Metadata.Tags))}")
                : new Markup(""),
            skill.Metadata != null
                ? new Markup($"[dim]Complexity:[/]   [bold]{skill.Metadata.ComplexityScore}[/]/10")
                : new Markup(""),
            new Markup($"[dim]Type:[/]         {(skill is ISkillPlugin ? "local" : "generic")} skill")
        );

        AnsiConsole.Write(new Panel(content)
        {
            Header = new PanelHeader($"[bold] {Markup.Escape(skill.Name)}  v{Markup.Escape(skill.Version)} [/]", Justify.Left),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("blue"),
            Padding = new Padding(1, 0),
        });

        var tools = skill.GetTools();
        if (tools.Count > 0)
        {
            var toolTable = new Table()
                .Border(TableBorder.None)
                .HideHeaders()
                .AddColumn(new TableColumn("").Width(28))
                .AddColumn(new TableColumn(""));

            foreach (var tool in tools)
                toolTable.AddRow(
                    $"[bold white]  • {Markup.Escape(tool.Name)}[/]",
                    $"[dim]{Markup.Escape(tool.Description)}[/]");

            AnsiConsole.MarkupLine($"[bold]Tools ({tools.Count}):[/]");
            AnsiConsole.Write(toolTable);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]To load full guidance: [white]load_skill(skill_name: \"{Markup.Escape(skill.Name)}\")[/][/]");
        AnsiConsole.WriteLine();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Agent management commands
    // ─────────────────────────────────────────────────────────────────────────

    static void ShowAgents(SubAgentManager manager)
    {
        var tasks = manager.GetActiveSubAgents().ToList();
        if (tasks.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No active sub-agents.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .Title($"[bold] Active Sub-Agents ({tasks.Count}) [/]")
            .AddColumn(new TableColumn("[bold]RunId[/]").Width(38))
            .AddColumn(new TableColumn("[bold]State[/]").Width(10))
            .AddColumn(new TableColumn("[bold]Elapsed[/]").Width(9).RightAligned())
            .AddColumn(new TableColumn("[bold]Session[/]"));

        foreach (var t in tasks.OrderBy(t => t.CreatedAt))
        {
            var elapsed = t.ElapsedTime.TotalSeconds < 60
                ? $"{t.ElapsedTime.TotalSeconds:F0}s"
                : $"{t.ElapsedTime:mm\\:ss}";
            var session = t.SessionKey.Length > 32 ? "…" + t.SessionKey[^31..] : t.SessionKey;
            var stateStyle = t.State.ToString() switch
            {
                "Running" => "bold green",
                "Paused" => "bold yellow",
                "Failed" => "bold red",
                _ => "dim"
            };
            table.AddRow(
                $"[grey]{Markup.Escape(t.RunId)}[/]",
                $"[{stateStyle}]{Markup.Escape(t.State.ToString())}[/]",
                $"[dodgerblue1]{elapsed}[/]",
                $"[dim]{Markup.Escape(session)}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    static void ShowAgentStats(SubAgentManager subAgentManager, CommandProcessor commandProcessor)
    {
        var stats = subAgentManager.GetStatistics();
        var pStats = commandProcessor.GetStatistics();

        var agentTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("").Width(22))
            .AddColumn(new TableColumn(""));

        agentTable.AddRow("[dim]Active sub-agents[/]", $"[bold]{stats.TotalActiveSubAgents}[/]");
        agentTable.AddRow("[dim]  Running[/]", $"[bold green]{stats.RunningSubAgents}[/]");
        agentTable.AddRow("[dim]  Pending[/]", stats.PendingSubAgents.ToString());
        agentTable.AddRow("[dim]  Completed[/]", stats.CompletedSubAgents.ToString());
        agentTable.AddRow("[dim]  Failed[/]", $"[bold red]{stats.FailedSubAgents}[/]");
        agentTable.AddRow("[dim]  Timed-out[/]", $"[bold yellow]{stats.TimedOutSubAgents}[/]");

        var procTable = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn("").Width(22))
            .AddColumn(new TableColumn(""));

        procTable.AddRow("[dim]Total processed[/]", pStats.TotalProcessed.ToString());
        procTable.AddRow("[dim]Total failed[/]", $"[bold red]{pStats.TotalFailed}[/]");
        procTable.AddRow("[dim]Active commands[/]", pStats.ActiveCommands.ToString());
        procTable.AddRow("[dim]Queued commands[/]", pStats.QueuedCommands.ToString());
        procTable.AddRow("[dim]Uptime[/]", $"[dodgerblue1]{pStats.Uptime:hh\\:mm\\:ss}[/]");

        AnsiConsole.Write(new Panel(agentTable)
        {
            Header = new PanelHeader("[bold] Sub-Agent Statistics [/]", Justify.Left),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("blue"),
            Padding = new Padding(1, 0),
        });

        AnsiConsole.Write(new Panel(procTable)
        {
            Header = new PanelHeader("[bold] Command Processor [/]", Justify.Left),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("blue"),
            Padding = new Padding(1, 0),
        });

        AnsiConsole.WriteLine();
    }

    static void HandleAgentPause(SubAgentManager manager, string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            AnsiConsole.MarkupLine("[dim]Usage:[/] [bold white]agents pause <runId>[/]");
            return;
        }

        if (manager.PauseSubAgent(runId))
            AnsiConsole.MarkupLine($"[bold yellow]⏸[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] paused.");
        else
            AnsiConsole.MarkupLine($"[bold red]✗[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] not found or already in a terminal state.");
        AnsiConsole.WriteLine();
    }

    static void HandleAgentResume(SubAgentManager manager, string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            AnsiConsole.MarkupLine("[dim]Usage:[/] [bold white]agents resume <runId>[/]");
            return;
        }

        if (manager.ResumeSubAgent(runId))
            AnsiConsole.MarkupLine($"[bold green]▶[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] resumed.");
        else
            AnsiConsole.MarkupLine($"[bold red]✗[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] not found or not in Paused state.");
        AnsiConsole.WriteLine();
    }

    static async Task HandleAgentStopAsync(SubAgentManager manager, string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            AnsiConsole.MarkupLine("[dim]Usage:[/] [bold white]agents stop <runId>[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[dim]Stopping sub-agent [bold white]{Markup.Escape(runId)}[/]...[/]");
        var ok = await manager.StopSubAgentAsync(runId);
        AnsiConsole.MarkupLine(ok
            ? $"[bold green]✓[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] stopped."
            : $"[bold red]✗[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] not found.");
        AnsiConsole.WriteLine();
    }

    static void HandleAgentKill(SubAgentManager manager, string runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var active = manager.GetActiveSubAgents().ToList();
            if (active.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No active sub-agents to kill.[/]");
                AnsiConsole.WriteLine();
                return;
            }

            AnsiConsole.MarkupLine($"[bold red]✗[/]  Killing {active.Count} sub-agent(s)...");
            foreach (var t in active)
                manager.KillSubAgent(t.RunId);
            AnsiConsole.MarkupLine("[bold green]Done.[/]");
        }
        else
        {
            if (manager.KillSubAgent(runId))
                AnsiConsole.MarkupLine($"[bold red]✗[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] killed.");
            else
                AnsiConsole.MarkupLine($"[bold red]✗[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] not found.");
        }
        AnsiConsole.WriteLine();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Startup session recovery
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// On startup, handles sessions that were Active when the previous process terminated:
    /// - Sub-agent sessions: marked Aborted (their in-flight work is lost).
    /// - Console session: if the last persisted message is a user message with no response,
    ///   the user is offered the option to re-queue it for processing.
    /// </summary>
    static async Task RecoverInterruptedSessionsAsync(
        SessionManager sessionManager,
        MarkdownSessionStore conversationStore,
        ICommandQueue commandQueue,
        FoxAgent agent,
        string consoleSessionId,
        IReadOnlyList<AgentFox.Sessions.SessionInfo> interrupted)
    {
        if (interrupted.Count == 0) return;

        var subAgentSessions = interrupted
            .Where(s => s.Origin == AgentFox.Sessions.SessionOrigin.SubAgent)
            .ToList();

        var channelSessions = interrupted
            .Where(s => s.Origin == AgentFox.Sessions.SessionOrigin.Channel)
            .ToList();

        // ── Sub-agent sessions: work is irrecoverable, mark aborted ──────────
        if (subAgentSessions.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold yellow]⚠[/]  {subAgentSessions.Count} sub-agent session(s) were interrupted by the previous process exit:");
            foreach (var s in subAgentSessions)
            {
                var age = (DateTime.UtcNow - s.LastActivityAt).TotalSeconds < 60
                    ? $"{(int)(DateTime.UtcNow - s.LastActivityAt).TotalSeconds}s ago"
                    : s.LastActivityAt.ToString("g");
                AnsiConsole.MarkupLine($"   [dim]• {Markup.Escape(s.SessionId)}  (last active: {age})[/]");
                sessionManager.MarkAborted(s.SessionId, "interrupted by process restart");
            }
            AnsiConsole.MarkupLine("   [dim]→ Marked as aborted. In-flight sub-agent work cannot be recovered.[/]");
            AnsiConsole.WriteLine();
        }

        // ── Channel sessions: log a warning (channel adapters will reconnect) ─
        if (channelSessions.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold yellow]⚠[/]  {channelSessions.Count} channel session(s) were active when the previous process exited.");
            AnsiConsole.MarkupLine("   [dim]→ Channel connections will be re-established; any in-flight message responses were lost.[/]");
            AnsiConsole.WriteLine();
        }

        // ── Console session: offer to re-run any unanswered user message ──────
        var consoleWasInterrupted = interrupted.Any(s => s.SessionId == consoleSessionId);
        if (!consoleWasInterrupted) return;

        var unprocessed = conversationStore.GetLastUnrespondedUserMessage(consoleSessionId);
        if (unprocessed == null) return;

        var preview = unprocessed.Length > 120 ? unprocessed[..120] + "…" : unprocessed;

        AnsiConsole.Write(new Panel(
            new Markup($"[italic]{Markup.Escape(preview)}[/]"))
        {
            Header = new PanelHeader("[bold yellow] ⚠ Previous session interrupted [/]", Justify.Left),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("yellow"),
            Padding = new Padding(1, 0),
        });

        AnsiConsole.Markup("[dim]Resume this task?[/] [bold](y/N):[/] ");
        var answer = Console.ReadLine()?.Trim();
        AnsiConsole.WriteLine();

        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[dim]Task skipped — you can re-enter it manually.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        AnsiConsole.MarkupLine("[dim]Re-queuing interrupted task...[/]");
        AnsiConsole.WriteLine();

        var tcs = new TaskCompletionSource<AgentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cmd = AgentCommand.CreateMainCommand(consoleSessionId, agent.Id, unprocessed);
        cmd.ResultSource = tcs;
        commandQueue.Enqueue(cmd);

        var result = await tcs.Task;
        AnsiConsole.WriteLine(result.Output);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Reads a (potentially multi-line) input from the console.
    /// <list type="bullet">
    ///   <item><b>Enter</b> — submits when no paste is in progress (KeyAvailable is false).</item>
    ///   <item><b>Shift+Enter</b> — always inserts a newline without submitting.</item>
    ///   <item><b>Paste</b> — newlines inside pasted text are detected via <see cref="Console.KeyAvailable"/>
    ///         (more characters waiting) and treated as line breaks rather than submit.</item>
    /// </list>
    /// </summary>
    static string ReadMultilineInput()
    {
        const string prompt = "\x1b[1;38;5;33m>\x1b[0m "; // bold dodgerblue1 ">" + space
        const string continuation = "  ";                   // aligns with text after "> "

        Console.Write(prompt);

        var lines = new List<string>();
        var current = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            // ── Submit ────────────────────────────────────────────────────────
            // Plain Enter with nothing more in the buffer = user pressed Enter to send.
            if (key.Key == ConsoleKey.Enter
                && !key.Modifiers.HasFlag(ConsoleModifiers.Shift)
                && !Console.KeyAvailable)
            {
                Console.WriteLine();
                lines.Add(current.ToString());
                return string.Join("\n", lines);
            }

            // ── New line ──────────────────────────────────────────────────────
            // Either Shift+Enter (explicit multiline) or Enter with more characters
            // waiting in the buffer (mid-paste newline).
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                Console.Write(continuation);
                lines.Add(current.ToString());
                current.Clear();
                continue;
            }

            // ── Backspace ─────────────────────────────────────────────────────
            if (key.Key == ConsoleKey.Backspace)
            {
                if (current.Length > 0)
                {
                    current.Remove(current.Length - 1, 1);
                    Console.Write("\b \b");
                }
                continue;
            }

            // ── Printable character ───────────────────────────────────────────
            if (key.KeyChar != '\0')
            {
                current.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
    }
}
