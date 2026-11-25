using AgentFox.LLM.Subscription;

namespace AgentFox.LLM.ClaudeCode;

/// <summary>Claude Code's recent use (<see cref="SubscriptionActivity"/>), plus the subscription usage the CLI reports
/// on each call. Memory only, a fixed handful of values (§0.1).</summary>
public sealed class ClaudeCodeActivity : SubscriptionActivity
{
    public ClaudeCodeUsageWindows? Usage { get; private set; }
    public DateTimeOffset? UsageReadUtc { get; private set; }

    public void RecordUsage(ClaudeCodeUsageWindows usage)
    {
        lock (Gate) { Usage = usage; UsageReadUtc = DateTimeOffset.UtcNow; }
    }
}
