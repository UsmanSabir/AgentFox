using AgentFox.Sessions;

namespace AgentFox.Hitl;

/// <summary>
/// Evaluates whether a given session/agent context is exempt from HITL approval.
/// Consulted by both the per-tool approval gate and the plan-approval flow
/// (<see cref="AgentFox.Tools.SubmitPlanTool"/>) so trusted contexts skip the human step.
/// </summary>
public sealed class HitlBypassPolicy
{
    private readonly HitlBypassConfig _cfg;

    public HitlBypassPolicy(HitlConfig hitl) => _cfg = hitl.Bypass ?? new HitlBypassConfig();

    /// <summary>
    /// Returns true if approval should be skipped for the given session and agent role.
    /// A null session matches only the role/global rules.
    /// </summary>
    public bool IsBypassed(SessionInfo? session, string? agentRole)
    {
        if (_cfg.AutoApproveAll)
            return true;

        if (agentRole != null &&
            _cfg.Roles.Contains(agentRole, StringComparer.OrdinalIgnoreCase))
            return true;

        if (session != null)
        {
            if (session.ChannelId != null &&
                _cfg.ChannelIds.Contains(session.ChannelId, StringComparer.OrdinalIgnoreCase))
                return true;

            if (session.ChannelType != null &&
                _cfg.ChannelTypes.Contains(session.ChannelType, StringComparer.OrdinalIgnoreCase))
                return true;

            if (_cfg.SessionOrigins.Contains(session.Origin.ToString(), StringComparer.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
