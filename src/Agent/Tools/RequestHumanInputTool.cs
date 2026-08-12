using AgentFox.Agents;
using AgentFox.Channels;
using AgentFox.Helpers;
using AgentFox.Hitl;
using AgentFox.Plugins.Interfaces;
using AgentFox.Sessions;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AgentFox.Tools;

/// <summary>
/// HITL Mode 2 — Agent-requested checkpoint.
///
/// When the LLM calls this tool, execution blocks — bounded, never indefinitely — until the human
/// replies:
///   • Channel session   → question is sent to the originating channel;
///                         the next message on that channel is returned as the answer.
///   • Console session   → question is printed to the terminal and the answer read back inline,
///                         but only when this session actually owns the terminal.
///   • Everything else   → question is registered by id and broadcast; anyone can answer it with
///     (cron, heartbeat,   <c>/hitl reply &lt;id&gt; &lt;answer&gt;</c> from the CLI, a channel, or the web UI.
///      sub-agent, web)
///
/// The tool intentionally does NOT use /approve /reject — it accepts any free-form reply.
/// Unanswered requests expire (see <see cref="HitlConfig.QuestionTimeoutSeconds"/>) and return a
/// "nobody answered" result, because the caller is holding a lane slot while it waits.
/// </summary>
public class RequestHumanInputTool : BaseTool
{
    private readonly HitlManager _hitlManager;
    private readonly ChannelManager? _channelManager;
    private readonly SessionManager? _sessionManager;
    private readonly ILogger? _logger;

    public RequestHumanInputTool(
        HitlManager hitlManager,
        ChannelManager? channelManager = null,
        SessionManager? sessionManager = null,
        ILogger? logger = null)
    {
        _hitlManager = hitlManager;
        _channelManager = channelManager;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public override string Name => "request_human_input";

    public override string Description =>
        "Pause the current task and ask the human user a question. " +
        "Waits for their reply before continuing. " +
        "Use for clarification, decisions, or confirmations that cannot be inferred from context.";

    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["question"] = new()
        {
            Type = "string",
            Description = "The question to ask the user.",
            Required = true
        },
        ["context"] = new()
        {
            Type = "string",
            Description = "Optional extra context to help the user understand the question.",
            Required = false
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var question = arguments.GetValueOrDefault("question")?.ToString();
        if (string.IsNullOrWhiteSpace(question))
            return ToolResult.Fail("question is required");

        var context = arguments.GetValueOrDefault("context")?.ToString();

        // Resolve the originating channel from the ambient session key set by FoxAgent.ProcessAsync
        var sessionKey = FoxAgent.CurrentSessionKey.Value;
        var sessionInfo = sessionKey != null ? _sessionManager?.GetSession(sessionKey) : null;
        var channelId = sessionInfo?.ChannelId;

        var msgLines = new List<string>
        {
            "💬 **Agent asks:**",
            question
        };
        if (!string.IsNullOrWhiteSpace(context))
        {
            msgLines.Add(string.Empty);
            msgLines.Add($"**Context:** {context}");
        }
        msgLines.Add(string.Empty);
        msgLines.Add("_Reply to continue._");
        var msg = string.Join("\n", msgLines);

        // ── Channel session ───────────────────────────────────────────────────
        if (channelId != null && _channelManager != null)
        {
            var channel = _channelManager.Channels.Values
                .FirstOrDefault(c => c.ChannelId == channelId && c.IsConnected);

            if (channel != null)
            {
                await channel.SendToTargetAsync(string.Empty, msg);
                _logger?.LogInformation(
                    "HITL free-form request sent to channel {ChannelId}", channelId);

                var response = await _hitlManager.RequestFreeFormAsync(channelId);
                return string.IsNullOrEmpty(response)
                    ? ToolResult.Ok(NoAnswer)
                    : ToolResult.Ok(response);
            }
        }

        // ── Interactive console session ───────────────────────────────────────
        // Only the console session may read the terminal, and only while a real interactive
        // console exists. This used to be an unconditional fallback, so a cron or sub-agent turn —
        // whose session has no ChannelId and therefore always landed here — parked a
        // Console.ReadLine() on a background thread. That reader competed with the REPL's own key
        // reader for every keystroke the operator typed: input vanished, and what did arrive went
        // to a plain ReadLine with no history and no completion pane. It also never returned, so
        // the turn held its lane slot for the life of the process.
        if (IsInteractiveConsoleSession(sessionInfo))
            return await AskOnConsoleAsync(question, context);

        // ── Everything else: a question anyone can answer, by id ─────────────
        return await AskByIdAsync(question, context, msg, sessionKey, channelId);
    }

    /// <summary>Returned when the human never replies — distinct from an empty answer.</summary>
    private const string NoAnswer =
        "No answer: the human did not respond before the request expired. " +
        "Continue with your best judgement, state the assumption you made, or stop and report " +
        "that you are blocked. Do not ask the same question again immediately.";

    /// <summary>
    /// True only for a session that actually owns the terminal: the interactive console session,
    /// on a process whose stdin is a real console. A cron, heartbeat, sub-agent, channel or web
    /// session fails this even though a console exists — the terminal is not theirs to read.
    /// </summary>
    private static bool IsInteractiveConsoleSession(SessionInfo? sessionInfo)
        => sessionInfo?.Origin == SessionOrigin.Console
           && !Console.IsInputRedirected
           && !Console.IsOutputRedirected;

    private static async Task<ToolResult> AskOnConsoleAsync(string question, string? context)
    {
        // The gate does two things here: background producers stop writing over the question while
        // the user types it, and the CLI's streaming spinner stops repainting on top of the answer.
        using var _ = ConsoleGate.BeginInteractiveRead();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold yellow]💬 Agent asks:[/] {Markup.Escape(question)}");
        if (!string.IsNullOrWhiteSpace(context))
            AnsiConsole.MarkupLine($"[dim]Context: {Markup.Escape(context)}[/]");
        AnsiConsole.MarkupLine("[dim]Type your answer and press Enter (Esc to skip).[/]");
        AnsiConsole.Markup("[bold]>[/] ");

        var answer = await ConsoleLineReader.ReadLineAsync(ConsoleAnswerTimeout);
        return ToolResult.Ok(string.IsNullOrWhiteSpace(answer) ? NoAnswer : answer);
    }

