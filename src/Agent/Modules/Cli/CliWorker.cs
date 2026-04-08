using AgentFox.Agents;
using AgentFox.Channels;
using AgentFox.Doctor;
using AgentFox.Doctor.Checks;
using AgentFox.LLM;
using AgentFox.MCP;
using SystemPromptBuilder = AgentFox.LLM.SystemPromptBuilder;
using AgentFox.Memory;
using AgentFox.Models;
using AgentFox.Plugins.Interfaces;
using AgentFox.Runtime.Services;
using AgentFox.Sessions;
using AgentFox.Skills;
using AgentFox.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text;
using System.Threading.Channels;
using AgentFox.Helpers;
using SysChannel = System.Threading.Channels.Channel;

namespace AgentFox.Modules.Cli;

/// <summary>
/// Hosted background service that owns the interactive CLI REPL.
/// <para>
/// On startup it:
/// <list type="bullet">
///   <item>Registers runtime tools (SpawnSubAgent, ManageMCP, channels)</item>
///   <item>Builds the <see cref="FoxAgent"/> and publishes it via <see cref="FoxAgentHolder"/></item>
///   <item>Notifies any <see cref="IAgentAwareModule"/> plugins via <c>OnAgentReadyAsync</c></item>
///   <item>Runs the interactive input loop until "exit" or cancellation</item>
/// </list>
/// </para>
/// </summary>
public sealed class CliWorker : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IChatClient _chatClient;
    private readonly ToolRegistry _toolRegistry;
    private readonly SkillRegistry _skillRegistry;
    private readonly MCPClient _mcpClient;
    private readonly HybridMemory _memory;
    private readonly SessionManager _sessionManager;
    private readonly SubAgentManager _subAgentManager;
    private readonly CommandProcessor _commandProcessor;
    private readonly ICommandQueue _commandQueue;
    private readonly WorkspaceManager _workspaceManager;
    private readonly UIConfig _uiConfig;
    private readonly IConfiguration _configuration;
    private readonly IAgentRuntime _agentRuntime;
    private readonly MarkdownSessionStore _sessionStore;
    private readonly FoxAgentHolder _agentHolder;
    private readonly IEnumerable<IAppModule> _modules;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CliWorker> _logger;
    private readonly ServiceConfig _serviceConfig;

    public CliWorker(
        IHostApplicationLifetime lifetime,
        IChatClient chatClient,
        ToolRegistry toolRegistry,
        SkillRegistry skillRegistry,
        MCPClient mcpClient,
        HybridMemory memory,
        SessionManager sessionManager,
        SubAgentManager subAgentManager,
        CommandProcessor commandProcessor,
        ICommandQueue commandQueue,
        WorkspaceManager workspaceManager,
        UIConfig uiConfig,
        IConfiguration configuration,
        IAgentRuntime agentRuntime,
        MarkdownSessionStore sessionStore,
        FoxAgentHolder agentHolder,
        IEnumerable<IAppModule> modules,
        ILoggerFactory loggerFactory,
        ILogger<CliWorker> logger,
        ServiceConfig serviceConfig)
    {
        _lifetime = lifetime;
        _chatClient = chatClient;
        _toolRegistry = toolRegistry;
        _skillRegistry = skillRegistry;
        _mcpClient = mcpClient;
        _memory = memory;
        _sessionManager = sessionManager;
        _subAgentManager = subAgentManager;
        _commandProcessor = commandProcessor;
        _commandQueue = commandQueue;
        _workspaceManager = workspaceManager;
        _uiConfig = uiConfig;
        _configuration = configuration;
        _agentRuntime = agentRuntime;
        _sessionStore = sessionStore;
        _agentHolder = agentHolder;
        _modules = modules;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _serviceConfig = serviceConfig;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BackgroundService entry point
    // ─────────────────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Console.IsInputRedirected)
            return;

        try
        {
            await RunInteractiveSessionAsync(stoppingToken);
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLI session terminated unexpectedly.");
            AnsiConsole.MarkupLine($"[bold red][ERR][/] CLI session terminated: {Markup.Escape(ex.Message)}");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Interactive session setup + REPL loop
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunInteractiveSessionAsync(CancellationToken ct)
    {
        var manifests = _skillRegistry.GetSkillManifests();
        AnsiConsole.MarkupLine($"[bold green]✓[/] [dim]{manifests.Count} skill(s) registered.[/]");
        AnsiConsole.WriteLine();

        // ── Register runtime tools ────────────────────────────────────────────
        FoxAgent? agentRef = null;
        var spawnSubAgentTool = new SpawnSubAgentTool(() => agentRef!);
        _toolRegistry.Register(spawnSubAgentTool);

        var spawnBgTool = new SpawnBackgroundSubAgentTool(
            _subAgentManager,
            logger: _loggerFactory.CreateLogger<SpawnBackgroundSubAgentTool>());
        _toolRegistry.Register(spawnBgTool);

        var appConfigPath = ResolveAppSettingsPath();
        _toolRegistry.Register(new ManageMCPTool(
            _mcpClient, appConfigPath,
            _loggerFactory.CreateLogger<ManageMCPTool>()));

        // ── Build system prompt ───────────────────────────────────────────────
        var systemPrompt = new SystemPromptBuilder()
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
                "For Composio integrations, provide clear examples and documentation on usage")
            .Build();

        // ── Build agent ───────────────────────────────────────────────────────
        var agent = BuildAgent(systemPrompt, withLogger: true);
        agentRef = agent;
        _agentHolder.Publish(agent);

        // ── Notify agent-aware plugins ────────────────────────────────────────
        await NotifyAgentAwareModulesAsync(agent, ct);

        // ── Upgrade runtime executor ──────────────────────────────────────────
        _agentRuntime.SetExecutor(new FoxAgentExecutor(
            defaultAgent: agent,
            agentFactory: client => BuildAgentWithClient(systemPrompt, client),
            modelResolver: model => LLMFactory.CreateWithModelOverride(_configuration, model),
            logger: _loggerFactory.CreateLogger<FoxAgentExecutor>()));

        // ── Load channels ─────────────────────────────────────────────────────
        var channelManager = await LoadChannelsFromConfigAsync(agent, ct);
        _toolRegistry.Register(new SendToChannelTool(
            channelManager, _loggerFactory.CreateLogger<SendToChannelTool>()));
        _toolRegistry.Register(new ManageChannelTool(
            channelManager, appConfigPath, _loggerFactory.CreateLogger<ManageChannelTool>()));

        if (channelManager.Channels.Count > 0)
            AnsiConsole.MarkupLine($"[bold green]✓[/]  {channelManager.Channels.Count} channel(s) connected.");
        else
            AnsiConsole.MarkupLine("[dim]No channels configured. Use manage_channel to add one at runtime.[/]");

        // ── Session recovery ──────────────────────────────────────────────────
        var interrupted = _sessionManager.GetInterruptedActiveSessions();
        var consoleSessionId = _sessionManager.GetOrCreateConsoleSession(agent.Id);
        spawnBgTool.Initialize(
            parentAgentId: agent.Id,
            parentSessionKey: consoleSessionId,
            parentSpawnDepth: 0);

        // ── Wire command processor ────────────────────────────────────────────
        RegisterCommandHandlers(agent, consoleSessionId);
        _commandProcessor.Start();

        await RecoverInterruptedSessionsAsync(agent, consoleSessionId, interrupted, ct);

        // ── DoctorAgent ───────────────────────────────────────────────────────
        var doctorAgent = new DoctorAgent(_chatClient, appConfigPath);

        AnsiConsole.MarkupLine("[dim]Type [bold white]help[/] for commands, [bold white]exit[/] to quit. [bold white]Shift+Enter[/] for multi-line input.[/]");
        AnsiConsole.WriteLine();

        // ── REPL loop ─────────────────────────────────────────────────────────
        while (!ct.IsCancellationRequested)
        {
            var input = ReadMultilineInput();
            if (string.IsNullOrWhiteSpace(input))
                continue;

            var handled = await HandleReplCommandAsync(
                input, agent, consoleSessionId, channelManager, doctorAgent, ct);

            if (handled == ReplAction.Exit)
            {
                AnsiConsole.MarkupLine("[bold green]Goodbye![/]");
                await channelManager.DisconnectAllAsync();
                await _commandProcessor.StopAsync(TimeSpan.FromSeconds(10));
                break;
            }

            if (handled != ReplAction.Unhandled)
                continue;

            // ── Route user turn through Main lane ─────────────────────────────
            await RunAgentTurnAsync(input, agent, consoleSessionId, ct);
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
            .WithMCPClient(_mcpClient)
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
        var awarenModules = _modules.OfType<IAgentAwareModule>().ToList();
        if (awarenModules.Count == 0) return;

        var context = new PluginContextAdapter(
            _toolRegistry,
            agent.PromptContributors,
            _sessionStore);

        foreach (var m in awarenModules)
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

    private void RegisterCommandHandlers(FoxAgent agent, string consoleSessionId)
    {
        // Sub-agent lane
        _commandProcessor.RegisterLaneHandler(CommandLane.Subagent, async (command, ct) =>
        {
            if (command is not AgentCommand agentCmd) return;

            var runId = agentCmd.RunId;
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

        // Main lane
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

            if (announcement.RequesterChannel != null && !announcement.SuppressChannelNotification)
            {
                try { await announcement.RequesterChannel.SendMessageAsync(announcement.FormatMessage()); }
                catch (Exception ex) { AnsiConsole.MarkupLine($"[bold red][[ERR]][/] Channel send failed: {Markup.Escape(ex.Message)}"); }
                return;
            }

            if (!string.IsNullOrEmpty(announcement.ParentSessionKey))
            {
                var notification = $"[Background sub-agent result]\n{announcement.FormatMessage()}";
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[bold blue][[SUB-AGENT]][/] Reporting to parent agent [dim](session: {Markup.Escape(announcement.ParentSessionKey)})[/]...");

                var notifyCmd = AgentCommand.CreateMainCommand(
                    announcement.ParentSessionKey, agentId: agent.Id, message: notification);
                try
                {
                    var parentResult = await _agentRuntime.ExecuteAsync(notifyCmd, ct);
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[bold cyan][[AGENT]][/]");
                    AnsiConsole.WriteLine(parentResult.Output);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[bold red][[ERR]][/] Failed to deliver sub-agent result: {Markup.Escape(ex.Message)}");
                }
                AnsiConsole.Markup("\n[bold dodgerblue1]>[/] ");
            }
        });

        // Background sub-agent result callback
        _subAgentManager.RegisterResultCallback(async (task, result) =>
        {
            AnsiConsole.MarkupLine($"\n[bold blue][[BG]][/] Sub-agent [dim]{Markup.Escape(task.SessionKey)}[/] finished — status: [bold]{Markup.Escape(result.Status.ToString())}[/]");

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
    }

    // ─────────────────────────────────────────────────────────────────────────
    // REPL command dispatcher
    // ─────────────────────────────────────────────────────────────────────────

    private enum ReplAction { Unhandled, Handled, Exit }

    private async Task<ReplAction> HandleReplCommandAsync(
        string input,
        FoxAgent agent,
        string consoleSessionId,
        ChannelManager channelManager,
        DoctorAgent doctorAgent,
        CancellationToken ct)
    {
        var trimmed = input.Trim();
        var lower = trimmed.ToLowerInvariant();

        switch (lower)
        {
            case "exit":
                return ReplAction.Exit;

            case "help" or "?":
                ShowHelp();
                return ReplAction.Handled;

            case "status":
                ShowStatus(agent);
                return ReplAction.Handled;

            case "tools":
                ShowTools();
                return ReplAction.Handled;

            case "skills":
                ShowSkills();
                return ReplAction.Handled;

            case "doctor":
            case "doctor fix":
            {
                await RunDoctorAsync(doctorAgent, lower == "doctor fix", ct);
                return ReplAction.Handled;
            }

            case "agents" or "agents list":
                ShowAgents();
                return ReplAction.Handled;

            case "agents stats":
                ShowAgentStats();
                return ReplAction.Handled;

            // ── Service commands ─────────────────────────────────────────────
            case "install-service":
            case "uninstall-service":
            case "start-service":
            case "stop-service":
            case "restart-service":
            case "service-status":
            case "service-config":
                await HandleServiceCommandAsync(lower, ct);
                return ReplAction.Handled;
        }

        if (lower.StartsWith("skill "))
        {
            ShowSkillDetail(trimmed[6..].Trim());
            return ReplAction.Handled;
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
                var doctorResult = await doctorAgent.ProcessRequestAsync(request);
                AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(doctorResult)}[/]");
            }
            return ReplAction.Handled;
        }

        if (lower.StartsWith("agents pause "))
        {
            HandleAgentPause(trimmed[13..].Trim());
            return ReplAction.Handled;
        }

        if (lower.StartsWith("agents resume "))
        {
            HandleAgentResume(trimmed[14..].Trim());
            return ReplAction.Handled;
        }

        if (lower.StartsWith("agents stop "))
        {
            await HandleAgentStopAsync(trimmed[12..].Trim());
            return ReplAction.Handled;
        }

        if (lower.StartsWith("agents kill"))
        {
            var target = lower.Length > 11 ? trimmed[11..].Trim() : "all";
            HandleAgentKill(target);
            return ReplAction.Handled;
        }

        return ReplAction.Unhandled;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Agent turn with streaming UI
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunAgentTurnAsync(
        string input,
        FoxAgent agent,
        string consoleSessionId,
        CancellationToken ct)
    {
        AnsiConsole.WriteLine();

        var tcs = new TaskCompletionSource<AgentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cmd = AgentCommand.CreateMainCommand(
            sessionKey: consoleSessionId,
            agentId: agent.Id,
            message: input);
        cmd.ResultSource = tcs;

        var streamChannel = SysChannel.CreateUnbounded<(bool IsReasoning, string Text)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var sb = new StringBuilder();
        var sbReasoning = new StringBuilder();
        Task<string>? liveDisplayTask = null;

        IRenderable BuildDisplay()
        {
            var responseText = sb.ToString();
            if (sbReasoning.Length == 0)
            {
                var table = new Table().NoBorder();
                table.AddColumn(new TableColumn(""));
                table.AddRow(Markup.Escape(responseText));
                return table;
            }
            var reasoningLines = sbReasoning.ToString().Split('\n');
            var visibleReasoning = reasoningLines.Length > 20
                ? string.Join('\n', reasoningLines[^20..])
                : sbReasoning.ToString();
            var thinkingPanel = new Panel(
                    new Markup($"[italic dim yellow]{Markup.Escape(visibleReasoning.TrimEnd('\n'))}[/]"))
                .Header("[dim yellow]Thinking...[/]")
                .BorderColor(Color.Yellow)
                .Expand();
            return sb.Length == 0 ? thinkingPanel : new Rows(thinkingPanel, new Text(sb.ToString()));
        }

        cmd.Streaming = new StreamingCallbacks
        {
            OnStart = () =>
            {
                if (!_uiConfig.RenderReasoning)
                {
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
                                    var chunk = sb.ToString();
                                    if (!string.IsNullOrWhiteSpace(chunk))
                                    {
                                        table.Rows.Clear();
                                        table.AddRow(Markup.Escape(chunk));
                                        ctx.Refresh();
                                    }
                                }
                            }
                            return string.Empty;
                        });
                }
                else
                {
                    liveDisplayTask = AnsiConsole.Live(new Text(string.Empty))
                        .AutoClear(false)
                        .Overflow(VerticalOverflow.Ellipsis)
                        .Cropping(VerticalOverflowCropping.Top)
                        .StartAsync(async ctx =>
                        {
                            await foreach (var (isReasoning, text) in streamChannel.Reader.ReadAllAsync())
                            {
                                if (isReasoning) sbReasoning.Append(text);
                                else sb.Append(text);
                                ctx.UpdateTarget(BuildDisplay());
                                ctx.Refresh();
                            }
                            return string.Empty;
                        });
                }
                return Task.CompletedTask;
            },
            OnReasoning = !_uiConfig.RenderReasoning ? null
                : async chunk => await streamChannel.Writer.WriteAsync((true, chunk)),
            OnToken = async chunk => await streamChannel.Writer.WriteAsync((false, chunk)),
            OnComplete = async () =>
            {
                streamChannel.Writer.TryComplete();
                if (liveDisplayTask != null)
                    await liveDisplayTask;
            },
        };

        _commandQueue.Enqueue(cmd);
        var result = await tcs.Task;

        AnsiConsole.WriteLine();

        if (result.SpawnedSubAgents.Count > 0)
            AnsiConsole.MarkupLine($"[bold blue]↳[/] Spawned [bold]{result.SpawnedSubAgents.Count}[/] sub-agent(s)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Doctor
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunDoctorAsync(DoctorAgent doctorAgent, bool autoFix, CancellationToken ct)
    {
        var workspacePath = _workspaceManager.ResolvePath("");
        var ltMemory = MemoryBackendFactory.CreateLongTermStorage(_configuration, _workspaceManager);
        var doctorRunner = new DoctorRunner(new IHealthCheckable[]
        {
            new ConfigHealthCheck(_configuration, doctorAgent),
            new LlmHealthCheck(_configuration),
            new EmbeddingHealthCheck(
                EmbeddingServiceFactory.Create(_configuration),
                ltMemory as SqliteLongTermMemory,
                _configuration),
            new MemoryHealthCheck(ltMemory, _configuration, workspacePath),
            new SessionHealthCheck(_configuration, workspacePath),
            new SkillHealthCheck(_skillRegistry),
            new ToolHealthCheck(_toolRegistry),
            new McpHealthCheck(_mcpClient, _configuration, doctorAgent),
        });
        await doctorRunner.RunAsync(autoFix);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Channel loading
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<ChannelManager> LoadChannelsFromConfigAsync(FoxAgent agent, CancellationToken ct)
    {
        var manager = new ChannelManager(
            agent, _sessionManager, _commandQueue,
            _loggerFactory.CreateLogger<ChannelManager>());

        var channelSection = _configuration.GetSection("Channels");
        if (!channelSection.Exists()) return manager;

        var tgSection = channelSection.GetSection("Telegram");
        if (tgSection.Exists() && tgSection.GetValue<bool>("Enabled"))
        {
            var token = tgSection["BotToken"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token) || token.Contains("your-telegram"))
            {
                AnsiConsole.MarkupLine("[bold yellow]⚠[/]  Telegram: BotToken not configured — skipping.");
            }
            else
            {
                var pollingTimeout = tgSection.GetValue<int>("PollingTimeoutSeconds", 30);
                var (ch, error) = ChannelFactory.Create("Telegram", new Dictionary<string, string>
                {
                    ["BotToken"] = token,
                    ["PollingTimeoutSeconds"] = pollingTimeout.ToString()
                });
                if (ch != null) manager.AddChannel(ch);
                else AnsiConsole.MarkupLine($"[bold yellow]⚠[/]  Telegram: {error}");
            }
        }

        if (manager.Channels.Count == 0) return manager;

        AnsiConsole.MarkupLine("[dim]Connecting channels...[/]");
        await manager.ConnectAllAsync();
        return manager;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Session recovery
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RecoverInterruptedSessionsAsync(
        FoxAgent agent,
        string consoleSessionId,
        IReadOnlyList<SessionInfo> interrupted,
        CancellationToken ct)
    {
        if (interrupted.Count == 0) return;

        var subAgentSessions = interrupted.Where(s => s.Origin == SessionOrigin.SubAgent).ToList();
        var channelSessions  = interrupted.Where(s => s.Origin == SessionOrigin.Channel).ToList();

        if (subAgentSessions.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold yellow]⚠[/]  {subAgentSessions.Count} sub-agent session(s) were interrupted:");
            foreach (var s in subAgentSessions)
            {
                var age = (DateTime.UtcNow - s.LastActivityAt).TotalSeconds < 60
                    ? $"{(int)(DateTime.UtcNow - s.LastActivityAt).TotalSeconds}s ago"
                    : s.LastActivityAt.ToString("g");
                AnsiConsole.MarkupLine($"   [dim]• {Markup.Escape(s.SessionId)}  (last active: {age})[/]");
                _sessionManager.MarkAborted(s.SessionId, "interrupted by process restart");
            }
            AnsiConsole.MarkupLine("   [dim]→ Marked as aborted.[/]");
            AnsiConsole.WriteLine();
        }

        if (channelSessions.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold yellow]⚠[/]  {channelSessions.Count} channel session(s) were active when the previous process exited.");
            AnsiConsole.MarkupLine("   [dim]→ Channel connections will be re-established.[/]");
            AnsiConsole.WriteLine();
        }

        var consoleWasInterrupted = interrupted.Any(s => s.SessionId == consoleSessionId);
        if (!consoleWasInterrupted) return;

        var unprocessed = _sessionStore.GetLastUnrespondedUserMessage(consoleSessionId);
        if (unprocessed == null) return;

        var preview = unprocessed.Length > 120 ? unprocessed[..120] + "…" : unprocessed;

        AnsiConsole.Write(new Panel(new Markup($"[italic]{Markup.Escape(preview)}[/]"))
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
        _commandQueue.Enqueue(cmd);

        var result = await tcs.Task;
        AnsiConsole.WriteLine(result.Output);
        AnsiConsole.WriteLine();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Display helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static void ShowHelp()
    {
        void AddSection(string header, string[][] rows)
        {
            var table = new Table()
                .Border(TableBorder.None).HideHeaders()
                .AddColumn(new TableColumn("").Width(30))
                .AddColumn(new TableColumn(""));
            foreach (var row in rows)
                table.AddRow($"[bold white]{Markup.Escape(row[0])}[/]", $"[dim]{Markup.Escape(row[1])}[/]");
            AnsiConsole.MarkupLine($"\n[bold dodgerblue1]{header}[/]");
            AnsiConsole.Write(table);
        }

        AddSection("General Commands", new[]
        {
            new[] { "help",                "Show this help message" },
            new[] { "status",              "Show agent status" },
            new[] { "tools",               "List available tools" },
            new[] { "skills",              "List all registered skills" },
            new[] { "skill <name>",        "Show detailed info for a skill" },
            new[] { "doctor",              "Run health checks" },
            new[] { "doctor fix",          "Run health checks and attempt auto-fixes" },
            new[] { "doctor config <req>", "Ask DoctorAgent to modify appsettings.json" },
            new[] { "exit",                "Exit the program" },
        });

        AddSection("Startup Flags", new[]
        {
            new[] { "--doctor",  "Run health checks on startup and exit" },
            new[] { "--fix",     "Combined with --doctor: attempt automatic fixes" },
        });

        AddSection("Agent Management", new[]
        {
            new[] { "agents",              "List active sub-agents" },
            new[] { "agents stats",        "Show processor/queue statistics" },
            new[] { "agents pause <id>",   "Pause a sub-agent" },
            new[] { "agents resume <id>",  "Resume a paused sub-agent" },
            new[] { "agents stop <id>",    "Gracefully stop a sub-agent" },
            new[] { "agents kill <id>",    "Force-kill a sub-agent" },
            new[] { "agents kill all",     "Force-kill every active sub-agent" },
        });

        AddSection("Service Management", new[]
        {
            new[] { "install-service",     "Install FoxAgent as a system service" },
            new[] { "uninstall-service",   "Uninstall the FoxAgent service" },
            new[] { "start-service",       "Start the FoxAgent service" },
            new[] { "stop-service",        "Stop the FoxAgent service" },
            new[] { "restart-service",     "Restart the FoxAgent service" },
            new[] { "service-status",      "Show service status" },
            new[] { "service-config",      "Show service configuration" },
        });

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]You can also ask the agent to execute commands, read/write files, spawn sub-agents, use skills, and more.[/]");
        AnsiConsole.WriteLine();
    }

    private static void ShowStatus(FoxAgent agent)
    {
        var info = agent.GetInfo();
        var table = new Table()
            .Border(TableBorder.Rounded).BorderColor(Color.Blue).HideHeaders()
            .AddColumn(new TableColumn("[bold]Property[/]").Width(14))
            .AddColumn(new TableColumn("[bold]Value[/]"));

        table.AddRow("[dim]Name[/]",       $"[bold white]{Markup.Escape(info.Name)}[/]");
        table.AddRow("[dim]ID[/]",         $"[grey]{Markup.Escape(info.Id)}[/]");
        table.AddRow("[dim]Status[/]",     $"[bold green]{Markup.Escape(info.Status.ToString())}[/]");
        table.AddRow("[dim]Messages[/]",   info.MessageCount.ToString());
        table.AddRow("[dim]Sub-agents[/]", info.SubAgentCount.ToString());
        table.AddRow("[dim]Tools[/]",      info.ToolCount.ToString());
        table.AddRow("[dim]Memory[/]",     info.HasMemory ? "[green]Enabled[/]" : "[dim]Disabled[/]");
        table.AddRow("[dim]Created[/]",    $"[grey]{info.CreatedAt:yyyy-MM-dd HH:mm:ss}[/]");
        table.AddRow("[dim]Last Active[/]",$"[grey]{info.LastActiveAt:yyyy-MM-dd HH:mm:ss}[/]");

        AnsiConsole.Write(new Panel(table)
        {
            Header = new PanelHeader("[bold] Agent Status [/]", Justify.Left),
            Border = BoxBorder.Rounded, BorderStyle = Style.Parse("blue"),
            Padding = new Padding(1, 0),
        });
        AnsiConsole.WriteLine();
    }

    private void ShowTools()
    {
        var tools = _toolRegistry.GetAll();
        var table = new Table()
            .Border(TableBorder.Rounded).BorderColor(Color.Blue)
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

    private void ShowSkills()
    {
        var manifests = _skillRegistry.GetSkillManifests();
        if (manifests.Count == 0) { AnsiConsole.MarkupLine("[dim]No skills registered.[/]"); return; }

        var table = new Table()
            .Border(TableBorder.Rounded).BorderColor(Color.Blue)
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
        AnsiConsole.MarkupLine("[dim]Use [bold white]skill <name>[/] for details.[/]");
        AnsiConsole.WriteLine();
    }

    private void ShowSkillDetail(string skillName)
    {
        var skill = _skillRegistry.Get(skillName)
            ?? _skillRegistry.GetAll().FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));

        if (skill == null)
        {
            AnsiConsole.MarkupLine($"[bold red]Skill '{Markup.Escape(skillName)}' not found.[/] Use [bold white]skills[/] to list all.");
            return;
        }

        var content = new Rows(
            new Markup($"[dim]Description:[/]  {Markup.Escape(skill.Description)}"),
            skill.Dependencies.Count > 0 ? new Markup($"[dim]Dependencies:[/] {Markup.Escape(string.Join(", ", skill.Dependencies))}") : new Markup(""),
            skill.Metadata?.Capabilities.Count > 0 ? new Markup($"[dim]Capabilities:[/] {Markup.Escape(string.Join(", ", skill.Metadata.Capabilities))}") : new Markup(""),
            skill.Metadata?.Tags.Count > 0 ? new Markup($"[dim]Tags:[/]         {Markup.Escape(string.Join(", ", skill.Metadata.Tags))}") : new Markup(""),
            skill.Metadata != null ? new Markup($"[dim]Complexity:[/]   [bold]{skill.Metadata.ComplexityScore}[/]/10") : new Markup(""),
            new Markup($"[dim]Type:[/]         {(skill is ISkillPlugin ? "local" : "generic")} skill"));

        AnsiConsole.Write(new Panel(content)
        {
            Header = new PanelHeader($"[bold] {Markup.Escape(skill.Name)}  v{Markup.Escape(skill.Version)} [/]", Justify.Left),
            Border = BoxBorder.Rounded, BorderStyle = Style.Parse("blue"),
            Padding = new Padding(1, 0),
        });

        var tools = skill.GetTools();
        if (tools.Count > 0)
        {
            var toolTable = new Table().Border(TableBorder.None).HideHeaders()
                .AddColumn(new TableColumn("").Width(28))
                .AddColumn(new TableColumn(""));
            foreach (var tool in tools)
                toolTable.AddRow($"[bold white]  • {Markup.Escape(tool.Name)}[/]", $"[dim]{Markup.Escape(tool.Description)}[/]");
            AnsiConsole.MarkupLine($"[bold]Tools ({tools.Count}):[/]");
            AnsiConsole.Write(toolTable);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]To load guidance: [white]load_skill(skill_name: \"{Markup.Escape(skill.Name)}\")[/][/]");
        AnsiConsole.WriteLine();
    }

    private void ShowAgents()
    {
        var tasks = _subAgentManager.GetActiveSubAgents().ToList();
        if (tasks.Count == 0) { AnsiConsole.MarkupLine("[dim]No active sub-agents.[/]"); AnsiConsole.WriteLine(); return; }

        var table = new Table()
            .Border(TableBorder.Rounded).BorderColor(Color.Blue)
            .Title($"[bold] Active Sub-Agents ({tasks.Count}) [/]")
            .AddColumn(new TableColumn("[bold]RunId[/]").Width(38))
            .AddColumn(new TableColumn("[bold]State[/]").Width(10))
            .AddColumn(new TableColumn("[bold]Elapsed[/]").Width(9).RightAligned())
            .AddColumn(new TableColumn("[bold]Session[/]"));

        foreach (var t in tasks.OrderBy(t => t.CreatedAt))
        {
            var elapsed = t.ElapsedTime.TotalSeconds < 60
                ? $"{t.ElapsedTime.TotalSeconds:F0}s" : $"{t.ElapsedTime:mm\\:ss}";
            var session = t.SessionKey.Length > 32 ? "…" + t.SessionKey[^31..] : t.SessionKey;
            var stateStyle = t.State.ToString() switch
            {
                "Running" => "bold green", "Paused" => "bold yellow", "Failed" => "bold red", _ => "dim"
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

    private void ShowAgentStats()
    {
        var stats  = _subAgentManager.GetStatistics();
        var pStats = _commandProcessor.GetStatistics();

        var agentTable = new Table().Border(TableBorder.None).HideHeaders()
            .AddColumn(new TableColumn("").Width(22)).AddColumn(new TableColumn(""));
        agentTable.AddRow("[dim]Active sub-agents[/]", $"[bold]{stats.TotalActiveSubAgents}[/]");
        agentTable.AddRow("[dim]  Running[/]",         $"[bold green]{stats.RunningSubAgents}[/]");
        agentTable.AddRow("[dim]  Pending[/]",         stats.PendingSubAgents.ToString());
        agentTable.AddRow("[dim]  Completed[/]",       stats.CompletedSubAgents.ToString());
        agentTable.AddRow("[dim]  Failed[/]",          $"[bold red]{stats.FailedSubAgents}[/]");
        agentTable.AddRow("[dim]  Timed-out[/]",       $"[bold yellow]{stats.TimedOutSubAgents}[/]");

        var procTable = new Table().Border(TableBorder.None).HideHeaders()
            .AddColumn(new TableColumn("").Width(22)).AddColumn(new TableColumn(""));
        procTable.AddRow("[dim]Total processed[/]", pStats.TotalProcessed.ToString());
        procTable.AddRow("[dim]Total failed[/]",    $"[bold red]{pStats.TotalFailed}[/]");
        procTable.AddRow("[dim]Active commands[/]", pStats.ActiveCommands.ToString());
        procTable.AddRow("[dim]Queued commands[/]", pStats.QueuedCommands.ToString());
        procTable.AddRow("[dim]Uptime[/]",          $"[dodgerblue1]{pStats.Uptime:hh\\:mm\\:ss}[/]");

        AnsiConsole.Write(new Panel(agentTable) { Header = new PanelHeader("[bold] Sub-Agent Statistics [/]", Justify.Left), Border = BoxBorder.Rounded, BorderStyle = Style.Parse("blue"), Padding = new Padding(1, 0) });
        AnsiConsole.Write(new Panel(procTable)  { Header = new PanelHeader("[bold] Command Processor [/]",   Justify.Left), Border = BoxBorder.Rounded, BorderStyle = Style.Parse("blue"), Padding = new Padding(1, 0) });
        AnsiConsole.WriteLine();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Agent management commands
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleAgentPause(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) { AnsiConsole.MarkupLine("[dim]Usage:[/] [bold white]agents pause <runId>[/]"); return; }
        if (_subAgentManager.PauseSubAgent(runId))
            AnsiConsole.MarkupLine($"[bold yellow]⏸[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] paused.");
        else
            AnsiConsole.MarkupLine($"[bold red]✗[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] not found.");
        AnsiConsole.WriteLine();
    }

    private void HandleAgentResume(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) { AnsiConsole.MarkupLine("[dim]Usage:[/] [bold white]agents resume <runId>[/]"); return; }
        if (_subAgentManager.ResumeSubAgent(runId))
            AnsiConsole.MarkupLine($"[bold green]▶[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] resumed.");
        else
            AnsiConsole.MarkupLine($"[bold red]✗[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] not found.");
        AnsiConsole.WriteLine();
    }

    private async Task HandleAgentStopAsync(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) { AnsiConsole.MarkupLine("[dim]Usage:[/] [bold white]agents stop <runId>[/]"); return; }
        AnsiConsole.MarkupLine($"[dim]Stopping sub-agent [bold white]{Markup.Escape(runId)}[/]...[/]");
        var ok = await _subAgentManager.StopSubAgentAsync(runId);
        AnsiConsole.MarkupLine(ok
            ? $"[bold green]✓[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] stopped."
            : $"[bold red]✗[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] not found.");
        AnsiConsole.WriteLine();
    }

    private void HandleAgentKill(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var active = _subAgentManager.GetActiveSubAgents().ToList();
            if (active.Count == 0) { AnsiConsole.MarkupLine("[dim]No active sub-agents to kill.[/]"); AnsiConsole.WriteLine(); return; }
            AnsiConsole.MarkupLine($"[bold red]✗[/]  Killing {active.Count} sub-agent(s)...");
            foreach (var t in active) _subAgentManager.KillSubAgent(t.RunId);
            AnsiConsole.MarkupLine("[bold green]Done.[/]");
        }
        else
        {
            if (_subAgentManager.KillSubAgent(runId))
                AnsiConsole.MarkupLine($"[bold red]✗[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] killed.");
            else
                AnsiConsole.MarkupLine($"[bold red]✗[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] not found.");
        }
        AnsiConsole.WriteLine();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Service management commands
    // ─────────────────────────────────────────────────────────────────────────

    private async Task HandleServiceCommandAsync(string command, CancellationToken ct)
    {
        try
        {
            var handler = ServiceCommandHandler.CreateFromConfiguration(_configuration, _logger);
            var result = await handler.ProcessCommandAsync(command);
            
            AnsiConsole.WriteLine();
            if (result.Success)
            {
                AnsiConsole.MarkupLine($"[bold green]✓[/]  {Markup.Escape(result.Message)}");
                if (!string.IsNullOrEmpty(result.Details))
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(result.Details)}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold red]✗[/]  {Markup.Escape(result.Message)}");
                if (!string.IsNullOrEmpty(result.Details))
                    AnsiConsole.MarkupLine($"[dim]{Markup.Escape(result.Details)}[/]");
            }
            AnsiConsole.WriteLine();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]✗[/]  Error processing service command: {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteLine();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Multiline console input
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a (potentially multi-line) input from the console.
    /// <list type="bullet">
    ///   <item><b>Enter</b> — submits when no paste is in progress.</item>
    ///   <item><b>Shift+Enter</b> — inserts a newline without submitting.</item>
    ///   <item><b>Paste</b> — newlines inside pasted text are treated as line breaks.</item>
    /// </list>
    /// </summary>
    internal static string ReadMultilineInput()
    {
        const string prompt       = "\x1b[1;38;5;33m>\x1b[0m "; // bold dodgerblue1
        const string continuation = "  ";

        Console.Write(prompt);

        var lines   = new List<string>();
        var current = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter
                && !key.Modifiers.HasFlag(ConsoleModifiers.Shift)
                && !Console.KeyAvailable)
            {
                Console.WriteLine();
                lines.Add(current.ToString());
                return string.Join("\n", lines);
            }

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                Console.Write(continuation);
                lines.Add(current.ToString());
                current.Clear();
                continue;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (current.Length > 0)
                {
                    current.Remove(current.Length - 1, 1);
                    Console.Write("\b \b");
                }
                continue;
            }

            if (key.KeyChar != '\0')
            {
                current.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
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
