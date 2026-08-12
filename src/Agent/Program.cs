using AgentFox.Agents;
using AgentFox.Doctor;
using AgentFox.Hitl;
using AgentFox.Doctor.Checks;
using AgentFox.Doctor.Onboarding;
using AgentFox.Helpers;
using AgentFox.LLM;
using AgentFox.Learning;
using AgentFox.MCP;
using AgentFox.Memory;
using AgentFox.Models;
using AgentFox.Modules.Loaders;
using AgentFox.Modules.Web;
using AgentFox.Channels;
using AgentFox.Plugins.Channels;
using AgentFox.Plugins.Interfaces;
using AgentFox.Runtime.Services;
using AgentFox.Sessions;
using AgentFox.Skills;
using AgentFox.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Text;
using Microsoft.Extensions.FileProviders;
using System.Reflection;
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

        if (args.Any(a => a.Equals("--version", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(VersionInfo.Full);
            return 0;
        }

        var appCfgPath = AppSettingsHelper.ResolveAppSettingsPath();
        if (ConfigMigrationCommand.TryRun(args, appCfgPath, out var configCommandExitCode))
            return configCommandExitCode;

        // Migrate before constructing the host: a breaking migration must not depend on the
        // new application being able to bind the old configuration shape.
        if (File.Exists(appCfgPath))
        {
            var migration = ConfigMigrator.Migrate(appCfgPath);
            if (!migration.Success)
            {
                Console.Error.WriteLine(migration.Message);
                return 1;
            }
        }
        
        // ── Service mode detection ────────────────────────────────────────────
        // Check if running in service mode before showing banner
        bool isServiceMode = ServiceHostMode.DetectServiceMode(args);
        
        // Show banner only if not in service mode
        if (!isServiceMode)
            ShowBanner();

        bool runDoctor    = args.Contains("--doctor");
        bool doctorFix    = args.Contains("--fix");
        // If appsettings.json doesn't exist, default to onboarding mode to guide the user through initial setup.
        // Never in service mode: a service has no console, so the wizard would block forever on
        // its first prompt and the SCM would kill the process with error 1053.
        bool runOnboarding = !isServiceMode &&
            (args.Contains("--onboarding") || !File.Exists(appCfgPath));

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
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Use a deterministic configuration stack. Release defaults are replaceable; the
        // user file is authoritative and never belongs to the release archive. Environment
        // variables and command-line switches remain the highest-priority providers.
        builder.Configuration.Sources.Clear();
        builder.Configuration
            .AddJsonFile(AppSettingsHelper.ResolveDefaultsPath(), optional: false, reloadOnChange: false)
            .AddJsonFile(appCfgPath, optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args);

        var configuration = builder.Configuration;

        // ── Windows Service / systemd lifetime ────────────────────────────────
        // The SCM starts the process and then waits for it to report SERVICE_RUNNING. A plain
        // web host never does that, so the service is killed after 30 s with error 1053
        // ("did not respond to the start request in a timely fashion"). AddWindowsService swaps
        // in the WindowsServiceLifetime that performs the handshake. It self-checks
        // WindowsServiceHelpers.IsWindowsService(), so it is inert for a normal console run.
        if (isServiceMode && OperatingSystem.IsWindows())
        {
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = configuration["Services:ServiceName"] ?? "AgentFox";
            });
        }

        // Bind the credential guard before anything can run a tool. It reads the composed
        // configuration to learn which values are live secrets, so it must come after the
        // configuration stack is complete and before the tool registry is built.
        AgentFox.Plugins.Security.SecretGuard.Initialize(configuration);

        builder.Services.AddManagementAuthentication(configuration);

        // Chat attachments ride inline as base64 in the /chat body, so a request can exceed
        // Kestrel's 30 MB default. Give it enough headroom that AttachmentSupport's own limits
        // are what a user hits — a precise 400 explaining the cap, not an opaque 413.
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64L * 1024 * 1024);

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
            var wizard     = new OnboardingWizard(appCfgPath);

            // Command mode: any LLM named args present alongside --onboarding
            bool commandMode = args.Any(a =>
                a.Equals("--provider",  StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--model",     StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--apikey",    StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--api-key",   StringComparison.OrdinalIgnoreCase));

            if (commandMode)
            {
                await wizard.RunCommandModeAsync(args);
                return 0;
            }

            var onboarding = await wizard.RunInteractiveModeAsync();
            if (!onboarding.StartAgentNow)
                return 0;

            // Continue into normal startup with the settings the wizard just wrote.
            builder.Configuration.AddJsonFile(appCfgPath, optional: true, reloadOnChange: true);
            AnsiConsole.MarkupLine("[bold green]✓[/] Setup complete — starting AgentFox...");
            AnsiConsole.WriteLine();
        }

        // ── Pre-build async services ──────────────────────────────────────────
        // These need async init (Composio, MCP) so they are created before the host
        // and then registered as already-constructed singletons.
        var workspaceManager = new WorkspaceManager(configuration);
        var memoryPolicy     = new MemoryAccessPolicy(configuration, workspaceManager);
        var toolsConfig      = configuration.GetSection("Tools").Get<ToolsConfig>() ?? new ToolsConfig();
        var toolRegistry     = CreateToolRegistry(workspaceManager, toolsConfig, configuration);
        SkillRegistry? skillRegistry = null;
        McpManager?    mcpManager    = null;
        HybridMemory?  memory        = null;
        RoutedMemory?  agentMemory   = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots12)
            .SpinnerStyle(Style.Parse("dodgerblue1 bold"))
            .StartAsync("[bold]Initializing AgentFox[/] [dim]— loading tools, memory & integrations...[/]",
                async ctx =>
                {
                    ctx.Status("[dodgerblue1]Registering tools & workspace...[/]");
                    skillRegistry = await CreateSkillRegistryAsync(toolRegistry, configuration);
                    mcpManager    = await CreateAndInitializeMcpManagerAsync(configuration);

                    var longTermMemory = MemoryBackendFactory.CreateLongTermStorage(configuration, workspaceManager);
                    memory = new HybridMemory(100, longTermMemory);
                    agentMemory = new RoutedMemory(memory, memoryPolicy, "main");

                    if (toolsConfig.Memory)
                    {
                        if (toolsConfig.IsEnabled("add_memory"))      toolRegistry.Register(new AddMemoryTool(agentMemory));
                        if (toolsConfig.IsEnabled("search_memory"))   toolRegistry.Register(new SearchMemoryTool(agentMemory));
                        if (toolsConfig.IsEnabled("get_all_memories")) toolRegistry.Register(new GetAllMemoriesTool(agentMemory));
                    }
                    ctx.Status("[green]Ready.[/]");
                });

        AnsiConsole.MarkupLine("[bold green]✓[/] AgentFox initialized successfully.");
        AnsiConsole.WriteLine();

        // ── First-run setup: local embedding model missing ───────────────────
        // The single-file exe degrades gracefully (vector search off) instead of
        // crashing; here we offer to download/restore the model interactively.
        // Skipped for --doctor (it has its own fix) and non-interactive sessions.
        if (!runDoctor && AnsiConsole.Profile.Capabilities.Interactive)
        {
            var embeddingProvider = EmbeddingServiceFactory.ResolveConfig(configuration)
                .Provider.Trim().ToLowerInvariant();
            if (embeddingProvider == "local" && !ModelSetup.IsAvailable())
            {
                AnsiConsole.MarkupLine("[yellow]⚠ The local embedding model is not set up[/] — vector search is disabled.");
                if (AnsiConsole.Confirm("Download / restore it now ([dim]~22 MB[/])?", defaultValue: true))
                {
                    if (await ModelSetup.EnsureAsync())
                        AnsiConsole.MarkupLine("[green]✓[/] Embedding model ready. [dim]Restart AgentFox to enable vector search.[/]");
                    else
                        AnsiConsole.MarkupLine("[dim]You can retry later with [bold]AgentFox doctor --fix[/].[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[dim]Skipped. Run [bold]AgentFox doctor --fix[/] anytime to set it up.[/]");
                }
                AnsiConsole.WriteLine();
            }
        }

        // ── --doctor mode (runs before web host, then exits) ──────────────────
        if (runDoctor)
        {
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
                new McpHealthCheck(mcpManager!, configuration, doctorAgent),
            });
            await doctorRunner.RunAsync(doctorFix);
            return 0;
        }

        // ── Single-shot command mode (runs before web host, then exits) ───────
        if (taskArgs.Length > 0)
            return await RunCommandLineMode(taskArgs, configuration, workspaceManager,
                toolRegistry, skillRegistry!, mcpManager!, agentMemory!, memoryPolicy);

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
        builder.Services.AddSingleton(mcpManager!);
        builder.Services.AddSingleton(memory!);
        builder.Services.AddSingleton(agentMemory!);
        builder.Services.AddSingleton(memoryPolicy);
        builder.Services.AddSingleton<IExperienceStore>(sp =>
            new JsonExperienceStore(Path.Combine(
                sp.GetRequiredService<WorkspaceManager>().ResolvePath(""),
                "learning", "experiences.json")));
        builder.Services.AddSingleton<ExperienceLearningService>();
        builder.Services.AddSingleton<IChannelProvider, TelegramChannelProvider>();
        builder.Services.AddSingleton<IChannelProvider, SlackChannelProvider>();
        builder.Services.AddSingleton<IChannelProvider, DiscordChannelProvider>();
        builder.Services.AddSingleton<IChannelProvider, TeamsChannelProvider>();
        builder.Services.AddSingleton<IChannelProvider, WhatsAppChannelProvider>();
        builder.Services.AddSingleton<ChannelProviderCatalog>();

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
            sp.GetRequiredService<WorkspaceManager>(),
            memoryPolicy: sp.GetRequiredService<MemoryAccessPolicy>()));
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

        // Agent holder + channel manager holder + scheduling holder + IAgentService (used by WebModule /chat)
        builder.Services.AddSingleton<PendingNotificationStore>();
        builder.Services.AddSingleton<ConversationEventBus>();
        builder.Services.AddSingleton<WebChatTurnCoordinator>();
        builder.Services.AddSingleton<SpecialistAgentRegistry>();
        builder.Services.AddSingleton<AgentFox.Plugins.Interfaces.IAgentRegistry>(sp =>
            sp.GetRequiredService<SpecialistAgentRegistry>());
        // Bound explicitly so HitlManager picks up the approval/question expiry settings. Without
        // it the manager falls back to its own defaults and the appsettings values are ignored.
        builder.Services.AddSingleton(
            configuration.GetSection("Hitl").Get<HitlConfig>() ?? new HitlConfig());
        builder.Services.AddSingleton<HitlManager>();
        builder.Services.AddSingleton<AgentFox.Planning.PlanStateStore>();

        // Optional HarnessAgent execution profile (roadmap Phase 0). Disabled by default —
        // the factory throws unless Harness:Enabled=true, so registering it is behaviour-neutral.
        builder.Services.Configure<AgentFox.Harness.HarnessOptions>(
            configuration.GetSection(AgentFox.Harness.HarnessOptions.SectionName));
        builder.Services.AddSingleton<AgentFox.Harness.HarnessAgentFactory>();
        builder.Services.AddSingleton<FoxAgentHolder>();
        builder.Services.AddSingleton<ChannelManagerHolder>();
        builder.Services.AddSingleton<SchedulingHolder>();
        builder.Services.AddSingleton<AgentFox.Plugins.Interfaces.IAgentService, FoxAgentService>();

        // Plugin session tracking and configuration management
        builder.Services.AddSingleton<AgentFox.Plugins.PluginSessionStore>();
        builder.Services.AddSingleton(sp =>
        {
            var workspaceDir = sp.GetRequiredService<WorkspaceManager>();
            var configDir = Path.Combine(workspaceDir.ResolvePath(""), "plugin-configs");
            var secretProtector = new AgentFox.Plugins.AesPluginSecretProtector(
                Path.Combine(configDir, ".plugin-secrets.key"),
                sp.GetRequiredService<ILogger<AgentFox.Plugins.AesPluginSecretProtector>>());
            return new AgentFox.Plugins.PluginConfigManager(
                configDir,
                sp.GetRequiredService<ILogger<AgentFox.Plugins.PluginConfigManager>>(),
                sp.GetServices<AgentFox.Plugins.IPluginConfigDefinitionProvider>(),
                secretProtector);
        });

        // AgentOrchestrator — builds the main agent, starts the command processor,
        // and connects channels. Runs in every mode (cli, web, api, service).
        builder.Services.AddHostedService<AgentOrchestrator>();

        // Service heartbeat (for periodic health checks when running as service)
        if (serviceCfg.Enabled && serviceCfg.HeartbeatIntervalSeconds > 0)
        {
            builder.Services.AddHostedService<ServiceHeartbeat>();
        }

        // ── Load modules ──────────────────────────────────────────────────────
        // All discovered modules (built-in + plugins) are ENABLED BY DEFAULT. Opt OUT specific
        // ones with a "DisabledModules" CSV (e.g. "web,webhook"). The legacy opt-in "Modules"
        // key is still honored for back-compat: if present, ONLY those are enabled.
        var disabledModules = (configuration["DisabledModules"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var legacyEnabledModules = configuration["Modules"]?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool IsModuleEnabled(string name) => legacyEnabledModules is { Count: > 0 }
            ? legacyEnabledModules.Contains(name)
            : !disabledModules.Contains(name);

        bool requiresWeb   = IsModuleEnabled("api") || IsModuleEnabled("web");
        var pluginDiscovery = LoadPluginsAndModules(builder);
        var modules         = pluginDiscovery.Modules;

        // Bind the HTTP listener to Services.Port (default 8080) when the web layer is active.
        // UseUrls writes the same "urls" configuration key the --urls switch does and is applied
        // later, so it would otherwise silently win over an explicit --urls; skip it when one was
        // supplied. For bare  dotnet run,  the single-file exe, or the Windows service, no --urls
        // is passed and the port comes from Services.Port in the configuration file.
        if (requiresWeb && string.IsNullOrWhiteSpace(configuration["urls"]))
            builder.WebHost.UseUrls($"http://*:{serviceCfg.Port}");

        // Expose ONLY enabled modules to DI consumers (AgentOrchestrator's OnAgentReadyAsync
        // notification, CliWorker). A disabled module must not receive lifecycle callbacks.
        var activeModules = modules.Where(m => IsModuleEnabled(m.Name)).ToList();
        builder.Services.AddSingleton<IEnumerable<IAppModule>>(activeModules);

        var enabledModuleAssemblies = new HashSet<Assembly>();

        foreach (var module in modules.Where(m => IsModuleEnabled(m.Name)))
        {
            module.RegisterServices(builder.Services, configuration);
            enabledModuleAssemblies.Add(module.GetType().Assembly);
        }

        // A plugin module that is explicitly disabled is skipped on purpose; tell the user so a
        // dropped-in plugin that does nothing isn't a mystery.
        var hostAssembly = typeof(Program).Assembly;
        var enableHint = legacyEnabledModules is { Count: > 0 }
            ? "remove it from \"DisabledModules\" or add it to \"Modules\""
            : "remove it from \"DisabledModules\"";
        foreach (var module in modules.Where(m =>
                     m.GetType().Assembly != hostAssembly && !IsModuleEnabled(m.Name)))
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]⚠ Plugin module '{module.Name}' is disabled[/] [dim]({enableHint} to enable).[/]");
        }

        // NOTE: Plugin tools are NOT registered into DI here. Nothing resolves IEnumerable<ITool>
        // from the container — the ToolRegistry is populated by direct Register() calls and by each
        // plugin's IAgentAwareModule.OnAgentReadyAsync -> IPluginContext.RegisterTool(...). Registering
        // the tool types as ITool singletons would only force ValidateOnBuild to eagerly construct them
        // (which fails for tools whose constructor pulls in plugin-versioned dependencies such as
        // Microsoft.Extensions.AI.Abstractions) without ever wiring them into the agent.
        foreach (var providerType in pluginDiscovery.ChannelProviderTypes.Where(t => enabledModuleAssemblies.Contains(t.Assembly)))
        {
            builder.Services.AddSingleton(typeof(IChannelProvider), providerType);
        }

        // ── Build and configure the web application ───────────────────────────
        var app = builder.Build();

        // Record the build version in the logs so it shows up in service mode too
        // (the console banner is suppressed when running as a Windows/systemd service).
        app.Services.GetService<ILogger<Program>>()?
            .LogInformation("AgentFox {Version}", VersionInfo.Full);

        if (requiresWeb)
        {
            // Serve wwwroot from embedded resources (single-file publish) or from disk (dev / regular publish).
            // When EmbeddedResource items are present in the .csproj, the manifest is baked into the assembly
            // and ManifestEmbeddedFileProvider serves them.  During development the wwwroot folder on disk is
            // used so you don't need to rebuild just to iterate on the frontend.
            //
            // Static files must be registered BEFORE UseRouting() so that requests for static files
            // short-circuit early and never reach the routing / endpoint middleware.
            var entryAssembly     = Assembly.GetEntryAssembly()!;
            var embeddedResources = entryAssembly.GetManifestResourceNames();
            bool hasEmbeddedWwwroot = embeddedResources.Any(n => n.Contains(".wwwroot."));
            ManifestEmbeddedFileProvider? embeddedProvider = null;

            IFileProvider? embeddedFileProvider = null;
            if (hasEmbeddedWwwroot)
            {
                embeddedFileProvider = new ManifestEmbeddedFileProvider(entryAssembly, "wwwroot");
                app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embeddedFileProvider });
                app.UseStaticFiles(new StaticFileOptions  { FileProvider = embeddedFileProvider });
            }
            else
            {
                app.UseDefaultFiles();
                app.UseStaticFiles();
            }

            // Plugin-supplied UI assets, served at /plugin-assets/{slug}/. Registered here — before
            // UseRouting, with the other static-file middleware — because these are static assets.
            //
            // Note the prefix is NOT /ext/{slug}: that is the host's own SPA route which frames the
            // page. Serving assets there instead would let this middleware answer /ext/{slug} with
            // the plugin's raw index.html, and the user would lose the AgentFox sidebar and header.
            var pluginUiPages = ResolvePluginUiPages(app);
            foreach (var page in pluginUiPages)
            {
                var assetPath = PluginUiPaths.AssetPathFor(page.Slug);
                app.UseDefaultFiles(new DefaultFilesOptions
                {
                    FileProvider = page.Assets,
                    RequestPath  = assetPath
                });
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = page.Assets,
                    RequestPath  = assetPath
                });
            }

            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();

            var apiGroup = app.MapGroup("/api").RequireAuthorization("ManagementViewer");
            foreach (var module in modules.Where(m => IsModuleEnabled(m.Name)))
                module.MapEndpoints(apiGroup);

            // Navigation manifest for the pages mounted above. The host frontend renders these
            // generically, so a plugin adds a page without any host-side route, type, or npm change.
            apiGroup.MapGet("/plugin-ui", (HttpContext http) => Results.Ok(
                pluginUiPages
                    .Where(p => http.User.IsInRole(p.RequiredRole))
                    .Select(p => new
                    {
                        slug        = p.Slug,
                        title       = p.Title,
                        icon        = p.Icon,
                        description = p.Description,
                        order       = p.Order,
                        // Where the host renders the page vs where its assets live — see PluginUiPaths.
                        path        = PluginUiPaths.PagePathFor(p.Slug),
                        entry       = $"{PluginUiPaths.AssetPathFor(p.Slug)}/{p.EntryPath.TrimStart('/')}"
                    })));

            // SPA fallback — all non-API routes resolve to index.html.
            // When serving from embedded resources, pass the same provider so the fallback
            // can locate index.html even without a physical wwwroot directory on disk.
            if (embeddedFileProvider != null)
                app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = embeddedFileProvider });
            else
            app.MapFallbackToFile("index.html");
        }

        // Notify modules of startup (IAppModule.StartAsync)
        foreach (var module in modules.Where(m => IsModuleEnabled(m.Name)))
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
        AnsiConsole.MarkupLineInterpolated($"[dim]  {VersionInfo.Display}[/]");
        AnsiConsole.WriteLine();
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
        McpManager mcpManager,
        IMemory memory,
        MemoryAccessPolicy memoryPolicy)
    {
        var sessionCfg = new SessionConfig();
        configuration.GetSection("Sessions").Bind(sessionCfg);
        var sessionManager = new SessionManager(
            sessionCfg,
            workspaceManager,
            memoryPolicy: memoryPolicy);
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

        FoxAgent? agentRef = null;
        SpawnBackgroundSubAgentTool? spawnBgTool = null;
        var toolsConfig = configuration.GetSection("Tools").Get<ToolsConfig>() ?? new ToolsConfig();
        if (toolsConfig.SubAgent)
        {
            var spawnTool = new SpawnSubAgentTool(() => agentRef!);
            toolRegistry.Register(spawnTool);
            spawnBgTool = new SpawnBackgroundSubAgentTool(subAgentManager);
            toolRegistry.Register(spawnBgTool);
            toolRegistry.Register(new CheckSubAgentStatusTool(subAgentManager));
        }

        var systemPrompt = new SystemPromptBuilder()
            .WithPersona(SystemPromptConfig.AgentPrompts.DeveloperAssistant)
            .WithAllTools(toolRegistry)
            .WithToolInstructions(false)
            .WithSkillsIndex(skillRegistry.GetSkillManifests())
            .WithConstraints(
                "Always verify changes before executing destructive operations",
                "Prioritize security and best practices",
                "Ask for clarification when requirements are ambiguous",
                "Use add_memory to save important user facts or preferences to long-term memory.",
                "Use search_memory to recall past information or facts when requested.",
                "Use get_all_memories to retrieve everything stored in long-term memory.",
                "Reply in the same language as the user's latest message unless the user asks for another language.")
            .Build();

        var chatClient = LLMFactory.CreateFromConfiguration(configuration);
        var agentBuilder = new AgentBuilder(toolRegistry)
            .WithName("AgentFox")
            .WithSystemPrompt(systemPrompt)
            .WithMemory(memory)
            .WithSkillsRegistry(skillRegistry)
            .WithMcpManager(mcpManager)
            .WithConversationStore(sessionStore)
            .WithHistoryProvider(sessionStore.HistoryProvider)
            .WithChatClient(chatClient)
            .WithWorkspaceManager(workspaceManager)
            .WithSessionManager(sessionManager)
            .WithCompactionFromConfig(configuration)
            .WithTodoPlannerFromConfig(configuration)
            .WithToolTimeout(
                TimeSpan.FromSeconds(toolsConfig.TimeoutSeconds),
                AgentFox.Tools.ToolTimeoutPolicy.ExemptTools);

        // No plan gate on this path, so the todo guidance is phase-independent.
        if (agentBuilder.IsTodoPlannerEnabled)
        {
            var todoRestores = new AgentFox.Planning.TodoRestoreTracker();
            agentBuilder = agentBuilder
                .WithTodoRestoreTracker(todoRestores)
                .WithPromptContributor(new AgentFox.Planning.TodoPlannerContributor(
                    store: null,
                    restores: todoRestores,
                    staleAfter: TimeSpan.FromHours(agentBuilder.TodoPlannerOptions!.StaleAfterHours)));
        }

        var agent = agentBuilder.Build();
        agentRef = agent;

        var cliSessionId = sessionManager.GetOrCreateConsoleSession(agent.Id);
        spawnBgTool?.Initialize(
            parentAgentId:    agent.Id,
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
    // Factory helpers
    // ─────────────────────────────────────────────────────────────────────────

    static ToolRegistry CreateToolRegistry(WorkspaceManager workspaceManager, ToolsConfig toolsConfig, IConfiguration configuration)
    {
        var registry = new ToolRegistry();

        if (toolsConfig.Shell && toolsConfig.IsEnabled("shell"))
            registry.Register(new ShellCommandTool(workspaceManager, toolsConfig.ShellTimeoutSeconds));

        if (toolsConfig.FileSystem)
        {
            if (toolsConfig.IsEnabled("read_file"))       registry.Register(new ReadFileTool(workspaceManager));
            if (toolsConfig.IsEnabled("write_file"))      registry.Register(new WriteFileTool(workspaceManager));
            if (toolsConfig.IsEnabled("list_files"))      registry.Register(new ListFilesTool(workspaceManager));
            if (toolsConfig.IsEnabled("search_files"))    registry.Register(new SearchFilesTool(workspaceManager));
            if (toolsConfig.IsEnabled("make_directory"))  registry.Register(new MakeDirectoryTool(workspaceManager));
            if (toolsConfig.IsEnabled("delete"))          registry.Register(new DeleteTool(workspaceManager));
        }

        if (toolsConfig.SystemInfo && toolsConfig.IsEnabled("get_env_info"))
            registry.Register(new GetEnvironmentInfoTool());

        if (toolsConfig.Web)
        {
            if (toolsConfig.IsEnabled("web_search")) registry.Register(new WebSearchTool(configuration));
            if (toolsConfig.IsEnabled("fetch_url"))  registry.Register(new FetchUrlTool());
        }

        if (toolsConfig.Utilities)
        {
            if (toolsConfig.IsEnabled("calculate"))  registry.Register(new CalculatorTool());
            if (toolsConfig.IsEnabled("uuid"))        registry.Register(new UuidTool());
            if (toolsConfig.IsEnabled("timestamp"))   registry.Register(new TimestampTool());
        }

        return registry;
    }

    static async Task<McpManager> CreateAndInitializeMcpManagerAsync(IConfiguration configuration)
    {
        var mcpManager = new McpManager();
        var servers = configuration.GetSection("MCP:Servers").Get<List<McpServerConfig>>() ?? [];

        // Only process servers that have a name and are not explicitly disabled.
        // IsEnabled returns true unless Enabled is explicitly set to false in config.
        // Absent "Enabled" key → null → treated as enabled (opt-out, not opt-in).
        var enabledServers = servers
            .Where(s => !string.IsNullOrWhiteSpace(s.Name) && s.IsEnabled)
            .ToList();

        foreach (var serverConfig in enabledServers)
        {
            try
            {
                var success = await mcpManager.AddServerAsync(serverConfig);
                if (!success)
                    AnsiConsole.MarkupLine(
                        $"[bold yellow]⚠[/]  MCP server [dim]{Markup.Escape(serverConfig.Name)}[/]: connection failed.");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"[bold yellow]⚠[/]  MCP server [dim]{Markup.Escape(serverConfig.Name)}[/]: {Markup.Escape(ex.Message)}");
            }
        }

        var skipped = servers.Count - enabledServers.Count;
        if (enabledServers.Count > 0 || skipped > 0)
        {
            var skippedNote = skipped > 0 ? $" [dim]({skipped} disabled)[/]" : "";
            AnsiConsole.MarkupLine($"[bold green]✓[/]  MCP: {enabledServers.Count} server(s) configured.{skippedNote}");
        }

        return mcpManager;
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

    /// <summary>
    /// Collects the plugin-supplied UI pages to mount under <c>/ext/{slug}</c>.
    ///
    /// <para>
    /// A bad slug is rejected rather than sanitized: it becomes a static-file request path, so
    /// accepting a slash or <c>..</c> would let a plugin mount assets outside its own prefix (or over
    /// the host's own routes). A duplicate slug is dropped for the same reason — the first
    /// registration wins and the collision is reported, because silently serving one plugin's assets
    /// from another's URL is worse than not serving them at all.
    /// </para>
    /// </summary>
    private static List<PluginUiPage> ResolvePluginUiPages(WebApplication app)
    {
        var pages = new List<PluginUiPage>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logger = app.Services.GetService<ILogger<Program>>();

        // Two sources, so a plugin can either implement the interface on its module (nothing to
        // register — the common case) or register a standalone contributor in DI. Reference-distinct
        // in case it does both; slug collisions are caught below either way.
        var contributors = app.Services.GetServices<IPluginUiContributor>()
            .Concat(app.Services.GetServices<IAppModule>().OfType<IPluginUiContributor>())
            .Distinct();

        foreach (var contributor in contributors)
        {
            IReadOnlyList<PluginUiPage> contributed;
            try
            {
                contributed = contributor.GetPages().ToList();
            }
            catch (Exception ex)
            {
                // A plugin whose UI fails to enumerate must not take the whole web layer down.
                logger?.LogWarning(ex, "Plugin UI contributor {Contributor} failed; skipping it.",
                    contributor.GetType().Name);
                continue;
            }

            foreach (var page in contributed)
            {
                if (!IsValidUiSlug(page.Slug))
                {
                    logger?.LogWarning(
                        "Plugin UI page '{Slug}' from {Contributor} was rejected: a slug must be a single "
                        + "path segment of lowercase letters, digits, or hyphens.",
                        page.Slug, contributor.GetType().Name);
                    continue;
                }

                if (!claimed.Add(page.Slug))
                {
                    logger?.LogWarning(
                        "Plugin UI slug '{Slug}' is already mounted; ignoring the copy from {Contributor}.",
                        page.Slug, contributor.GetType().Name);
                    continue;
                }

                pages.Add(page);
            }
        }

        pages.Sort((left, right) => left.Order != right.Order
            ? left.Order.CompareTo(right.Order)
            : string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase));

        if (pages.Count > 0)
            AnsiConsole.MarkupLineInterpolated(
                $"[green]✓[/] Mounted {pages.Count} plugin UI page(s): {string.Join(", ", pages.Select(p => PluginUiPaths.PagePathFor(p.Slug)))}");

        return pages;
    }

    private static bool IsValidUiSlug(string? slug) =>
        !string.IsNullOrWhiteSpace(slug)
        && slug.Length <= 40
        && char.IsAsciiLetterOrDigit(slug[0])
        && slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    private sealed record PluginDiscovery(
        List<IAppModule> Modules,
        List<Type> ChannelProviderTypes);

    private static PluginDiscovery LoadPluginsAndModules(WebApplicationBuilder builder)
    {
        var pluginFolder = Path.Combine(AppContext.BaseDirectory, "plugins");
        Directory.CreateDirectory(pluginFolder);

        // Create a temporary service provider for plugin module instantiation
        var tempServices = new ServiceCollection();
        tempServices.AddSingleton(builder.Configuration.GetSection("Plugins"));
        var tempProvider = tempServices.BuildServiceProvider();

        var pluginModules = new List<IAppModule>();
        var providerTypes = new List<Type>();
        var loadedPlugins = new List<string>();
        var skippedNoDeps = 0;

        // Load each plugin DLL exactly once into a single PluginLoadContext and pull the
        // module, tool and channel-provider types from that same assembly instance.
        //
        // This is critical: an AssemblyLoadContext produces a *distinct* Assembly object per
        // context even for the same file. If modules, tools and providers were discovered via
        // separate load contexts (as they were previously), then module.GetType().Assembly would
        // never equal toolType.Assembly, and the assembly-identity gating in Main that decides
        // which plugin tools/channels to register would silently drop them all.
        foreach (var dll in Directory.GetFiles(pluginFolder, "*.dll", SearchOption.AllDirectories))
        {
            // Skip non-plugin payloads that legitimately live under plugins/ — e.g. a Chromium
            // browser downloaded by a plugin into plugins/Chrome/... Those native .dll files are
            // not managed assemblies and have hundreds of entries; loading them is both pointless
            // and throws BadImageFormatException.
            if (dll.Contains($"{Path.DirectorySeparatorChar}Chrome{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                continue;

            // Only treat a DLL as a plugin entry assembly if it ships its own dependency manifest
            // ({name}.deps.json). The transitive dependency DLLs copied alongside a plugin
            // (PuppeteerSharp, WebDriverBiDi, Discord.*, …) have no deps.json and must NOT each get
            // their own load context: loaded standalone they become orphaned collectible contexts
            // that the GC unloads, after which a plugin's later attempt to load that same dependency
            // (e.g. PuppeteerSharp pulling in WebDriverBiDi at Puppeteer.LaunchAsync) throws
            // "An operation is not legal in the current state." Dependencies resolve correctly on
            // demand through the owning plugin's context via its AssemblyDependencyResolver.
            if (!File.Exists(Path.ChangeExtension(dll, ".deps.json")))
            {
                skippedNoDeps++;
                continue;
            }

            Assembly assembly;
            try
            {
                var context = new PluginLoadContext(dll);
                assembly = context.LoadFromAssemblyPath(dll);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
            {
                // Native DLL or otherwise not a loadable managed assembly — skip it.
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }

            foreach (var type in types.Where(t => t is { IsAbstract: false, IsInterface: false }))
            {
                if (typeof(IAppModule).IsAssignableFrom(type))
                    pluginModules.Add((IAppModule)ActivatorUtilities.CreateInstance(tempProvider, type)!);
                else if (typeof(IChannelProvider).IsAssignableFrom(type))
                    providerTypes.Add(type);
            }
            loadedPlugins.Add(Path.GetFileNameWithoutExtension(dll));
        }

        // Surface discovery results — this method was previously silent, so a mis-copied plugin
        // (just the .dll without its .deps.json + dependencies) looked like nothing happened.
        if (loadedPlugins.Count > 0)
            AnsiConsole.MarkupLineInterpolated(
                $"[green]✓[/] Loaded {loadedPlugins.Count} plugin(s): {string.Join(", ", loadedPlugins)}");
        else if (skippedNoDeps > 0)
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]⚠ Found {skippedNoDeps} DLL(s) under 'plugins' but none had a sibling '.deps.json', so none were loaded.[/]\n[dim]  Copy each plugin's entire publish output (DLL + its .deps.json + dependencies), not just the .dll.[/]");

        // Built-in modules (cli/web/webhook) live in the host assembly and are always available.
        var allModules = ModuleLoader.LoadModules();
        allModules.AddRange(pluginModules);

        return new PluginDiscovery(allModules, providerTypes);
    }
}
