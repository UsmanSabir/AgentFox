namespace AgentFox.Plugins.Interfaces;

/// <summary>
/// A tool that runs ONLY after a person approved this exact call, whatever the host's HITL configuration says.
///
/// <para>
/// <c>Hitl.RequireApprovalForTools</c> is configuration: a deployment can leave a tool out of it, turn HITL off, or bypass it
/// for a channel or a role. That is right for most tools. It is wrong for a tool whose effect is only legitimate BECAUSE a person
/// agreed to it — arming an order that later fires as the operator's own instruction is the first one. Such a tool implements
/// this interface, and the host then:
/// </para>
/// <list type="bullet">
///   <item><description>asks a person before every call, ignoring <c>Hitl.Enabled</c>, the watched list and every bypass rule
///   (plan enforcement still runs first, as for any tool);</description></item>
///   <item><description>shows the person <see cref="DescribeForApprovalAsync"/>'s text instead of the raw arguments, so the
///   approval is about what will happen and not about the model's prose;</description></item>
///   <item><description>refuses without asking when that method returns a refusal (a call that cannot succeed is not put to a
///   person);</description></item>
///   <item><description>runs the tool with <see cref="ToolApprovals.Current"/> set to the approval, which the tool checks: no
///   approval in scope means no gate asked anyone, and the tool must refuse rather than act.</description></item>
/// </list>
/// </summary>
public interface IHumanApprovedTool : ITool
{
    /// <summary>
    /// What the person is asked to approve, or why the call is refused without asking. Must not change anything.
    /// </summary>
    Task<ToolApprovalPrompt> DescribeForApprovalAsync(
        Dictionary<string, object?> arguments,
        ToolApprovalContext context,
        CancellationToken ct);
}

/// <summary>Where the call comes from, as the host knows it.</summary>
/// <param name="SessionKey">The session the call runs in.</param>
/// <param name="Origin">How that session was opened: <c>Web</c>, <c>Channel</c>, <c>Console</c>… (the host's session origin).
/// Null when the host could not tell.</param>
/// <param name="ChannelType">For a channel session, the channel's type (for example <c>whatsapp-bridge</c>).</param>
public sealed record ToolApprovalContext(string? SessionKey, string? Origin, string? ChannelType);

/// <summary>Either the text a person approves, or the reason the call is refused without asking.</summary>
public sealed record ToolApprovalPrompt(string? Text, string? Refusal)
{
    public static ToolApprovalPrompt Ask(string text) => new(text, null);
    public static ToolApprovalPrompt Refuse(string reason) => new(null, reason);
}

/// <summary>A person's approval of one tool call.</summary>
public sealed record HumanToolApproval(
    string ToolName,
    string ApprovalId,
    string? SessionKey,
    string? Origin,
    string? ChannelType,
    DateTime ApprovedUtc);

/// <summary>
/// The approval the CURRENT tool call runs under. Set by the host around the one execution a person approved and cleared after
/// it, so an approval cannot leak into a later call.
/// </summary>
public static class ToolApprovals
{
    private static readonly AsyncLocal<HumanToolApproval?> s_current = new();

    /// <summary>The approval of the call now running, or null when nobody approved it.</summary>
    public static HumanToolApproval? Current => s_current.Value;

    /// <summary>True when a person approved the running call of <paramref name="toolName"/>.</summary>
    public static bool IsApproved(string toolName) =>
        s_current.Value is { } a && string.Equals(a.ToolName, toolName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Sets the approval for the duration of one execution. For the host's gateway only.</summary>
    public static IDisposable Enter(HumanToolApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var previous = s_current.Value;
        s_current.Value = approval;
        return new Scope(previous);
    }

    private sealed class Scope(HumanToolApproval? previous) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            s_current.Value = previous;
        }
    }
}