    /// <summary>
    /// Upper bound on an inline console answer. The REPL is blocked on this turn while it runs, so
    /// an unanswered question here is the operator's own prompt frozen — bounded, not indefinite.
    /// </summary>
    private static readonly TimeSpan ConsoleAnswerTimeout = TimeSpan.FromMinutes(10);

    private async Task<ToolResult> AskByIdAsync(
        string question, string? context, string message, string? sessionKey, string? channelId)
    {
        var pending = new HitlQuestion(
            Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            sessionKey ?? string.Empty,
            channelId,
            question,
            DateTime.UtcNow);

        // Broadcast so the question is answerable from wherever the operator actually is, rather
        // than only from a terminal they may not be sitting at.
        var deliveredTo = 0;
        if (_channelManager != null)
        {
            try
            {
                deliveredTo = await _channelManager.BroadcastAsync(
                    $"{message}\n\n_Reply with_ `/hitl reply {pending.QuestionId} <your answer>`");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to broadcast HITL question {QuestionId}", pending.QuestionId);
            }
        }

        ConsoleGate.Write(() =>
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[bold yellow]💬 Agent asks[/] [[{Markup.Escape(pending.QuestionId)}]] {Markup.Escape(question)}");
            if (!string.IsNullOrWhiteSpace(context))
                AnsiConsole.MarkupLine($"[dim]Context: {Markup.Escape(context)}[/]");
            AnsiConsole.MarkupLine(
                $"[dim]Answer with [bold]/hitl reply {Markup.Escape(pending.QuestionId)} <your answer>[/][/]");
        });

        _logger?.LogInformation(
            "HITL question [{QuestionId}] raised from session {SessionKey} (delivered to {Count} channel(s))",
            pending.QuestionId, sessionKey, deliveredTo);

        var answer = await _hitlManager.AskAsync(pending);
        return ToolResult.Ok(string.IsNullOrWhiteSpace(answer) ? NoAnswer : answer);
    }
}
