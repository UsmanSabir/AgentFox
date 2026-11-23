using AgentFox.Plugins.Interfaces;

namespace AgentFox.Agents;

/// <summary>
/// What the tool-approval gate decided for one call: whether it may run, what to tell the model when it may not, and — when a
/// person approved it — that approval, which the gateway puts in scope for the one execution (<see cref="ToolApprovals"/>).
/// </summary>
public sealed record ToolApprovalVerdict(bool Allowed, string? Message = null, HumanToolApproval? Approval = null)
{
    public static ToolApprovalVerdict Allow(HumanToolApproval? approval = null) => new(true, null, approval);
    public static ToolApprovalVerdict Block(string? message = null) => new(false, message);
}
