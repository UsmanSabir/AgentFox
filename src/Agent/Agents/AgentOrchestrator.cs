using AgentFox.Channels;
using AgentFox.LLM;
using AgentFox.MCP;
using AgentFox.Memory;
using AgentFox.Models;
using AgentFox.Plugins.Interfaces;
using AgentFox.Runtime;
using AgentFox.Runtime.Services;
using AgentFox.Sessions;
using AgentFox.Skills;
using AgentFox.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using SystemPromptBuilder = AgentFox.LLM.SystemPromptBuilder;

namespace AgentFox.Agents;

/// <summary>
/// Hosted service that owns the lifecycle of the main <see cref="FoxAgent"/>,
/// the <see cref="CommandProcessor"/>, and all channel connections.
/// <para>
/// Runs in every mode except single-shot command mode (<c>RunCommandLineMode</c>).
/// This ensures the agent, sub-agent infrastructure, and command processing are
/// available to all modules (CLI REPL, Web /chat, Webhooks) without any module
/// being responsible for initialization.
/// </para>
/// </summary>
public sealed class AgentOrchestrator : IHostedService
{
    private readonly IChatClient _chatClient;
    private readonly ToolRegistry _toolRegistry;
    private readonly SkillRegistry _skillRegistry;
    private readonly McpManager _mcpManager;
    private readonly HybridMemory _memory;
    private readonly SessionManager _sessionManager;
    private readonly SubAgentManager _subAgentManager;
    private readonly CommandProcessor _commandProcessor;
    private readonly ICommandQueue _commandQueue;
    private readonly WorkspaceManager _workspaceManager;
    private readonly IConfiguration _configuration;
    private readonly IAgentRuntime _agentRuntime;
    private readonly MarkdownSessionStore _sessionStore;
    private readonly FoxAgentHolder _agentHolder;
    private readonly ChannelManagerHolder _channelManagerHolder;
    private readonly SchedulingHolder _schedulingHolder;
    private readonly IEnumerable<IAppModule> _modules;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentOrchestrator> _logger;

    // Built during InitializeAsync, used by StopAsync
    private ChannelManager? _channelManager;
    private string? _systemPrompt;
    private HeartbeatManager? _heartbeatManager;
    private HeartbeatService? _heartbeatService;
    private CronScheduler? _cronScheduler;

