namespace TradingAgent.Trading;

/// <summary>
/// Told when a persistent (keep-working) order reaches a terminal state on its own, without anybody
/// asking for it.
///
/// <para>
/// <b>Why expiry needs announcing at all.</b> An intent that expires unfilled ends quietly: it simply
/// stops being re-placed. Nobody made a decision at that moment, so nobody is looking — and the
/// standing instruction the operator believed was still working has ended with shares unbought or
/// unsold. A fill announces itself through the broker's own events; an expiry has no broker event, so
/// unless this worker says so, nothing does.
/// </para>
///
/// <para>
/// <b>Why a seam rather than a notification sent from here.</b> The same reason as
/// <see cref="TradingAgent.Watchlist.IStopReplacementObserver"/>: the lifecycle is core's, but how it
/// is ANNOUNCED is an edition's business. Core states the fact and lets whoever cares subscribe.
/// </para>
///
/// <para>
/// <b>Implementations must not throw and must not block.</b> This is called from inside the persistent
/// order worker's lifecycle pass, which holds that worker's run gate. The worker bounds each call by
/// <see cref="Budget"/> and swallows failures, but an implementation that relies on that guard is one
/// bad deploy from stalling every other intent's pass.
/// </para>
/// </summary>
public interface IPersistentOrderObserver
{
    /// <summary>
    /// Longest an observer may take before the worker stops waiting on it and moves on to the next
    /// intent. Generous enough for a channel round trip, short enough that a wedged channel cannot hold
    /// the lifecycle pass hostage.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The intent has just been written <c>expired</c>. <paramref name="intent"/> is the stored row as
    /// re-read after that write, so <see cref="PersistentOrderIntent.FilledQuantity"/> includes every
    /// fill this ledger has recorded for it. Called once per intent: an expired intent is terminal and
    /// never revisited by the worker.
    /// </summary>
    /// <param name="detail">The reason written to the intent, in the worker's own words.</param>
    Task ExpiredAsync(PersistentOrderIntent intent, string detail, CancellationToken ct = default);
}
