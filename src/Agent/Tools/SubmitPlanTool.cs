using AgentFox.Agents;
using AgentFox.Channels;
using AgentFox.Hitl;
using AgentFox.Planning;
using AgentFox.Plugins.Interfaces;
using AgentFox.Sessions;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AgentFox.Tools;

/// <summary>
/// The single human gate between the plan and execute phases of the
/// research → plan → execute workflow.
///
/// The model calls this once it has finished researching. The tool records the plan,
/// flips the session into <see cref="PlanPhase.AwaitingApproval"/>, and blocks for a
/// human /approve or /reject — reusing <see cref="HitlManager.RequestApprovalAsync"/>.
///   • approve → phase becomes <see cref="PlanPhase.Execute"/> (mutating tools unlock).
///   • reject  → phase returns to <see cref="PlanPhase.Research"/> with the reason fed back.
///
/// Trusted sessions/agents (per <see cref="HitlBypassPolicy"/>) auto-approve without
/// waiting for a human.
/// </summary>
public class SubmitPlanTool : BaseTool
{
    private readonly PlanStateStore _planStore;
    private readonly HitlManager _hitlManager;
    private readonly HitlBypassPolicy _bypass;
    private readonly Func<string?> _roleProvider;
    private readonly ChannelManager? _channelManager;
    private readonly SessionManager? _sessionManager;
    private readonly ILogger? _logger;

    public SubmitPlanTool(
        PlanStateStore planStore,
        HitlManager hitlManager,
        HitlBypassPolicy bypass,
        Func<string?> roleProvider,
        ChannelManager? channelManager = null,
        SessionManager? sessionManager = null,
        ILogger? logger = null)
    {
        _planStore = planStore;
        _hitlManager = hitlManager;
        _bypass = bypass;
        _roleProvider = roleProvider;
        _channelManager = channelManager;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public override string Name => "submit_plan";

    public override string Description =>
        "Submit your research-backed, step-by-step execution plan for human approval. " +
        "Call this only after researching with read-only tools. Mutating/side-effecting tools " +
        "stay locked until the plan is approved. Returns once the human approves or rejects.";

    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["plan"] = new()
        {
            Type = "string",
            Description = "The full step-by-step plan to execute, grounded in your research findings.",
            Required = true
        },
        ["summary"] = new()
        {
            Type = "string",
            Description = "Optional one-line summary shown above the plan in the approval prompt.",
            Required = false
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var plan = arguments.GetValueOrDefault("plan")?.ToString();
        if (string.IsNullOrWhiteSpace(plan))
            return ToolResult.Fail("plan is required");

        var summary = arguments.GetValueOrDefault("summary")?.ToString();

        var sessionKey = FoxAgent.CurrentSessionKey.Value ?? string.Empty;
        var sessionInfo = _sessionManager?.GetSession(sessionKey);

        var state = _planStore.For(sessionKey);
        state.PlanText = plan;
        state.RejectionReason = null;
        state.Phase = PlanPhase.AwaitingApproval;

        // ── Bypass: trusted session/agent auto-approves without a human ──────────
        if (_bypass.IsBypassed(sessionInfo, _roleProvider()))
        {
            state.Phase = PlanPhase.Execute;
            state.ApprovedAt = DateTime.UtcNow;
            _logger?.LogInformation("Plan auto-approved for trusted session {SessionKey}", sessionKey);
            return ToolResult.Ok("Plan auto-approved (trusted session). Proceed to execute the steps now.");
        }

        // ── Notify the human and block for /approve or /reject ──────────────────
        var channelId = sessionInfo?.ChannelId;
        var approvalId = Guid.NewGuid().ToString("N")[..8].ToUpper();

        var header = summary is { Length: > 0 } ? summary + "\n\n" : string.Empty;
        var msg =
            $"📋 **Plan Approval Required** `[{approvalId}]`\n\n" +
            header + plan +
            $"\n\n`/approve {approvalId}` — execute the plan\n" +
            $"`/reject {approvalId} [reason]` — send back for revision";

        if (channelId != null && _channelManager != null)
        {
            var channel = _channelManager.Channels.Values
                .FirstOrDefault(c => c.ChannelId == channelId && c.IsConnected);
            if (channel != null)
                await channel.SendToTargetAsync(string.Empty, msg);
        }
        else
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold yellow]📋 Plan Approval Required[/] [[{approvalId}]]");
            if (summary is { Length: > 0 })
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(summary)}[/]");
            AnsiConsole.WriteLine(plan);
            AnsiConsole.MarkupLine($"[dim]Type [bold]hitl approve {approvalId}[/] or [bold]hitl reject {approvalId} [reason][/][/]");
        }

        var request = new HitlRequest(
            approvalId, sessionKey, channelId,
            HitlTrigger.Checkpoint, "submit_plan", plan);

        var decision = await _hitlManager.RequestApprovalAsync(request);

        if (decision.Approved)
        {
            state.Phase = PlanPhase.Execute;
            state.ApprovedAt = DateTime.UtcNow;
            return ToolResult.Ok("Plan approved. Proceed to execute the steps now.");
        }

        state.Phase = PlanPhase.Research;
        state.RejectionReason = decision.Feedback;
        return ToolResult.Ok(
            $"Plan rejected: {decision.Feedback ?? "no reason given"}. " +
            "Revise the plan based on this feedback and call submit_plan again.");
    }
}