    public AgentOrchestrator(
        IChatClient chatClient,
        ToolRegistry toolRegistry,
        SkillRegistry skillRegistry,
        McpManager mcpManager,
        HybridMemory memory,
        SessionManager sessionManager,
        SubAgentManager subAgentManager,
        CommandProcessor commandProcessor,
        ICommandQueue commandQueue,
        WorkspaceManager workspaceManager,
        IConfiguration configuration,
        IAgentRuntime agentRuntime,
        MarkdownSessionStore sessionStore,
        FoxAgentHolder agentHolder,
        ChannelManagerHolder channelManagerHolder,
        SchedulingHolder schedulingHolder,
        IEnumerable<IAppModule> modules,
        ILoggerFactory loggerFactory,
        ILogger<AgentOrchestrator> logger)
    {
        _chatClient           = chatClient;
        _toolRegistry         = toolRegistry;
        _skillRegistry        = skillRegistry;
        _mcpManager           = mcpManager;
        _memory               = memory;
        _sessionManager       = sessionManager;
        _subAgentManager      = subAgentManager;
        _commandProcessor     = commandProcessor;
        _commandQueue         = commandQueue;
        _workspaceManager     = workspaceManager;
        _configuration        = configuration;
        _agentRuntime         = agentRuntime;
        _sessionStore         = sessionStore;
        _agentHolder          = agentHolder;
        _channelManagerHolder = channelManagerHolder;
        _schedulingHolder     = schedulingHolder;
        _modules              = modules;
        _loggerFactory        = loggerFactory;
        _logger               = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IHostedService
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires off initialization in the background so the host starts quickly.
    /// The agent becomes available via <see cref="FoxAgentHolder"/> once ready.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Run without awaiting so the host can finish startup concurrently.
        _ = Task.Run(() => InitializeAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channelManager != null)
        {
            try { await _channelManager.DisconnectAllAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disconnecting channels during shutdown."); }
        }

        _cronScheduler?.Stop();
        _cronScheduler?.Dispose();
        _heartbeatManager?.Stop();
        _heartbeatService?.Dispose();
        _heartbeatManager?.Dispose();

        try { await _commandProcessor.StopAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error stopping command processor during shutdown."); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Core initialization
    // ─────────────────────────────────────────────────────────────────────────

    private async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            var manifests      = _skillRegistry.GetSkillManifests();
            var appConfigPath  = ResolveAppSettingsPath();

            // ── Register runtime tools ────────────────────────────────────────
            var toolsConfig = _configuration.GetSection("Tools").Get<ToolsConfig>() ?? new ToolsConfig();

            FoxAgent? agentRef = null;
            SpawnBackgroundSubAgentTool? spawnBgTool = null;

            if (toolsConfig.SubAgent)
            {
                var spawnSubAgentTool = new SpawnSubAgentTool(() => agentRef!);
                _toolRegistry.Register(spawnSubAgentTool);

                spawnBgTool = new SpawnBackgroundSubAgentTool(
                    _subAgentManager,
                    logger: _loggerFactory.CreateLogger<SpawnBackgroundSubAgentTool>());
                _toolRegistry.Register(spawnBgTool);
            }

            if (toolsConfig.Mcp)
                _toolRegistry.Register(new ManageMCPTool(
                    _mcpManager, appConfigPath,
                    _loggerFactory.CreateLogger<ManageMCPTool>()));

            // Scheduling tools use lazy refs — managers are created after the agent is built
            if (toolsConfig.Scheduling)
            {
                _toolRegistry.Register(new ManageHeartbeatTool(() => _heartbeatManager!));
                _toolRegistry.Register(new ManageCronTool(() => _cronScheduler!));
            }

            // ── Create ChannelManager early with lazy agent ref ───────────────
            // The lazy ref resolves once the agent is built below. Any messages that
            // arrive before the agent is ready are dropped by the null guard in HandleMessage.
            _channelManager = new ChannelManager(
                () => agentRef,
                _sessionManager, _commandQueue,
                _loggerFactory.CreateLogger<ChannelManager>());

            // ── Connect channels BEFORE registering tools and building the prompt ─
            // This ensures SendToChannelTool.Description (which calls CurrentChannelNames()
            // live) shows the actual configured channels in the system prompt and in the
            // tool's parameter enum, rather than "none".
            await ConnectChannelsFromConfigAsync(_channelManager, ct);

            // ── Register channel tools BEFORE building system prompt ──────────
            if (toolsConfig.Channels)
            {
                _toolRegistry.Register(new SendToChannelTool(
                    _channelManager, _loggerFactory.CreateLogger<SendToChannelTool>()));
                _toolRegistry.Register(new ManageChannelTool(
                    _channelManager, appConfigPath, _loggerFactory.CreateLogger<ManageChannelTool>()));
                _toolRegistry.Register(new NotifyUserTool(
                    _channelManager, _loggerFactory.CreateLogger<NotifyUserTool>()));
            }

            // ── Build system prompt (includes channel tools with live channel list) ──
            _systemPrompt = new SystemPromptBuilder()
                .WithPersona(SystemPromptConfig.AgentPrompts.DeveloperAssistant)
                .WithAllTools(_toolRegistry)
                .WithSkillsIndex(manifests)
                .WithExecutionContext(
                    "You are running in interactive mode and can help with:\n" +
                    "- Code development and debugging\n" +
                    "- File system operations\n" +
                    "- System administration\n" +
                    "- Architecture and design consultation\n" +
                    "- Composio.dev integrations (GitHub, Slack, Jira, etc.)\n" +
                    "- Git, Docker, deployment, testing, and more via skills")
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
                    "For Composio integrations, provide clear examples and documentation on usage",
                    "Use notify_user to send alerts, summaries, cron job results, or any message intended for the user — it delivers to all connected channels automatically.")
                .Build();

            // ── Build agent ───────────────────────────────────────────────────
            var agent = BuildAgent(_systemPrompt, withLogger: true);
            agentRef = agent;  // lazy ref in ChannelManager and SpawnSubAgentTool now resolves

            // ── Register ChannelContributor so runtime channel changes (add/remove
            //    via manage_channel) are reflected in every subsequent LLM call ────
            if (toolsConfig.Channels)
                agent.PromptContributors.Add(new ChannelContributor(_channelManager));

            // ── Create scheduling infrastructure (if enabled) ─────────────────
            if (toolsConfig.Scheduling)
            {
                var schedulingDir = Path.Combine(_workspaceManager.ResolvePath(""), "scheduling");
                _heartbeatManager = new HeartbeatManager(
                    agent,
                    beatFilePath: Path.Combine(schedulingDir, "heartbeats.md"),
                    sessionManager: _sessionManager,
                    commandQueue: _commandQueue);
                _heartbeatService = new HeartbeatService(
                    _heartbeatManager,
                    logger: _loggerFactory.CreateLogger<HeartbeatService>());
                _heartbeatManager.Start();

                _cronScheduler = new CronScheduler(
                    agent,
                    jobsFilePath: Path.Combine(schedulingDir, "cron.md"),
                    sessionManager: _sessionManager,
                    commandQueue: _commandQueue);
                _cronScheduler.Start();

                // Expose managers to DI consumers (e.g. WebModule scheduling endpoints)
                _schedulingHolder.Publish(_heartbeatManager, _cronScheduler);
            }

            // ── Publish agent to holder (unlocks FoxAgentService for web /chat) ─
            _agentHolder.Publish(agent);

            // ── Upgrade runtime executor (enables sub-agent model overrides) ──
            _agentRuntime.SetExecutor(new FoxAgentExecutor(
                defaultAgent:  agent,
                agentFactory:  client => BuildAgentWithClient(_systemPrompt, client),
                modelResolver: model  => LLMFactory.CreateWithModelOverride(_configuration, model),
                logger:        _loggerFactory.CreateLogger<FoxAgentExecutor>()));

            // ── Notify agent-aware plugins ────────────────────────────────────
            await NotifyAgentAwareModulesAsync(agent, ct);

            // ── Initialise background-spawn tool with console session ─────────
            var consoleSessionId = _sessionManager.GetOrCreateConsoleSession(agent.Id);
            spawnBgTool?.Initialize(
                parentAgentId:    agent.Id,
                parentSessionKey: consoleSessionId,
                parentSpawnDepth: 0);

            // ── Publish channel manager (unlocks WebhookModule) ───────────────
            _channelManagerHolder.Publish(_channelManager);

            // ── Wire command processor ────────────────────────────────────────
            RegisterCommandHandlers(agent);
            _commandProcessor.Start();

            if (_channelManager.Channels.Count > 0)
                _logger.LogInformation("{Count} channel(s) connected.", _channelManager.Channels.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AgentOrchestrator initialization failed.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Agent builder helpers
    // ─────────────────────────────────────────────────────────────────────────

    private FoxAgent BuildAgent(string systemPrompt, bool withLogger = false) =>
        BuildAgentWithClient(systemPrompt, _chatClient, withLogger);

    private FoxAgent BuildAgentWithClient(string systemPrompt, IChatClient client, bool withLogger = false)
    {
        var builder = new AgentBuilder(_toolRegistry)
            .WithName("AgentFox")
            .WithSystemPrompt(systemPrompt)
            .WithMemory(_memory)
            .WithSkillsRegistry(_skillRegistry)
            .WithMcpManager(_mcpManager)
            .WithConversationStore(_sessionStore)
            .WithHistoryProvider(_sessionStore.HistoryProvider)
            .WithChatClient(client)
            .WithWorkspaceManager(_workspaceManager)
            .WithSessionManager(_sessionManager)
            .WithCompactionFromConfig(_configuration);

        if (withLogger)
            builder = builder.WithLogger(_loggerFactory.CreateLogger<FoxAgent>());

        return builder.Build();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Plugin notification
    // ─────────────────────────────────────────────────────────────────────────

    private async Task NotifyAgentAwareModulesAsync(FoxAgent agent, CancellationToken ct)
    {
        var awareModules = _modules.OfType<IAgentAwareModule>().ToList();
        if (awareModules.Count == 0) return;

        var context = new PluginContextAdapter(
            _toolRegistry,
            agent.PromptContributors,
            _sessionStore);

        foreach (var m in awareModules)
        {
            try
            {
                await m.OnAgentReadyAsync(context);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin {Module}.OnAgentReadyAsync threw an exception.", m.Name);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Command processor wiring
    // ─────────────────────────────────────────────────────────────────────────

    private void RegisterCommandHandlers(FoxAgent agent)
    {
        var isInteractive = !Console.IsInputRedirected;

        // Sub-agent lane: execute spawned sub-agents
        _commandProcessor.RegisterLaneHandler(CommandLane.Subagent, async (command, ct) =>
        {
            if (command is not AgentCommand agentCmd) return;

            var runId   = agentCmd.RunId;
            var subTask = _subAgentManager.GetSubAgentTask(runId);

            if (subTask != null)
                await subTask.PauseGate.WhenResumedAsync(ct);

            _subAgentManager.OnSubAgentStarted(runId);

            using var linked = subTask != null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct, subTask.CancellationTokenSource.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                var subResult = await _agentRuntime.ExecuteAsync(agentCmd, linked.Token);
                var completion = subResult.Success
                    ? SubAgentCompletionResult.Success(subResult.Output)
                    : SubAgentCompletionResult.Failure(subResult.Error ?? "Sub-agent returned no output");
                _subAgentManager.OnSubAgentCompleted(runId, completion);
            }
            catch (OperationCanceledException)
            {
                _subAgentManager.OnSubAgentCompleted(runId, SubAgentCompletionResult.Cancelled());
            }
            catch (Exception ex)
            {
                _subAgentManager.OnSubAgentCompleted(runId, SubAgentCompletionResult.Failure(ex.Message));
            }
        });

        // Main lane: execute agent turns + deliver sub-agent result announcements
        _commandProcessor.RegisterLaneHandler(CommandLane.Main, async (command, ct) =>
        {
            if (command is AgentCommand agentCmd)
            {
                AgentResult result;
                try
                {
                    result = await _agentRuntime.ExecuteAsync(agentCmd, ct);
                }
                catch (Exception ex)
                {
                    result = new AgentResult { Success = false, Error = ex.Message };
                }
                agentCmd.ResultSource?.TrySetResult(result);
                return;
            }

            if (command is not ResultAnnouncementCommand announcement) return;

            // Route to channel if the request originated from one
            if (announcement.RequesterChannel != null && !announcement.SuppressChannelNotification)
            {
                try { await announcement.RequesterChannel.SendMessageAsync(announcement.FormatMessage()); }
                catch (Exception ex) { _logger.LogError(ex, "Channel send failed after sub-agent completion."); }
                return;
            }

            // Report back to a parent agent session
            if (!string.IsNullOrEmpty(announcement.ParentSessionKey))
            {
                var notification = $"[Background sub-agent result]\n{announcement.FormatMessage()}";

                if (isInteractive)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"[bold blue][[SUB-AGENT]][/] Reporting to parent agent [dim](session: {Markup.Escape(announcement.ParentSessionKey)})[/]...");
                }

                var notifyCmd = AgentCommand.CreateMainCommand(
                    announcement.ParentSessionKey, agentId: agent.Id, message: notification);
                try
                {
                    var parentResult = await _agentRuntime.ExecuteAsync(notifyCmd, ct);
                    if (isInteractive)
                    {
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine("[bold cyan][[AGENT]][/]");
                        AnsiConsole.WriteLine(parentResult.Output);
                        AnsiConsole.Markup("\n[bold dodgerblue1]>[/] ");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deliver sub-agent result to parent session.");
                    if (isInteractive)
                        AnsiConsole.MarkupLine($"[bold red][[ERR]][/] Failed to deliver sub-agent result: {Markup.Escape(ex.Message)}");
                }
                return;
            }

            // Local announcement (no channel, no parent) — log + console if interactive
            _logger.LogInformation("Sub-agent {Session} completed.", announcement.SessionKey);
            if (isInteractive)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[bold blue][[BG]][/] Sub-agent finished: {Markup.Escape(announcement.FormatMessage())}");
                AnsiConsole.Markup("\n[bold dodgerblue1]>[/] ");
            }
        });

        // Background sub-agent result callback
        _subAgentManager.RegisterResultCallback(async (task, result) =>
        {
            if (isInteractive)
                AnsiConsole.MarkupLine($"\n[bold blue][[BG]][/] Sub-agent [dim]{Markup.Escape(task.SessionKey)}[/] finished — status: [bold]{Markup.Escape(result.Status.ToString())}[/]");
            else
                _logger.LogInformation("Background sub-agent {Session} finished with status {Status}.", task.SessionKey, result.Status);

            if (task.OriginatingChannel != null)
                return ResultAnnouncementCommand.CreateChannelAnnouncement(
                    result, task.OriginatingChannel,
                    task.OriginatingMessageId ?? string.Empty,
                    task.CorrelationId,
                    task.OriginatingChannelId ?? string.Empty);

            if (!string.IsNullOrEmpty(task.ParentSessionKey))
                return ResultAnnouncementCommand.CreateParentAgentAnnouncement(
                    result, task.CorrelationId, task.ParentSessionKey, task.SessionKey);

            return ResultAnnouncementCommand.CreateLocalAnnouncement(result, task.CorrelationId, task.SessionKey);
        });

        // Tool lane: heartbeat management commands (Add/Remove/Pause/etc.) + parallel agent tasks
        _commandProcessor.RegisterLaneHandler(CommandLane.Tool, async (command, ct) =>
        {
            if (command is HeartbeatCommand hbCmd && _heartbeatService != null)
            {
                await _heartbeatService.ExecuteCommandAsync(hbCmd, ct);
                return;
            }
            if (command is AgentCommand agentCmd)
            {
                try
                {
                    var result = await _agentRuntime.ExecuteAsync(agentCmd, ct);
                    agentCmd.ResultSource?.TrySetResult(result);
                }
                catch (OperationCanceledException) { agentCmd.ResultSource?.TrySetCanceled(ct); }
                catch (Exception ex) { agentCmd.ResultSource?.TrySetException(ex); }
            }
        });

        // Background lane: scheduled agent tasks (HeartbeatManager/CronScheduler) + service pings
        _commandProcessor.RegisterLaneHandler(CommandLane.Background, async (command, ct) =>
        {
            if (command is AgentCommand agentCmd)
            {
                try
                {
                    var result = await _agentRuntime.ExecuteAsync(agentCmd, ct);
                    agentCmd.ResultSource?.TrySetResult(result);
                }
                catch (OperationCanceledException) { agentCmd.ResultSource?.TrySetCanceled(ct); }
                catch (Exception ex) { agentCmd.ResultSource?.TrySetException(ex); }
                return;
            }
            if (command is ServicePingCommand ping)
            {
                _logger.LogDebug("Service heartbeat ping received (session: {Session})", ping.SessionKey);
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Channel loading
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the "Channels" array from config and adds/connects every enabled entry.
    /// Each element must have a "Type" field (e.g. "Telegram", "Discord"). The same
    /// provider type can appear multiple times to support multiple bots/servers.
    /// </summary>
    private async Task ConnectChannelsFromConfigAsync(ChannelManager manager, CancellationToken ct)
    {
        var channelSection = _configuration.GetSection("Channels");
        if (!channelSection.Exists()) return;

        // Track per-type counts so we can log meaningful names when duplicates exist.
        var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in channelSection.GetChildren())
        {
            // Each child is one array element — read all its key/value pairs.
            var config = entry.GetChildren()
                .Where(c => c.Value != null)
                .ToDictionary(c => c.Key, c => c.Value!, StringComparer.OrdinalIgnoreCase);

            // "Type" is required; "Enabled" defaults to true if absent.
            if (!config.TryGetValue("Type", out var type) || string.IsNullOrWhiteSpace(type))
            {
                _logger.LogWarning("Channels[{Index}]: missing 'Type' — skipping.", entry.Key);
                continue;
            }

            if (config.TryGetValue("Enabled", out var enabledStr)
                && bool.TryParse(enabledStr, out var enabled)
                && !enabled)
            {
                _logger.LogDebug("Channels[{Index}] ({Type}): disabled — skipping.", entry.Key, type);
                continue;
            }

            // Inject workspace path for providers that need it (e.g. Telegram file downloads).
            config["WorkspacePath"] = _workspaceManager.ResolvePath("");

            typeCounts.TryGetValue(type, out var count);
            typeCounts[type] = count + 1;

            var label = count == 0 ? type : $"{type}#{count + 1}";

            var (ch, error) = ChannelFactory.Create(type, config, _logger);
            if (ch != null)
            {
                manager.AddChannel(ch);
                _logger.LogInformation("Channel '{Label}' registered.", label);
            }
            else
            {
                _logger.LogWarning("Channel '{Label}' failed to create: {Error}", label, error);
            }
        }

        if (manager.Channels.Count == 0) return;

        _logger.LogInformation("Connecting {Count} channel(s)...", manager.Channels.Count);
        await manager.ConnectAllAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string ResolveAppSettingsPath()
    {
        var cwdPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        return File.Exists(cwdPath) ? cwdPath : Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }
}
