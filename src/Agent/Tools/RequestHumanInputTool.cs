using AgentFox.Agents;
using AgentFox.Channels;
using AgentFox.Hitl;
using AgentFox.Plugins.Interfaces;
using AgentFox.Sessions;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AgentFox.Tools;

/// <summary>
/// HITL Mode 2 — Agent-requested checkpoint.
///
/// When the LLM calls this tool, execution blocks until the human replies:
///   • Channel session  → question is sent to the originating channel;
///                        the next message on that channel is returned as the answer.
///   • Console session  → question is printed to stdout; answer is read from stdin.
///
/// The tool intentionally does NOT use /approve /reject — it accepts any free-form reply.
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
                return ToolResult.Ok(response);
            }
        }

        // ── Console fallback ─────────────────────────────────────────────────
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold yellow]💬 Agent asks:[/] {Markup.Escape(question)}");
        if (!string.IsNullOrWhiteSpace(context))
            AnsiConsole.MarkupLine($"[dim]Context: {Markup.Escape(context)}[/]");
        AnsiConsole.Markup("[bold]>[/] ");
        var consoleResponse = Console.ReadLine() ?? string.Empty;
        return ToolResult.Ok(consoleResponse);
    }
}
