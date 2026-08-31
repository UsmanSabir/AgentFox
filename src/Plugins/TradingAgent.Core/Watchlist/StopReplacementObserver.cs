namespace TradingAgent.Watchlist;

/// <summary>
/// One side of a stop replacement, flattened for reporting. Carries only what an operator needs to
/// recognise the order in their own broker terminal — never internal identifiers alone.
/// </summary>
/// <param name="OrderNo">
/// The broker order number, when there is one. Null on the incoming side, which has not been placed
/// yet, and on an outgoing side that never reached the broker.
/// </param>
public sealed record StopReplacementSide(
    decimal Trigger,
    decimal Limit,
    int Quantity,
    string? OrderNo);

/// <summary>
/// What is about to happen, or what just happened, to a protective stop being replaced.
/// </summary>
/// <param name="Outgoing">The stop whose broker order is being cancelled to free the shares.</param>
/// <param name="Incoming">The raised stop that will be placed once those shares are free.</param>
/// <param name="Reason">Why the replacement is happening, in the words of the decision that chose it.</param>
public sealed record StopReplacementPlan(
    string Symbol,
    StopReplacementSide Outgoing,
    StopReplacementSide Incoming,
    string Reason);

/// <summary>
/// Told when a protective stop's broker order is about to be cancelled so a raised one can take its
/// place, and told again once that attempt resolves.
///
/// <para>
/// <b>Why this is a seam rather than a notification sent from here.</b> The replacement is core's —
/// it owns the ledger, the canceller and the order window — but how it should be ANNOUNCED is an
/// edition's business: the premium edition routes it to its own channel topic with its own wording,
/// and the community edition does not announce it at all. Core therefore states the fact and lets
/// whoever cares subscribe, the same way <c>IChartOverlayProvider</c> lets an edition change what an
/// existing route returns without core knowing about it.
/// </para>
///
/// <para>
/// <b>Implementations must not throw and must not block.</b> This is called from the protective-stop
/// worker at the one moment a position is about to be briefly uncovered; an observer that throws, or
/// that waits on a slow channel, would delay or prevent the very cancellation it is reporting. The
/// worker guards both, but an implementation that relies on that guard is one bad deploy from
/// stalling a stop raise.
/// </para>
/// </summary>
public interface IStopReplacementObserver
{
    /// <summary>
    /// Longest an observer may take before the worker stops waiting on it and gets on with the
    /// replacement. Generous enough for a channel round trip, short enough that a wedged channel
    /// cannot hold a protective stop hostage.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The cancel has NOT gone out yet. Everything in <paramref name="plan"/> is intent: the outgoing
    /// order is still resting and the incoming one does not exist.
    /// </summary>
    Task ReplacementPlannedAsync(StopReplacementPlan plan, CancellationToken ct = default);

    /// <summary>
    /// The attempt finished. <paramref name="cancelled"/> says whether the outgoing order is confirmed
    /// gone — when false, nothing changed at the broker and the position is still protected at the old
    /// trigger. A plan announced by <see cref="ReplacementPlannedAsync"/> is always followed by exactly
    /// one of these, because "we are about to remove your protection" followed by silence is the worst
    /// message this system could send.
    /// </summary>
    Task ReplacementResolvedAsync(
        StopReplacementPlan plan, bool cancelled, string detail, CancellationToken ct = default);
}

/// <summary>What must happen before a SELL can use shares a protective stop is holding.</summary>
public enum StopReleaseAction
{
    /// <summary>Enough shares are already free. Nothing to do.</summary>
    NotNeeded,

    /// <summary>One of this system's protective stops is holding them and can be stood down.</summary>
    ReleaseStop,

    /// <summary>
    /// The shares are held by something this system cannot safely stand down — an order it did not
    /// place, or a book it could not read. Doing nothing is the only safe answer.
    /// </summary>
    CannotRelease
}

public sealed record StopReleaseDecision(
    StopReleaseAction Action,
    string? StopId,
    string? OrderNo,
    string Reason);

/// <summary>Whether shares were actually freed, and what it cost.</summary>
public sealed record StopReleaseResult(bool Released, string Reason);

/// <summary>
/// Stands a protective stop down so a SELL that only REDUCES the position can get through.
///
/// <para>
/// <b>Why this exists.</b> This broker sizes a SELL against custody minus resting SELLs, so a
/// protective stop covering the whole position blocks a target scale-out — an order that takes profit
/// and makes the position smaller. Without a way to release, the target's trigger is proved right and
/// nothing happens.
/// </para>
///
/// <para>
/// <b>Implemented by the protective-stop worker, called by whoever fires armed orders.</b> A seam
/// rather than a direct reference because the two run on different clocks and neither owns the other:
/// the monitor knows a sell needs shares, the stop worker knows which stop is holding them and how to
/// put it back.
/// </para>
/// </summary>
public interface IProtectiveStopReleaser
{
    /// <summary>
    /// Frees up to <paramref name="quantityNeeded"/> shares of <paramref name="symbol"/> by cancelling
    /// a protective stop's native order, VERIFIED against the outstanding book. The stop's intent is
    /// left active and re-placed on a later pass against whatever remains, so this shrinks coverage
    /// temporarily rather than removing protection.
    /// </summary>
    Task<StopReleaseResult> ReleaseForSellAsync(
        string symbol, int quantityNeeded, CancellationToken ct = default);
}
