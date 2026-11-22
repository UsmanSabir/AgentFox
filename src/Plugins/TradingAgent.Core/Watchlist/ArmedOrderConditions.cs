using System.Collections.Concurrent;
using TradingAgent.Analysis;

namespace TradingAgent.Watchlist;

/// <summary>
/// The seam behind <see cref="ArmedTriggerKind.Rule"/>: an edition that can read a condition document
/// tells core whether it is met. Optional — a build that registers none simply cannot arm or fire a
/// rule order, and says so.
///
/// <para>
/// <b>It decides; it never acts.</b> Core keeps everything after the decision: the compare-and-set
/// claim, the approval gate, the refusal backoff, free-share confirmation, the stop release and the
/// keep-working hand-over. An implementation that placed an order itself would be a second order path
/// with none of those, which is precisely what this seam exists to avoid.
/// </para>
///
/// <para>
/// <b>It must be a LEAF and must answer quickly.</b> <see cref="WatchlistMonitorWorker"/> depends on it,
/// and the worker sits on the dependency graph every broker adapter is wired into, so an implementation
/// that takes a broker service in its constructor can close a cycle the container never reports. It is
/// also called inside the monitor pass, once per rule order: gather data elsewhere and answer from
/// memory here.
/// </para>
/// </summary>
public interface IArmedOrderConditionEvaluator
{
    /// <summary>
    /// Validates a condition at arm time and returns the document to store (normalised) and a one-line
    /// summary of it, or a refusal. Pure as far as core is concerned: nothing is stored on a refusal.
    /// </summary>
    ArmedConditionValidation Validate(ArmedConditionRequest request);

    /// <summary>
    /// Whether <paramref name="order"/>'s condition is met right now. <paramref name="passPrice"/> is the
    /// live price the monitor pass holds for its symbol, or null when it has none. Must not throw for an
    /// ordinary "no"; core treats an exception as "cannot decide" and the order waits.
    /// </summary>
    ArmedConditionDecision Evaluate(ArmedOrder order, decimal? passPrice, DateTime nowUtc);
}

/// <summary>What is being armed, as far as a condition's validity depends on it.</summary>
public sealed record ArmedConditionRequest(
    string Symbol,
    string Action,
    string OrderType,
    decimal? Price,
    string Spec);

/// <summary>The answer to <see cref="IArmedOrderConditionEvaluator.Validate"/>.</summary>
public sealed record ArmedConditionValidation(
    string? NormalizedSpec,
    string? Summary,
    string? ErrorCode = null,
    string? Message = null)
{
    public bool Ok => ErrorCode is null && !string.IsNullOrWhiteSpace(NormalizedSpec);

    public static ArmedConditionValidation Valid(string spec, string summary) => new(spec, summary);

    public static ArmedConditionValidation Invalid(string code, string message) => new(null, null, code, message);
}

/// <summary>
/// The answer to <see cref="IArmedOrderConditionEvaluator.Evaluate"/>. <paramref name="LimitPrice"/> is
/// the limit to send when the order's own <see cref="ArmedOrder.Price"/> is not the one to use — a rule
/// priced off the price that met it. Null keeps the stored price.
/// </summary>
public sealed record ArmedConditionDecision(bool Fire, string Reason, decimal? LimitPrice = null)
{
    public static ArmedConditionDecision Wait(string reason) => new(false, reason);
}

/// <summary>
/// The monitor pass's latest technical reading per symbol — the SMA, RSI and ATR it computed anyway —
/// kept so a consumer outside the pass (a rule condition) can read them without a second analysis.
///
/// <para>
/// Memory only. One reading per symbol, replaced by every pass, never served past <see cref="MaxAge"/>,
/// and bounded at <see cref="Capacity"/> symbols, oldest dropped (§0.1). A restart drops it; the next
/// pass refills it.
/// </para>
/// </summary>
public sealed class MonitorTechnicalReadings
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);
    public const int Capacity = 2000;

    private readonly ConcurrentDictionary<string, MonitorTechnicalReading> _latest =
        new(StringComparer.OrdinalIgnoreCase);

    public void Record(TechnicalSnapshot snapshot, DateTime observedUtc)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Symbol)) return;
        _latest[snapshot.Symbol] = new MonitorTechnicalReading(snapshot, observedUtc);
        if (_latest.Count <= Capacity) return;
        foreach (var stale in _latest.OrderBy(p => p.Value.ObservedUtc).Take(_latest.Count - Capacity).ToList())
            _latest.TryRemove(stale.Key, out _);
    }

    /// <summary>The latest reading for <paramref name="symbol"/>, or null when none is younger than <see cref="MaxAge"/>.</summary>
    public MonitorTechnicalReading? Latest(string symbol, DateTime nowUtc)
    {
        if (!_latest.TryGetValue(symbol, out var reading)) return null;
        if (nowUtc - reading.ObservedUtc <= MaxAge) return reading;
        _latest.TryRemove(symbol, out _);
        return null;
    }
}

public sealed record MonitorTechnicalReading(TechnicalSnapshot Snapshot, DateTime ObservedUtc);
