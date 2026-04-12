using AgentFox.Agents;
using AgentFox.Channels;
using AgentFox.Doctor;
using AgentFox.Doctor.Checks;
using AgentFox.Helpers;
using AgentFox.LLM;
using AgentFox.MCP;
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
/// Agent initialization, channel loading, and command processor setup are handled
/// by <see cref="AgentOrchestrator"/>. This worker simply awaits the agent, then
/// drives the interactive input loop until "exit" or cancellation.
/// </para>
/// </summary>
public sealed class CliWorker : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
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
    private readonly UIConfig _uiConfig;
    private readonly IConfiguration _configuration;
    private readonly MarkdownSessionStore _sessionStore;
    private readonly FoxAgentHolder _agentHolder;
    private readonly ChannelManagerHolder _channelManagerHolder;
    private readonly ILogger<CliWorker> _logger;
    private readonly ServiceConfig _serviceConfig;

    public CliWorker(
        IHostApplicationLifetime lifetime,
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
        UIConfig uiConfig,
        IConfiguration configuration,
        MarkdownSessionStore sessionStore,
        FoxAgentHolder agentHolder,
        ChannelManagerHolder channelManagerHolder,
        ILogger<CliWorker> logger,
        ServiceConfig serviceConfig)
    {
        _lifetime             = lifetime;
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
        _uiConfig             = uiConfig;
        _configuration        = configuration;
        _sessionStore         = sessionStore;
        _agentHolder          = agentHolder;
        _channelManagerHolder = channelManagerHolder;
        _logger               = logger;
        _serviceConfig        = serviceConfig;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BackgroundService entry point
    // ─────────────────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // In service / web-only / redirected mode there is no interactive terminal.
        // AgentOrchestrator still runs (agent + channels + command processor are live);
        // we just skip the REPL loop.
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
    // Interactive session — waits for the orchestrator then runs the REPL
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunInteractiveSessionAsync(CancellationToken ct)
    {
        var manifests = _skillRegistry.GetSkillManifests();
        AnsiConsole.MarkupLine($"[bold green]✓[/] [dim]{manifests.Count} skill(s) registered.[/]");
        AnsiConsole.WriteLine();

        // Wait until AgentOrchestrator has finished building the agent and channels.
        var agent          = await _agentHolder.WaitAsync(ct);
        var channelManager = await _channelManagerHolder.WaitAsync(ct);

        if (channelManager.Channels.Count > 0)
            AnsiConsole.MarkupLine($"[bold green]✓[/]  {channelManager.Channels.Count} channel(s) connected.");
        else
            AnsiConsole.MarkupLine("[dim]No channels configured. Use manage_channel to add one at runtime.[/]");

        // Session recovery (interactive — must stay in CliWorker)
        var interrupted      = _sessionManager.GetInterruptedActiveSessions();
        var consoleSessionId = _sessionManager.GetOrCreateConsoleSession(agent.Id);
        await RecoverInterruptedSessionsAsync(agent, consoleSessionId, interrupted, ct);

        // DoctorAgent for inline health-check commands
        var appConfigPath = AppSettingsHelper.ResolveAppSettingsPath();
        var doctorAgent   = new DoctorAgent(_chatClient, appConfigPath);

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
                // StopApplication triggers AgentOrchestrator.StopAsync which
                // disconnects channels and stops the command processor.
                break;
            }

            if (handled != ReplAction.Unhandled)
                continue;

            await RunAgentTurnAsync(input, agent, consoleSessionId, ct);
        }
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
        var lower   = trimmed.ToLowerInvariant();

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
        var tcs = new TaskCompletionSource<AgentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cmd = AgentCommand.CreateMainCommand(
            sessionKey: consoleSessionId,
            agentId:    agent.Id,
            message:    input);
        cmd.ResultSource = tcs;

        var streamChannel = SysChannel.CreateUnbounded<(bool IsReasoning, string Text)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var sb           = new StringBuilder();
        var sbReasoning  = new StringBuilder();
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
            var reasoningLines  = sbReasoning.ToString().Split('\n');
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
                                else             sb.Append(text);
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
            OnToken  = async chunk => await streamChannel.Writer.WriteAsync((false, chunk)),
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
        var ltMemory      = MemoryBackendFactory.CreateLongTermStorage(_configuration, _workspaceManager);
        var doctorRunner  = new DoctorRunner(new IHealthCheckable[]
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
            new McpHealthCheck(_mcpManager, _configuration, doctorAgent),
        });
        await doctorRunner.RunAsync(autoFix);
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
            Header      = new PanelHeader("[bold yellow] ⚠ Previous session interrupted [/]", Justify.Left),
            Border      = BoxBorder.Rounded,
            BorderStyle = Style.Parse("yellow"),
            Padding     = new Padding(1, 0),
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
            new[] { "exit",                "Quit AgentFox" },
        });

        AddSection("Sub-Agent Commands", new[]
        {
            new[] { "agents",              "List active sub-agents" },
            new[] { "agents stats",        "Show sub-agent and processor statistics" },
            new[] { "agents pause <id>",   "Pause a running sub-agent" },
            new[] { "agents resume <id>",  "Resume a paused sub-agent" },
            new[] { "agents stop <id>",    "Gracefully stop a sub-agent" },
            new[] { "agents kill [<id>]",  "Kill a sub-agent (or all with no id)" },
        });

        AddSection("Service Commands", new[]
        {
            new[] { "install-service",     "Install as a system service" },
            new[] { "uninstall-service",   "Remove the system service" },
            new[] { "start-service",       "Start the system service" },
            new[] { "stop-service",        "Stop the system service" },
            new[] { "restart-service",     "Restart the system service" },
            new[] { "service-status",      "Show service status" },
            new[] { "service-config",      "Show service configuration" },
        });

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]You can also ask the agent to execute commands, read/write files, spawn sub-agents, use skills, and more.[/]");
        AnsiConsole.WriteLine();
    }

    private static void ShowStatus(FoxAgent agent)
    {
        var info  = agent.GetInfo();
        var table = new Table()
            .Border(TableBorder.Rounded).BorderColor(Color.Blue).HideHeaders()
            .AddColumn(new TableColumn("[bold]Property[/]").Width(14))
            .AddColumn(new TableColumn("[bold]Value[/]"));

        table.AddRow("[dim]Name[/]",        $"[bold white]{Markup.Escape(info.Name)}[/]");
        table.AddRow("[dim]ID[/]",          $"[grey]{Markup.Escape(info.Id)}[/]");
        table.AddRow("[dim]Status[/]",      $"[bold green]{Markup.Escape(info.Status.ToString())}[/]");
        table.AddRow("[dim]Messages[/]",    info.MessageCount.ToString());
        table.AddRow("[dim]Sub-agents[/]",  info.SubAgentCount.ToString());
        table.AddRow("[dim]Tools[/]",       info.ToolCount.ToString());
        table.AddRow("[dim]Memory[/]",      info.HasMemory ? "[green]Enabled[/]" : "[dim]Disabled[/]");
        table.AddRow("[dim]Created[/]",     $"[grey]{info.CreatedAt:yyyy-MM-dd HH:mm:ss}[/]");
        table.AddRow("[dim]Last Active[/]", $"[grey]{info.LastActiveAt:yyyy-MM-dd HH:mm:ss}[/]");

        AnsiConsole.Write(new Panel(table)
        {
            Header      = new PanelHeader("[bold] Agent Status [/]", Justify.Left),
            Border      = BoxBorder.Rounded,
            BorderStyle = Style.Parse("blue"),
            Padding     = new Padding(1, 0),
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
            Header      = new PanelHeader($"[bold] {Markup.Escape(skill.Name)}  v{Markup.Escape(skill.Version)} [/]", Justify.Left),
            Border      = BoxBorder.Rounded,
            BorderStyle = Style.Parse("blue"),
            Padding     = new Padding(1, 0),
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
            var session    = t.SessionKey.Length > 32 ? "…" + t.SessionKey[^31..] : t.SessionKey;
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
        agentTable.AddRow("[dim]  Running[/]",          $"[bold green]{stats.RunningSubAgents}[/]");
        agentTable.AddRow("[dim]  Pending[/]",          stats.PendingSubAgents.ToString());
        agentTable.AddRow("[dim]  Completed[/]",        stats.CompletedSubAgents.ToString());
        agentTable.AddRow("[dim]  Failed[/]",           $"[bold red]{stats.FailedSubAgents}[/]");
        agentTable.AddRow("[dim]  Timed-out[/]",        $"[bold yellow]{stats.TimedOutSubAgents}[/]");

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
            var result  = await handler.ProcessCommandAsync(command);

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

}
