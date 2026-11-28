using System.Reflection;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Feed;
using TradingAgent.Market;
using TradingAgent.Reconciliation;
using TradingAgent.Research;
using TradingAgent.Trading;

namespace TradingAgent.Observability;

/// <summary>
/// What the dead-man reports about one agent (plan step 7): whether it is alive, reading the broker, receiving prices
/// and keeping its stops placed. Served at <c>GET /api/trading/health</c> and pushed to the fleet watchdog as the
/// heartbeat body (<see cref="TradingHeartbeatSender"/>), so the watchdog judges an agent on the same facts its owner
/// would see.
/// </summary>
/// <param name="Status"><c>resting</c> outside a session; inside one <c>ok</c>, <c>degraded</c> or <c>down</c>.</param>
/// <param name="Problems">Stable machine keys, one per problem — see <see cref="TradingHealth.Assess"/>.</param>
public sealed record TradingHealthReport(
    string Status,
    IReadOnlyList<string> Problems,
    DateTime CheckedUtc,
    string? Tenant,
    string Version,
    DateTime StartedUtc,
    bool SessionOpen,
    string MarketPhase,
    string MarketPhaseRollout,
    string? BrokerMarketState,
    DateTime? BrokerMarketStateAtUtc,
    bool ReconciliationHealthy,
    DateTime? ReconciliationCheckedUtc,
    bool ReadSinceStart,
    DateTime? LastPricedUtc,
    DateTime? StopPassUtc,
    int OpenStops,
    IReadOnlyList<ProtectiveStopWorker.UnprotectedStop> Unprotected,
    string TradingStop,
    string ExecutionMode);

public static class TradingHealth
{
    /// <summary>How long a session may run before an agent that never read its account counts as down.</summary>
    public static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(5);

    /// <summary>How old the last healthy reconciliation, or the last stop pass, may be inside a session.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The status and its reasons. Pure, for tests. Outside a session nothing is judged: hosted agents are stopped then
    /// on purpose, and a watchdog that alerted on that would be noise the owner learns to ignore.
    ///
    /// <list type="bullet">
    /// <item><c>never-read</c> (down): no healthy broker read since start, past the startup grace.</item>
    /// <item><c>stop-pass-stale</c> (down): stops are open and the stop worker has not run inside a session lately.</item>
    /// <item><c>reconciliation-stale</c>, <c>feed-silent</c>, <c>unprotected</c>, <c>market-unknown</c>,
    /// <c>full-stop</c> (degraded).</item>
    /// </list>
    /// </summary>
    public static (string Status, IReadOnlyList<string> Problems) Assess(
        DateTime nowUtc, bool sessionOpen, DateTime sessionOpenedUtc, DateTime startedUtc, bool readSinceStart,
        bool reconciliationHealthy, DateTime? reconciliationAtUtc, DateTime? lastPricedUtc, TimeSpan feedSilentAfter,
        int openStops, DateTime? stopPassUtc, int unprotected, MarketPhase phase, TradingStopMode stop)
    {
        if (!sessionOpen) return ("resting", []);

        // Grace runs from whichever came later: this process starting, or the session opening.
        var since = startedUtc > sessionOpenedUtc ? startedUtc : sessionOpenedUtc;
        var pastGrace = nowUtc - since >= StartupGrace;
        var problems = new List<string>();
        var down = false;

        if (!readSinceStart && pastGrace) { problems.Add("never-read"); down = true; }
        if (openStops > 0 && pastGrace && (stopPassUtc is not { } pass || nowUtc - pass > StaleAfter))
        { problems.Add("stop-pass-stale"); down = true; }
        if (readSinceStart && (!reconciliationHealthy || reconciliationAtUtc is not { } rec || nowUtc - rec > StaleAfter))
            problems.Add("reconciliation-stale");
        if (pastGrace && (lastPricedUtc is not { } priced || nowUtc - priced > feedSilentAfter)) problems.Add("feed-silent");
        if (unprotected > 0) problems.Add("unprotected");
        if (phase == MarketPhase.Unknown) problems.Add("market-unknown");
        if (stop == TradingStopMode.FullStop) problems.Add("full-stop");

        return (down ? "down" : problems.Count > 0 ? "degraded" : "ok", problems);
    }
}

/// <summary>Gathers a <see cref="TradingHealthReport"/> from the parts that already know each fact.</summary>
public sealed class TradingHealthReporter(
    IMarketCalendar calendar,
    TradingReconciliationState reconciliation,
    TradingPolicyProvider policy,
    IOptions<TradingAgentOptions> options,
    IMarketPhase? marketPhase = null,
    IBrokerMarketState? brokerState = null,
    CompositeLiveQuoteSource? quotes = null,
    ProtectiveStopWorker? stops = null)
{
    private static readonly string Version =
        typeof(TradingHealthReporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(TradingHealthReporter).Assembly.GetName().Version?.ToString() ?? "unknown";

    private readonly DateTime _startedUtc = DateTime.UtcNow;

    public TradingHealthReport Report()
    {
        var now = DateTime.UtcNow;
        var status = calendar.GetStatus(now);
        var rec = reconciliation.Current;
        var current = policy.Current();
        var health = stops?.Health;
        var phase = marketPhase?.Current.Phase ?? (status.IsOpen ? MarketPhase.Open : MarketPhase.Closed);
        var opened = status.SessionOpenPkt is { } pkt ? DateTime.SpecifyKind(pkt.AddHours(-5), DateTimeKind.Utc) : now;
        var watchdog = options.Value.Watchdog;

        var (verdict, problems) = TradingHealth.Assess(
            now, status.IsOpen, opened, _startedUtc, reconciliation.HasBeenHealthy,
            rec.Supported && rec.Healthy, rec.Supported ? rec.CheckedUtc : null, quotes?.LastPricedUtc,
            TimeSpan.FromSeconds(Math.Max(30, watchdog.FeedSilentSeconds)),
            health?.OpenStops ?? 0, health?.LastPassUtc, health?.Unprotected.Count ?? 0, phase, current.TradingStop);

        return new TradingHealthReport(
            verdict, problems, now, string.IsNullOrWhiteSpace(watchdog.Tenant) ? null : watchdog.Tenant, Version, _startedUtc,
            status.IsOpen, phase.ToString(), (marketPhase?.Rollout ?? RuleRollout.Off).ToString(),
            brokerState?.LastMarketStatus, brokerState?.LastMarketStatusAtUtc,
            rec.Supported && rec.Healthy, rec.Supported ? rec.CheckedUtc : null, reconciliation.HasBeenHealthy,
            quotes?.LastPricedUtc, health?.LastPassUtc, health?.OpenStops ?? 0, health?.Unprotected ?? [],
            current.TradingStop.ToWire(), current.ExecutionMode);
    }
}
