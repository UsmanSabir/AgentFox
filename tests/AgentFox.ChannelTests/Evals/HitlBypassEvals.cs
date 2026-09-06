using AgentFox.Hitl;
using AgentFox.Sessions;

namespace AgentFox.ChannelTests.Evals;

/// <summary>
/// Who is exempt from the human approval step, and who is not.
///
/// Worth pinning because every rule here is a config list, every one of them widens who can act
/// without a human, and the failure mode is silent in the permissive direction: a typo that makes
/// a rule match too much removes a safeguard and nothing looks different. The asymmetry to hold
/// on to is that a bypass never REFUSES anything, so a wrong answer here is only ever "asked
/// nobody when it should have asked someone".
/// </summary>
[TestClass]
public sealed class HitlBypassEvals
{
    private static HitlBypassPolicy Policy(Action<HitlBypassConfig> configure)
    {
        var bypass = new HitlBypassConfig();
        configure(bypass);
        return new HitlBypassPolicy(new HitlConfig { Bypass = bypass });
    }

    private static SessionInfo Session(
        SessionOrigin origin = SessionOrigin.Channel,
        string? channelId = null,
        string? channelType = null) =>
        new() { Origin = origin, ChannelId = channelId, ChannelType = channelType };

    [TestMethod]
    public void BypassDecisionTable()
    {
        EvalSuite.Run("hitl-bypass", [
            EvalSuite.Case("nothing_configured_bypasses_nothing",
                () => !Policy(_ => { }).IsBypassed(Session(), agentRole: "main"),
                "An empty bypass config must exempt nobody. Defaults decide what an operator who "
                + "configured nothing gets, and that has to be the safe answer."),

            EvalSuite.Case("null_session_still_bypasses_nothing_by_default",
                () => !Policy(_ => { }).IsBypassed(null, agentRole: null),
                "A missing session must not be treated as a trusted one — unknown is not exempt."),

            EvalSuite.Case("auto_approve_all_bypasses_everything",
                () => Policy(c => c.AutoApproveAll = true).IsBypassed(null, agentRole: null),
                "AutoApproveAll is the documented fully-trusted escape hatch and must work "
                + "even with no session and no role."),

            EvalSuite.Case("role_matches_case_insensitively",
                () => Policy(c => c.Roles.Add("Trader")).IsBypassed(null, agentRole: "trader"),
                "Roles come from config and from a live agent; casing must not decide safety."),

            EvalSuite.Case("role_rule_does_not_match_a_different_role",
                () => !Policy(c => c.Roles.Add("Trader")).IsBypassed(null, agentRole: "researcher"),
                "A role list must not match roles it does not name."),

            EvalSuite.Case("channel_id_rule_matches_that_channel_only",
                () => Policy(c => c.ChannelIds.Add("admin-chat"))
                          .IsBypassed(Session(channelId: "admin-chat"), null)
                      && !Policy(c => c.ChannelIds.Add("admin-chat"))
                          .IsBypassed(Session(channelId: "public-chat"), null),
                "A private admin chat being exempt must not exempt every chat."),

            EvalSuite.Case("channel_type_rule_matches_the_type",
                () => Policy(c => c.ChannelTypes.Add("Console"))
                          .IsBypassed(Session(channelType: "console"), null),
                "Channel type is a display-cased string in config and may arrive either way."),

            EvalSuite.Case("session_origin_rule_matches_the_origin",
                () => Policy(c => c.SessionOrigins.Add("CronJob"))
                          .IsBypassed(Session(origin: SessionOrigin.CronJob), null)
                      && !Policy(c => c.SessionOrigins.Add("CronJob"))
                          .IsBypassed(Session(origin: SessionOrigin.Channel), null),
                "Origin rules exist so unattended runs are not blocked waiting for a human "
                + "who was never going to see the prompt — but only the named origins."),

            EvalSuite.Case("a_null_channel_id_does_not_match_a_configured_one",
                () => !Policy(c => c.ChannelIds.Add("admin-chat")).IsBypassed(Session(), null),
                "A session with no channel must not match a channel rule. Unknown is not a match."),
        ]);
    }
}
