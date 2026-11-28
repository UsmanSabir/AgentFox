using System.Globalization;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.AhlAnalytics;
using TradingAgent.Config;
using TradingAgent.Feed;
using TradingAgent.Observability;
using TradingAgent.Persistence;
using TradingAgent.Research;

namespace TradingAgent.Market;

/// <summary>
/// The market phase as the order-sending workers see it. A narrow read-only view of <see cref="MarketStateService"/>,
/// so a worker that only asks "is the market halted?" does not depend on how that is found out.
/// </summary>
public interface IMarketPhase
{
    MarketPhaseReading Current { get; }

    /// <summary>On: halts gate the workers. Shadow: they are detected and logged, and nothing is gated.</summary>
    RuleRollout Rollout { get; }

    /// <summary>Raised on every phase change, with the previous reading and the new one.</summary>
    event Action<MarketPhaseReading, MarketPhaseReading>? Changed;
}

/// <summary>The KSE-30's move, read for halt detection.</summary>
public interface IIndexMoveSource
{
    Task<IndexMove> ReadAsync(CancellationToken ct);
}

/// <summary>A market state from a source other than the broker (the analytics feed).</summary>
public interface IAnalyticsMarketState
{
    Task<StateObservation> ReadAsync(CancellationToken ct);
}

/// <summary>The KSE-30 from the PSX data portal (dps.psx.com.pk), as the owner specified (2026-11-28).</summary>
public sealed class PsxIndexMoveSource(PsxDataClient psx, IOptions<TradingAgentOptions> options) : IIndexMoveSource
{
    public async Task<IndexMove> ReadAsync(CancellationToken ct)
    {
        var quote = await psx.GetQuoteSummaryAsync(options.Value.HaltIndexSymbol, ct);
        return new IndexMove(quote.Error is null ? quote.DayChangePercent : null, quote.LastTickUtc);
    }
}

/// <summary>
/// The analytics feed's market state (<c>st</c>), stamped with the feed's own "last updated". Read WITHOUT the broker
/// handshake: halt detection must never be the thing that starts a broker login.
/// </summary>
public sealed class AhlAnalyticsMarketState(AhlAnalyticsClient analytics) : IAnalyticsMarketState
{
    public async Task<StateObservation> ReadAsync(CancellationToken ct)
    {
        var snapshot = await analytics.GetMarketSnapshotAsync(forceRefresh: false, allowHandshake: false, ct: ct);
        if (snapshot?.MarketState is not { Length: > 0 } state) return new(null, null);
        // "yyyy-MM-dd HH:mm:ss", Pakistan time (UTC+5, no daylight saving).
        DateTime? updated = DateTime.TryParseExact(snapshot.LastUpdate, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var pkt) ? DateTime.SpecifyKind(pkt.AddHours(-5), DateTimeKind.Utc) : null;
        return new(state, updated);
    }
}

/// <summary>
/// Watches for a market-wide halt and its end, and tells the order-sending workers (plan step 5).
///
/// <para>
/// <b>Why the workers need this.</b> Until now they followed the trading calendar, so during a halt the stop worker
/// kept trying to place, each attempt was refused, and the refusals started backoffs that held the re-placement back
/// after trading resumed. A local backstop meanwhile could fire into the first minutes after the reopen. The decision
/// itself is <see cref="HaltDetector"/>'s; this class gathers its inputs, keeps the reading, announces transitions and
/// records them.
/// </para>
///
/// <para>
/// <b>Rollout.</b> <see cref="TradingAgentOptions.MarketHaltHandling"/> defaults to Shadow: the phase is detected and
/// every transition goes to the decision log and the warning log, but no worker is gated and nobody is alerted, until
/// the owner has reviewed several live sessions and switches it On (owner, 2026-11-28).
/// </para>
/// </summary>
public sealed class MarketStateService : BackgroundService, IMarketPhase
{
    internal const string Rule = "market.halt-handling";

