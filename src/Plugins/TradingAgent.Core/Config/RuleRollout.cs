namespace TradingAgent.Config;

/// <summary>
/// How far a new protection rule is trusted to act.
///
/// <para>
/// <b>Why Shadow exists.</b> A rule that decides whether a protective stop is placed cannot be proved on a simulator
/// alone, and the obvious "run it in Shadow execution mode" does not work for it: Shadow mode's orders are simulated
/// and never appear in the broker's book, so a rule that reads the book would see every one of them as gone. Nor can
/// a second agent watch the same account, because the broker allows one session per account. So the rule runs
/// INSIDE the live agent, beside the rule it replaces: the old rule keeps deciding, the new one is evaluated against
/// the real book on every pass, and every pass where they disagree is written to the decision log
/// (<see cref="TradingAgent.Persistence.IRuleShadowLog"/>). The owner reads that log over several live sessions
/// before switching the rule On (owner, 2026-11-28).
/// </para>
/// </summary>
public enum RuleRollout
{
    /// <summary>Not evaluated at all.</summary>
    Off = 0,

    /// <summary>Evaluated and logged where it would have decided differently; acts on nothing, alerts nobody.</summary>
    Shadow = 1,

    /// <summary>Decides.</summary>
    On = 2
}
