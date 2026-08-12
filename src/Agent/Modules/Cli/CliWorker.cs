using AgentFox.Agents;
using AgentFox.Channels;
using AgentFox.Doctor;
using AgentFox.Hitl;
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
using PrettyPrompt;
using PrettyPrompt.Completion;
using PrettyPrompt.Consoles;
using PrettyPrompt.Documents;
using PrettyPrompt.Highlighting;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text;
using System.Threading.Channels;
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
    private readonly HitlManager _hitlManager;
    private readonly IEnumerable<IAppModule> _modules;

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
        ServiceConfig serviceConfig,
        HitlManager hitlManager,
        IEnumerable<IAppModule> modules)
    {
        _hitlManager          = hitlManager;
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
        _modules              = modules;
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
            AnsiConsole.MarkupLine($"[bold red][[ERR]][/] CLI session terminated: {Markup.Escape(ex.Message)}");
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

        // The web/API module binds its own Kestrel listener (see Program.cs UseUrls); only print
        // the link when that module is actually active, so this doesn't claim a UI exists for a
        // CLI-only / --disabled-web run.
        if (_modules.Any(m => m.Name is "web" or "api"))
        {
            var url = $"http://localhost:{_serviceConfig.Port}";
            AnsiConsole.MarkupLine($"[bold green]✓[/]  Web UI: [link={url}]{url}[/]");
        }

        // Session recovery (interactive — must stay in CliWorker)
        var interrupted      = _sessionManager.GetInterruptedActiveSessions();
        var consoleSessionId = _sessionManager.GetOrCreateConsoleSession(agent.Id);
        await RecoverInterruptedSessionsAsync(agent, consoleSessionId, interrupted, ct);

        // DoctorAgent for inline health-check commands
        var appConfigPath = AppSettingsHelper.ResolveAppSettingsPath();
        var doctorAgent   = new DoctorAgent(_chatClient, appConfigPath);

        AnsiConsole.MarkupLine("[dim]Type [bold white]/help[/] for commands, [bold white]/exit[/] to quit. [bold white]Shift+Enter[/] for multi-line input, [bold white]↑/↓[/] for history, [bold white]Esc[/] to clear.[/]");
        AnsiConsole.WriteLine();

        // PrettyPrompt owns the whole line-editing experience (cursor movement, history navigation,
        // real clipboard paste, Ctrl-C to clear the current line) so the REPL no longer hand-rolls
        // key handling via raw Console.ReadKey. One instance is reused for the life of the session so
        // in-memory + persisted history accumulates across turns.
        var callbacks = new ReplPromptCallbacks();
        await using var prompt = new Prompt(
            persistentHistoryFilepath: Path.Combine(_workspaceManager.ResolvePath(""), ".cli_history"),
            callbacks: callbacks,
            console: new GatedConsole(() => callbacks.InputIsEmpty),
            configuration: new PromptConfiguration(
                prompt: new FormattedString(
                    "> ",
                    new FormatSpan(0, 1, new ConsoleFormat(Foreground: AnsiColor.Rgb(0, 135, 255), Bold: true)))));

        // ── REPL loop ─────────────────────────────────────────────────────────
        while (!ct.IsCancellationRequested)
        {
            // Anything a background lane produced while the last turn (or the last prompt) held
            // the terminal goes out now, onto a clean screen, before the prompt is drawn over it.
            ConsoleGate.Flush();

            string input;
            // The prompt owns the terminal for as long as it is on screen: background writes are
            // buffered rather than scribbled through PrettyPrompt's incremental render. GatedConsole
            // surrenders the line as soon as something is waiting and the input buffer is empty, so
            // buffered output surfaces in seconds rather than waiting on the next keystroke.
            using (ConsoleGate.Suspend())
            {
                callbacks.ResetInput();
                var response = await prompt.ReadLineAsync();
                if (!response.IsSuccess)
                    continue; // Ctrl-C, or a wake-up for pending output — redraw rather than quit.

                input = response.Text;
            }

            if (string.IsNullOrWhiteSpace(input))
                continue;

            // A bug in a single command handler must not escape this loop: this REPL runs
            // in the same host as the Web module and every messaging channel, and an
            // unhandled exception here previously took ALL of them down via
            // BackgroundServiceExceptionBehavior.StopHost (see CliWorker crash on a Spectre
            // markup parse error from an unescaped approval id).
            try
            {
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "REPL command failed: {Input}", input);
                AnsiConsole.MarkupLine($"[bold red]Command failed:[/] {Markup.Escape(ex.Message)}");
            }
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
        var raw = input.Trim();
        if (!raw.StartsWith('/'))
            return ReplAction.Unhandled; // no leading slash — treat as a chat message for the agent

        var trimmed = raw[1..].Trim();
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
                AnsiConsole.MarkupLine("[yellow]Usage: /doctor config <your request>[/]");
                AnsiConsole.MarkupLine("[dim]Example: /doctor config set LLM provider to Anthropic with claude-3-5-sonnet[/]");
            }
            else
            {
                var doctorResult = await doctorAgent.ProcessRequestAsync(request);
                AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(doctorResult)}[/]");
            }
            return ReplAction.Handled;
        }

        // ── HITL approval commands ───────────────────────────────────────────
        if (lower.StartsWith("hitl approve "))
        {
            var rest       = trimmed["hitl approve ".Length..].Trim();
            var spaceIdx   = rest.IndexOf(' ');
            var approvalId = spaceIdx < 0 ? rest : rest[..spaceIdx];
            var feedback   = spaceIdx < 0 ? null : rest[(spaceIdx + 1)..].Trim();

            if (_hitlManager.Respond(approvalId, approved: true, feedback))
                AnsiConsole.MarkupLine($"[green]✅ Approved[/] [[{Markup.Escape(approvalId)}]]");
            else
                AnsiConsole.MarkupLine($"[yellow]No pending approval with id '{Markup.Escape(approvalId)}'.[/]");
            return ReplAction.Handled;
        }

        if (lower.StartsWith("hitl reject "))
        {
            var rest       = trimmed["hitl reject ".Length..].Trim();
            var spaceIdx   = rest.IndexOf(' ');
            var approvalId = spaceIdx < 0 ? rest : rest[..spaceIdx];
            var reason     = spaceIdx < 0 ? null : rest[(spaceIdx + 1)..].Trim();

            if (_hitlManager.Respond(approvalId, approved: false, reason))
                AnsiConsole.MarkupLine($"[red]❌ Rejected[/] [[{Markup.Escape(approvalId)}]]");
            else
                AnsiConsole.MarkupLine($"[yellow]No pending approval with id '{Markup.Escape(approvalId)}'.[/]");
            return ReplAction.Handled;
        }

        // Free-form answer to a request_human_input raised by a session that does not own the
        // terminal (cron, heartbeat, sub-agent, web). Those used to read stdin directly from a
        // background thread, which is what stole the operator's keystrokes; now they wait here.
        if (lower.StartsWith("hitl reply "))
        {
            var rest       = trimmed["hitl reply ".Length..].Trim();
            var spaceIdx   = rest.IndexOf(' ');
            var questionId = spaceIdx < 0 ? rest : rest[..spaceIdx];
            var answer     = spaceIdx < 0 ? string.Empty : rest[(spaceIdx + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(answer))
                AnsiConsole.MarkupLine("[yellow]Usage: /hitl reply <id> <your answer>[/]");
            else if (_hitlManager.RespondToQuestion(questionId, answer))
                AnsiConsole.MarkupLine($"[green]✅ Answered[/] [[{Markup.Escape(questionId)}]]");
            else
                AnsiConsole.MarkupLine(
                    $"[yellow]No pending question with id '{Markup.Escape(questionId)}'.[/] " +
                    "[dim]It may have expired — see /hitl.[/]");
            return ReplAction.Handled;
        }

        if (lower is "hitl" or "hitl list")
        {
            ShowHitlPending();
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
        // The streamed display owns the screen for the length of the turn. A cron run or a sub-agent
        // announcement completing mid-turn used to print straight into the middle of it, corrupting
        // both its own output and the live view; buffered here, it lands cleanly once the turn ends.
        using var terminal = ConsoleGate.Suspend();

        // Visually separates the user's (possibly multi-line) input from the streamed response
        // that follows immediately after — without this, the response starts rendering right where
        // the cursor was left after Enter, and the two run together with nothing to tell them apart.
        AnsiConsole.Write(new Rule().RuleStyle("grey dim"));

        var tcs = new TaskCompletionSource<AgentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cmd = AgentCommand.CreateMainCommand(
            sessionKey: consoleSessionId,
            agentId:    agent.Id,
            message:    input);
        cmd.ResultSource = tcs;

        // Created per streamed turn, not per REPL input: EnsureTodosCompletedAsync can issue bounded
        // follow-up turns through the same callbacks, and each one starts with a fresh OnStart after
        // the previous OnComplete closed the writer. Reusing one channel made the follow-up's first
        // OnToken throw ChannelClosedException and fail an otherwise-fine turn.
        Channel<(bool IsReasoning, string Text)>? streamChannel = null;
        var sb          = new StringBuilder();
        var sbReasoning = new StringBuilder();
        Task? liveDisplayTask = null;

        var turnStartedAt = DateTime.UtcNow;
        var spinnerFrame  = 0;

        // What the busy line says. Written from the streaming callbacks (serially — Agent.cs awaits
        // them one at a time inside its update loop) and read by whichever display is currently up.
        var busyLabel = "Sending";

        // Handed off from the spinner to the Live display at the *first streamed chunk*, not at
        // OnStart. OnStart fires before RunStreamingAsync — i.e. before the first LLM call and
        // before every tool round-trip — so handing over there froze the terminal on a static
        // "Working..." for the longest part of the turn, which read as "the indicator vanished".
        var handoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Assigned below, immediately before Enqueue — so by the time the lane task can reach a
        // streaming callback it is guaranteed non-null.
        Task? busyTask = null;

        string BusyFooter()
        {
            var frames  = Spinner.Known.Dots.Frames;
            var glyph   = frames[spinnerFrame % frames.Count];
            var elapsed = (int)(DateTime.UtcNow - turnStartedAt).TotalSeconds;
            return $"[dodgerblue1]{Markup.Escape(glyph)}[/] [dim]{Markup.Escape(busyLabel)}... {elapsed}s[/]";
        }

        // `final` drops the busy line so the completed turn is left on screen as just its output.
        IRenderable BuildDisplay(bool final = false)
        {
            var rows = new List<IRenderable>();

            if (sbReasoning.Length > 0)
            {
                var reasoningLines   = sbReasoning.ToString().Split('\n');
                var visibleReasoning = reasoningLines.Length > 20
                    ? string.Join('\n', reasoningLines[^20..])
                    : sbReasoning.ToString();
                rows.Add(new Panel(
                        new Markup($"[italic dim yellow]{Markup.Escape(visibleReasoning.TrimEnd('\n'))}[/]"))
                    .Header("[dim yellow]Thinking...[/]")
                    .BorderColor(Color.Yellow)
                    .Expand());
            }

            if (sb.Length > 0)
                rows.Add(new Text(sb.ToString()));

            if (!final)
                rows.Add(new Markup(BusyFooter()));

            return rows.Count == 0 ? new Text(string.Empty) : new Rows(rows);
        }

        // Called from the first chunk of each turn. Spectre allows only one interactive display at
        // a time, so the spinner must fully release the exclusivity lock before Live starts —
        // otherwise it throws "Trying to run one or more interactive functions concurrently".
        async Task<Channel<(bool IsReasoning, string Text)>> EnsureLiveDisplayAsync()
        {
            var existing = streamChannel;
            if (existing != null) return existing;

            handoff.TrySetResult();
            if (busyTask != null) await busyTask;

            var channel = SysChannel.CreateUnbounded<(bool IsReasoning, string Text)>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            streamChannel = channel;

            liveDisplayTask = AnsiConsole.Live(BuildDisplay())
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .Cropping(VerticalOverflowCropping.Top)
                .StartAsync(async ctx =>
                {
                    var reader = channel.Reader;
                    while (true)
                    {
                        // Tick even when no token arrives: a turn that streams some text and then
                        // spends a minute in tool calls must keep animating, and batching whatever
                        // did arrive beats repainting once per token.
                        var wait = reader.WaitToReadAsync().AsTask();
                        if (await Task.WhenAny(wait, Task.Delay(SpinnerTickMs)) == wait)
                        {
                            if (!await wait) break; // writer completed — turn is done streaming
                            while (reader.TryRead(out var item))
                            {
                                if (item.IsReasoning) sbReasoning.Append(item.Text);
                                else                  sb.Append(item.Text);
                            }
                        }

                        // A tool that is asking the user something owns the screen until it has its
                        // answer — repainting at 120ms on top of the line being typed makes it
                        // unreadable. Tokens keep accumulating; only the paint is held.
                        if (ConsoleGate.InteractiveReadInProgress)
                            continue;

                        spinnerFrame++;
                        ctx.UpdateTarget(BuildDisplay());
                        ctx.Refresh();
                    }

                    ctx.UpdateTarget(BuildDisplay(final: true));
                    ctx.Refresh();
                });

            return channel;
        }

        cmd.Streaming = new StreamingCallbacks
        {
            OnStart = () =>
            {
                // A follow-up turn (EnsureTodosCompletedAsync) re-enters here after OnComplete tore
                // the previous display down. Restart the spinner for its pre-token phase; on the
                // first turn — or a turn that never streamed a chunk — busyTask is still running
                // and already covers this.
                if (busyTask is { IsCompleted: true })
                {
                    streamChannel   = null;
                    liveDisplayTask = null;
                    handoff         = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    busyTask        = RunBusyIndicatorAsync(handoff.Task, tcs.Task, () => busyLabel, turnStartedAt);
                }

                busyLabel = "Thinking";
                return Task.CompletedTask;
            },
            OnStatus = status =>
            {
                busyLabel = status switch
                {
                    "thinking"           => "Thinking",
                    "running_tools"      => "Running tools",
                    "preparing_response" => "Finishing up",
                    _                    => busyLabel,
                };
                return Task.CompletedTask;
            },
            OnToolActivity = activity =>
            {
                busyLabel = activity.Status == "running" && !string.IsNullOrWhiteSpace(activity.ToolName)
                    ? $"Running {activity.ToolName}"
                    : "Thinking";
                return Task.CompletedTask;
            },
            OnReasoning = !_uiConfig.RenderReasoning ? null
                : async chunk =>
                {
                    var channel = await EnsureLiveDisplayAsync();
                    await channel.Writer.WriteAsync((true, chunk));
                },
            OnToken = async chunk =>
            {
                var channel = await EnsureLiveDisplayAsync();
                await channel.Writer.WriteAsync((false, chunk));
            },
            OnComplete = async () =>
            {
                streamChannel?.Writer.TryComplete();
                if (liveDisplayTask != null)
                    await liveDisplayTask;
            },
        };

        busyTask = RunBusyIndicatorAsync(handoff.Task, tcs.Task, () => busyLabel, turnStartedAt);
        _commandQueue.Enqueue(cmd);

        AgentResult result;
        try
        {
            // WaitAsync so host shutdown / Ctrl-C unblocks the REPL. Without it a command that
            // never completes its ResultSource (e.g. dropped because its lane has no handler)
            // wedges the prompt permanently with no output.
            result = await tcs.Task.WaitAsync(ct);
        }
        catch (Exception ex)
        {
            // A lane that faults or cancels its ResultSource surfaces here. The Main lane
            // converts exceptions into a failed AgentResult instead, handled further down.
            handoff.TrySetResult();
            streamChannel?.Writer.TryComplete();
            await SafeAwaitAsync(busyTask);
            await SafeAwaitAsync(liveDisplayTask);
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            WriteTurnError(ex.Message);
            return;
        }

        // Both are already finished on the normal path (the first chunk drains the spinner,
        // OnComplete awaits the live display), but a turn that never streams a chunk leaves the
        // spinner holding Spectre's exclusivity lock for every later write.
        handoff.TrySetResult();
        streamChannel?.Writer.TryComplete();
        await SafeAwaitAsync(busyTask);
        await SafeAwaitAsync(liveDisplayTask);

        AnsiConsole.WriteLine();

        if (!result.Success)
        {
            WriteTurnError(result.Error);
            return;
        }

        // Nothing was streamed: either the turn produced its output outside the streaming path
        // (/new, /reset, the empty-task guard) or it produced nothing at all. Both used to print
        // absolutely nothing, so "Session reset" and "no response" looked identical to a no-op.
        if (sb.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(result.Output))
                AnsiConsole.WriteLine(result.Output);
            else
                AnsiConsole.MarkupLine("[dim](no response returned)[/]");
            AnsiConsole.WriteLine();
        }

        if (result.SpawnedSubAgents.Count > 0)
            AnsiConsole.MarkupLine($"[bold blue]↳[/] Spawned [bold]{result.SpawnedSubAgents.Count}[/] sub-agent(s)");
    }

    /// <summary>Repaint interval for the streaming display's busy line.</summary>
    private const int SpinnerTickMs = 120;

    /// <summary>
    /// Spinner shown from the moment a turn is queued until the first streamed chunk (or the turn
    /// ending without ever streaming one). Returns once either happens, releasing Spectre's
    /// interactive exclusivity lock so the streaming <c>Live</c> display can take over — which then
    /// carries the busy line itself, so the animation is continuous across the whole turn.
    /// </summary>
    private async Task RunBusyIndicatorAsync(
        Task handoff, Task<AgentResult> completed, Func<string> label, DateTime startedAt)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("dodgerblue1"))
            .StartAsync("[dim]Sending...[/]", async ctx =>
            {
                var settled = Task.WhenAny(handoff, completed);
                while (await Task.WhenAny(settled, Task.Delay(SpinnerTickMs)) != settled)
                {
                    // Same reason as the streaming display: hold the paint while a tool is reading
                    // the user's answer off the terminal.
                    if (ConsoleGate.InteractiveReadInProgress)
                        continue;

                    var elapsed = (int)(DateTime.UtcNow - startedAt).TotalSeconds;
                    // Queue depth still counts this command while it waits, hence the -1: what the
                    // user wants to know is how much other work (channel, web, cron) is in front of
                    // them, not that their own message exists.
                    var ahead   = Math.Max(0, _commandQueue.GetTotalQueueCount() - 1);
                    ctx.Status(ahead > 0
                        ? $"[dim]Queued behind {ahead} command(s)... {elapsed}s[/]"
                        : $"[dim]{Markup.Escape(label())}... {elapsed}s[/]");
                    ctx.Refresh();
                }
            });
    }

    private static void WriteTurnError(string? error)
    {
        var message = string.IsNullOrWhiteSpace(error) ? "The turn failed without reporting a reason." : error;
        AnsiConsole.Write(new Panel(new Markup($"[red]{Markup.Escape(message)}[/]"))
        {
            Header      = new PanelHeader("[bold red] ✗ Turn failed [/]", Justify.Left),
            Border      = BoxBorder.Rounded,
            BorderStyle = Style.Parse("red"),
            Padding     = new Padding(1, 0),
        });
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Awaits a display task purely for its teardown side effects. A failure inside the spinner
    /// or live view must not replace the turn's own result (or error) with a rendering error.
    /// </summary>
    private async Task SafeAwaitAsync(Task? task)
    {
        if (task == null) return;
        try { await task; }
        catch (Exception ex) { _logger.LogDebug(ex, "CLI display task failed during teardown."); }
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
                table.AddRow($"[bold white]/{Markup.Escape(row[0])}[/]", $"[dim]{Markup.Escape(row[1])}[/]");
            AnsiConsole.MarkupLine($"\n[bold dodgerblue1]{header}[/]");
            AnsiConsole.Write(table);
        }

        AddSection("General Commands", new[]
        {
            new[] { "help",                "Show this help message" },
            new[] { "new",                 "Archive the current session and start fresh" },
            new[] { "reset",               "Alias for /new" },
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
            new[] { "agents stats",        "Show processor stats and any long-running commands" },
            new[] { "agents pause <id>",   "Pause a running sub-agent" },
            new[] { "agents resume <id>",  "Resume a paused sub-agent" },
            new[] { "agents stop <id>",    "Gracefully stop a sub-agent" },
            new[] { "agents kill [<id>]",  "Kill a sub-agent (or all with no id)" },
        });

        AddSection("HITL (Human-in-the-Loop)", new[]
        {
            new[] { "hitl",                "List pending approvals and questions" },
            new[] { "hitl approve <id>",   "Approve a pending tool execution" },
            new[] { "hitl reject <id>",    "Reject a pending tool execution" },
            new[] { "hitl reply <id> <a>", "Answer a question asked by a background session" },
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
        AnsiConsole.MarkupLine("[dim]Use [bold white]/skill <name>[/] for details.[/]");
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

    private void ShowHitlPending()
    {
        var pending   = _hitlManager.GetPending();
        var questions = _hitlManager.GetPendingQuestions();

        if (pending.Count == 0 && questions.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No pending HITL approvals or questions.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        if (questions.Count > 0)
        {
            var qTable = new Table()
                .Border(TableBorder.Rounded).BorderColor(Color.Yellow)
                .Title($"[bold] Pending Questions ({questions.Count}) [/]")
                .AddColumn(new TableColumn("[bold]ID[/]").Width(10))
                .AddColumn(new TableColumn("[bold]Question[/]"))
                .AddColumn(new TableColumn("[bold]Waiting[/]").Width(9).RightAligned());

            foreach (var q in questions)
            {
                var elapsed = (DateTime.UtcNow - q.CreatedAt).TotalSeconds;
                qTable.AddRow(
                    $"[bold yellow]{Markup.Escape(q.QuestionId)}[/]",
                    Markup.Escape(q.Question.Length > 90 ? q.Question[..87] + "…" : q.Question),
                    $"[dodgerblue1]{(elapsed < 60 ? $"{elapsed:F0}s" : $"{elapsed / 60:F0}m")}[/]");
            }

            AnsiConsole.Write(qTable);
            AnsiConsole.MarkupLine("[dim]  /hitl reply <id> <your answer>[/]");
            AnsiConsole.WriteLine();
        }

        if (pending.Count == 0)
            return;

        var table = new Table()
            .Border(TableBorder.Rounded).BorderColor(Color.Yellow)
            .Title($"[bold] Pending Approvals ({pending.Count}) [/]")
            .AddColumn(new TableColumn("[bold]ID[/]").Width(10))
            .AddColumn(new TableColumn("[bold]Trigger[/]").Width(10))
            .AddColumn(new TableColumn("[bold]Tool / Description[/]"))
            .AddColumn(new TableColumn("[bold]Waiting[/]").Width(9).RightAligned());

        foreach (var (req, createdAt) in pending.OrderBy(p => p.CreatedAt))
        {
            var elapsed = (DateTime.UtcNow - createdAt).TotalSeconds;
            var wait    = elapsed < 60 ? $"{elapsed:F0}s" : $"{elapsed / 60:F0}m";
            table.AddRow(
                $"[bold yellow]{Markup.Escape(req.ApprovalId)}[/]",
                $"[dim]{req.Trigger}[/]",
                Markup.Escape(req.Description),
                $"[dodgerblue1]{wait}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[dim]  /hitl approve <id>   /hitl reject <id> [reason][/]");
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

        // The first question after "why did everything stop?" is "what is still running?", and the
        // answer used to be available only by reading the log file.
        var stuck = _commandProcessor.GetLongRunningCommands(StuckCommandDisplayThreshold);
        if (stuck.Count > 0)
        {
            var stuckTable = new Table()
                .Border(TableBorder.Rounded).BorderColor(Color.Yellow)
                .Title($"[bold] Long-Running Commands ({stuck.Count}) [/]")
                .AddColumn(new TableColumn("[bold]Lane[/]").Width(11))
                .AddColumn(new TableColumn("[bold]RunId[/]").Width(38))
                .AddColumn(new TableColumn("[bold]Elapsed[/]").Width(9).RightAligned())
                .AddColumn(new TableColumn("[bold]Session[/]"));

            foreach (var c in stuck)
            {
                var session = c.SessionKey.Length > 32 ? "…" + c.SessionKey[^31..] : c.SessionKey;
                stuckTable.AddRow(
                    $"[bold]{Markup.Escape(c.Lane.ToString())}[/]",
                    $"[grey]{Markup.Escape(c.RunId)}[/]",
                    $"[yellow]{c.Elapsed:hh\\:mm\\:ss}[/]",
                    $"[dim]{Markup.Escape(session)}[/]");
            }

            AnsiConsole.Write(stuckTable);
            AnsiConsole.MarkupLine(
                "[dim]A command on the serial [bold]Main[/] lane blocks this prompt until it returns.[/]");
        }

        AnsiConsole.WriteLine();
    }

    /// <summary>How long a command must have been running before /agents stats calls it out.</summary>
    private static readonly TimeSpan StuckCommandDisplayThreshold = TimeSpan.FromMinutes(2);

    // ─────────────────────────────────────────────────────────────────────────
    // Agent management commands
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleAgentPause(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) { AnsiConsole.MarkupLine("[dim]Usage:[/] [bold white]/agents pause <runId>[/]"); return; }
        if (_subAgentManager.PauseSubAgent(runId))
            AnsiConsole.MarkupLine($"[bold yellow]⏸[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] paused.");
        else
            AnsiConsole.MarkupLine($"[bold red]✗[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] not found.");
        AnsiConsole.WriteLine();
    }

    private void HandleAgentResume(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) { AnsiConsole.MarkupLine("[dim]Usage:[/] [bold white]/agents resume <runId>[/]"); return; }
        if (_subAgentManager.ResumeSubAgent(runId))
            AnsiConsole.MarkupLine($"[bold green]▶[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] resumed.");
        else
            AnsiConsole.MarkupLine($"[bold red]✗[/]  Sub-agent [dim]{Markup.Escape(runId)}[/] not found.");
        AnsiConsole.WriteLine();
    }

    private async Task HandleAgentStopAsync(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) { AnsiConsole.MarkupLine("[dim]Usage:[/] [bold white]/agents stop <runId>[/]"); return; }
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
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

}

// ─────────────────────────────────────────────────────────────────────────
// PrettyPrompt callbacks — slash-command completion + Esc-to-clear
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Completion is scoped to slash commands (<c>/help</c>, <c>/agents</c>, ...) only. Plain-word
/// completion was tried and dropped: PrettyPrompt's completion window, once open, never truly hides
/// itself — <c>SlidingArrayWindow.IsEmpty</c> (which gates whether the renderer draws the box) only
/// reflects whether the original candidate list was empty, not whether any candidate still matches
/// what's typed, so it kept showing the full list for ordinary chat text like "hello".
/// <para>
/// Requiring a leading '/' sidesteps that for the common case (free-form chat basically never starts
/// with '/'), and <see cref="GetSpanToReplaceByCompletionAsync"/> below adds a second-layer fix for
/// slash-typos: it collapses the replacement span to an empty span at the caret whenever nothing
/// matches, which — per PrettyPrompt's own close condition (span start AND end both differing from
/// the prior keystroke) — forces the pane to actually close instead of lingering with a stale list.
/// </para>
/// </summary>
internal sealed class ReplPromptCallbacks : PromptCallbacks
{
    private static readonly (string Command, string Description)[] Commands =
    [
        ("help",              "Show this help message"),
        ("new",               "Archive the current session and start fresh"),
        ("reset",             "Alias for /new"),
        ("status",            "Show agent status"),
        ("tools",             "List available tools"),
        ("skills",            "List all registered skills"),
        ("skill",             "Show detailed info for a skill: /skill <name>"),
        ("doctor",            "Run health checks (/doctor fix, /doctor config <request>)"),
        ("exit",              "Quit AgentFox"),
        ("agents",            "List/manage sub-agents (/agents stats|pause|resume|stop|kill)"),
        ("hitl",              "List/respond to pending approvals (/hitl approve|reject <id>)"),
        ("install-service",   "Install as a system service"),
        ("uninstall-service", "Remove the system service"),
        ("start-service",     "Start the system service"),
        ("stop-service",      "Stop the system service"),
        ("restart-service",   "Restart the system service"),
        ("service-status",    "Show service status"),
        ("service-config",    "Show service configuration"),
    ];

    // Built once (not per keystroke): avoids reallocating a CompletionItem + closure per
    // command on every call, and keeps GetCompletionItemsAsync a plain field read.
    private static readonly IReadOnlyList<CompletionItem> AllCompletionItems = BuildCompletionItems();

    private static CompletionItem[] BuildCompletionItems()
    {
        var items = new CompletionItem[Commands.Length];
        for (int i = 0; i < Commands.Length; i++)
        {
            var description = Commands[i].Description;
            items[i] = new CompletionItem(
                replacementText: Commands[i].Command,
                getExtendedDescription: _ => Task.FromResult<FormattedString>(description));
        }
        return items;
    }

    private static bool IsSlashCommand(string text) => text.Length > 0 && text[0] == '/';

    // Index just past the command word: the first space/newline after the slash, or end of text.
    private static int GetFirstTokenEnd(string text)
    {
        var idx = text.IndexOfAny(TokenBreakChars, 1);
        return idx < 0 ? text.Length : idx;
    }

    private static readonly char[] TokenBreakChars = [' ', '\n'];

    private static bool IsOnFirstToken(string text, int caret)
        => IsSlashCommand(text) && caret >= 1 && caret <= GetFirstTokenEnd(text);

    protected override Task<bool> ShouldOpenCompletionWindowAsync(
        string text, int caret, KeyPress keyPress, CancellationToken cancellationToken)
        => Task.FromResult(IsOnFirstToken(text, caret));

    protected override Task<IReadOnlyList<CompletionItem>> GetCompletionItemsAsync(
        string text, int caret, TextSpan spanToBeReplaced, CancellationToken cancellationToken)
        => Task.FromResult(IsOnFirstToken(text, caret) ? AllCompletionItems : []);

    // Determines what gets replaced if a completion is committed — but also doubles as the mechanism
    // that closes the completion pane once nothing matches (see class remarks above). While the typed
    // prefix still matches at least one command, this returns the real command-word span, [1, tokenEnd).
    // Once nothing matches, it collapses to an empty span at the caret, which differs in BOTH start and
    // end from the previous keystroke's span — the one condition PrettyPrompt itself uses to force-close
    // an open completion pane mid-session.
    protected override Task<TextSpan> GetSpanToReplaceByCompletionAsync(
        string text, int caret, CancellationToken cancellationToken)
    {
        if (!IsSlashCommand(text) || caret < 1)
            return Task.FromResult(new TextSpan(caret, 0));

        var tokenEnd = GetFirstTokenEnd(text);
        if (caret > tokenEnd)
            return Task.FromResult(new TextSpan(caret, 0));

        var prefix = text[1..caret];
        var anyMatch = Commands.Any(c => c.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(anyMatch ? TextSpan.FromBounds(1, tokenEnd) : new TextSpan(caret, 0));
    }

    protected override Task<(string Text, int Caret)> FormatInput(
        string text, int caret, KeyPress keyPress, CancellationToken cancellationToken)
    {
        var formatted = keyPress.ConsoleKeyInfo.Key == ConsoleKey.Escape
            ? (Text: string.Empty, Caret: 0)
            : (Text: text, Caret: caret);

        // Tracked so GatedConsole knows whether it may surrender the line to flush background
        // output. Interrupting an empty prompt costs nothing; interrupting a half-typed command
        // would throw the operator's work away to print a notification.
        _inputIsEmpty = formatted.Text.Length == 0;

        return Task.FromResult(formatted);
    }

    private volatile bool _inputIsEmpty = true;

    /// <summary>True when the prompt's edit buffer holds nothing worth preserving.</summary>
    public bool InputIsEmpty => _inputIsEmpty;

    /// <summary>
    /// Called by the REPL before each read. <see cref="FormatInput"/> only fires once a key is
    /// pressed, so without this the buffer state from the previous line would still be reported for
    /// a fresh, empty prompt.
    /// </summary>
    public void ResetInput() => _inputIsEmpty = true;
}
