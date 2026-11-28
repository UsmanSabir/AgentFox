namespace TradingAgent.Persistence;

/// <summary>
/// One pass where a rule in <see cref="TradingAgent.Config.RuleRollout.Shadow"/> would have decided differently from
/// the rule that actually decided.
/// </summary>
/// <param name="Rule">Which rule, e.g. <c>protective-stop.live-coverage</c>.</param>
/// <param name="CurrentDecision">What the acting rule did, in words an operator can read (<c>Skip</c>, <c>Place 100</c>).</param>
/// <param name="ShadowDecision">What the shadow rule would have done.</param>
/// <param name="Detail">Why, from the shadow rule's own reasoning.</param>
public sealed record RuleShadowDecision(
    DateTime RecordedUtc,
    string Rule,
    string? Symbol,
    string? SubjectId,
    string CurrentDecision,
    string ShadowDecision,
    string Detail);

/// <summary>
/// The decision log behind <see cref="TradingAgent.Config.RuleRollout.Shadow"/>: durable, so a rule can be judged over
/// several live sessions on a host whose text log keeps three days at Warning level.
///
/// <para><b>Retention (§0.1):</b> pruned by the retention worker on the ledger's schedule.</para>
/// </summary>
public interface IRuleShadowLog
{
    Task RecordRuleShadowDecisionAsync(RuleShadowDecision decision, CancellationToken ct = default);

    Task<IReadOnlyList<RuleShadowDecision>> GetRuleShadowDecisionsAsync(
        DateTime sinceUtc, string? rule = null, CancellationToken ct = default);

    Task<int> PruneRuleShadowDecisionsAsync(DateTime before, CancellationToken ct = default);
}