    private readonly IMarketCalendar _calendar;
    private readonly IBrokerMarketState _broker;
    private readonly IIndexMoveSource? _index;
    private readonly IAnalyticsMarketState? _analytics;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<MarketStateService> _logger;
    private readonly IServiceScopeFactory? _scopes;
    private readonly IUserNotifier? _notifier;
    private readonly TradingActivityLog? _activity;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MarketPhaseReading _current;
    private string? _conflictAnnouncedFor;

    public MarketStateService(
        IMarketCalendar calendar,
        IBrokerMarketState broker,
        IOptions<TradingAgentOptions> options,
        ILogger<MarketStateService> logger,
        IIndexMoveSource? index = null,
        IAnalyticsMarketState? analytics = null,
        IServiceScopeFactory? scopes = null,
        IUserNotifier? notifier = null,
        TradingActivityLog? activity = null,
        TimeProvider? time = null)
    {
        _calendar = calendar;
        _broker = broker;
        _index = index;
        _analytics = analytics;
        _options = options;
        _logger = logger;
        _scopes = scopes;
        _notifier = notifier;
        _activity = activity;
        _time = time ?? TimeProvider.System;
        _current = MarketPhaseReading.Initial(_time.GetUtcNow().UtcDateTime);
    }

    public MarketPhaseReading Current => Volatile.Read(ref _current);
    public RuleRollout Rollout => _options.Value.MarketHaltHandling;
    public event Action<MarketPhaseReading, MarketPhaseReading>? Changed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var open = false;
            try { open = (await EvaluateNowAsync(stoppingToken)).Phase != MarketPhase.Closed; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "[MarketState] Evaluation failed; the previous reading stands."); }

            var seconds = open ? Math.Clamp(_options.Value.MarketStatePollSeconds, 5, 300) : 60;
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), _time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Reads every source once, decides, and announces a change. Public for tests and diagnostics.</summary>
    public async Task<MarketPhaseReading> EvaluateNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var status = _calendar.GetStatus(now);
            var options = _options.Value;

            // Sources are read only inside a session: outside one the answer is Closed whatever they say.
            var broker = new StateObservation(null, null);
            var analytics = new StateObservation(null, null);
            var index = new IndexMove(null, null);
            if (status.IsOpen)
            {
                // A reading with no recorded time is the adapter's latest, served as current — the same reading
                // OrderWindow takes, so the two can never disagree about what the broker said.
                if (_broker.LastMarketStatus is { Length: > 0 } token)
                    broker = new(token, _broker.LastMarketStatusAtUtc ?? now);
                analytics = await Read(() => _analytics?.ReadAsync(ct), new StateObservation(null, null), "analytics state");
                index = await Read(() => _index?.ReadAsync(ct), new IndexMove(null, null), "KSE-30");
            }

            var sessionOpenedUtc = status.SessionOpenPkt is { } opened
                ? DateTime.SpecifyKind(opened.AddHours(-5), DateTimeKind.Utc) : (DateTime?)null;
            var previous = Current;
            var next = HaltDetector.Evaluate(
                new HaltInputs(now, status.IsOpen, sessionOpenedUtc, broker, analytics, index),
                previous,
                new HaltOptions(
                    options.HaltIndexMovePercent,
                    ReopenSettling: TimeSpan.FromMinutes(Math.Max(0, options.ReopenBackstopHoldMinutes)),
                    HaltMax: TimeSpan.FromMinutes(Math.Max(1, options.HaltMaxMinutes))));

            Volatile.Write(ref _current, next);
            if (next.Phase != previous.Phase) await AnnounceAsync(previous, next, ct);
            if (next.Conflict is { } conflict) await AnnounceConflictAsync(next, conflict, ct);
            return next;
        }
        finally
        {
            _gate.Release();
        }

        async Task<T> Read<T>(Func<Task<T>?> read, T fallback, string what)
        {
            try { return await (read() ?? Task.FromResult(fallback)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[MarketState] The {What} could not be read this time.", what);
                return fallback;
            }
        }
    }

    private async Task AnnounceAsync(MarketPhaseReading previous, MarketPhaseReading next, CancellationToken ct)
    {
        var acting = Rollout == RuleRollout.On;
        var tag = acting ? "" : "[Shadow] ";

        // The routine edges (a session opening or closing) are not news; halts, reopens and an unresolved halt are.
        var notable = next.Phase is MarketPhase.Halted or MarketPhase.ReopenSettling or MarketPhase.Unknown
                      || previous.Phase is MarketPhase.Halted or MarketPhase.ReopenSettling or MarketPhase.Unknown;
        if (notable)
            _logger.LogWarning("[MarketState] {Tag}{From} -> {To} ({Source}): {Reason}",
                tag, previous.Phase, next.Phase, next.Source, next.Reason);
        else
            _logger.LogInformation("[MarketState] {From} -> {To}: {Reason}", previous.Phase, next.Phase, next.Reason);

        try { Changed?.Invoke(previous, next); }
        catch (Exception ex) { _logger.LogWarning(ex, "[MarketState] A phase-change listener failed."); }

        if (!notable) return;

        await RecordAsync(previous.Phase.ToString(), next.Phase.ToString(), $"{next.Source}: {next.Reason}", ct);
        if (!acting) return;

        _activity?.Warn("Market", $"Market {next.Phase switch
        {
            MarketPhase.Halted => "HALTED",
            MarketPhase.ReopenSettling => "reopened",
            MarketPhase.Unknown => "halt unresolved",
            _ => "trading normally"
        }}", next.Reason);

        var (topic, message) = next.Phase switch
        {
            MarketPhase.Halted => (TradingTopics.MarketHalt,
                "⏸️ **Market halted**\n"
                + $"• {next.Reason}\n"
                + "• Nothing is sent while trading is stopped. Stops cancelled by the halt are re-placed when it reopens, "
                + "and local backstops wait a few minutes after the reopen unless the price falls through the catastrophic level."),
            MarketPhase.ReopenSettling => (TradingTopics.MarketReopen,
                "▶️ **Market reopened**\n"
                + $"• {next.Reason}\n"
                + "• Reconciling with the broker and re-placing protective stops now."),
            MarketPhase.Unknown => (TradingTopics.MarketUnknown,
                "❓ **Halt not resolved**\n"
                + $"• {next.Reason}\n"
                + "• Check the market in your broker's app."),
            _ => (TradingTopics.MarketReopen, $"▶️ **Market trading normally**\n• {next.Reason}")
        };
        await NotifyAsync(message, topic);
    }

    private async Task AnnounceConflictAsync(MarketPhaseReading reading, string conflict, CancellationToken ct)
    {
        var key = $"{reading.Session}:{conflict}";
        if (_conflictAnnouncedFor == key) return;
        _conflictAnnouncedFor = key;

        _logger.LogWarning("[MarketState] {Tag}Sources disagree — {Conflict}. {Reason}",
            Rollout == RuleRollout.On ? "" : "[Shadow] ", conflict, reading.Reason);
        await RecordAsync("conflict", conflict, reading.Reason, ct);
        if (Rollout == RuleRollout.On)
            await NotifyAsync($"⚠️ **Market state unclear**\n• {reading.Reason}\n• Check the market in your broker's app.",
                TradingTopics.MarketUnknown);
    }

    private async Task RecordAsync(string current, string shadow, string detail, CancellationToken ct)
    {
        if (_scopes is null) return;
        try
        {
            using var scope = _scopes.CreateScope();
            if (scope.ServiceProvider.GetService<ITradingRepository>() is IRuleShadowLog log)
                await log.RecordRuleShadowDecisionAsync(new RuleShadowDecision(
                    _time.GetUtcNow().UtcDateTime, Rule, null, null, current, shadow, detail), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[MarketState] The phase change could not be recorded.");
        }
    }

    private async Task NotifyAsync(string message, string topic)
    {
        if (_notifier is null) return;
        try { await _notifier.NotifyAsync(message, topic); }
        catch (Exception ex) { _logger.LogWarning(ex, "[MarketState] Could not deliver the market alert."); }
    }
}
