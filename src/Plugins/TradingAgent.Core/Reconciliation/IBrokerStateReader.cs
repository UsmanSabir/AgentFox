namespace TradingAgent.Reconciliation;

public sealed record BrokerReconciliationSnapshot(
    bool Supported,
    bool Healthy,
    string Reason,
    DateTime CheckedUtc,
    string DetailsJson = "{}")
{
    /// <summary>
    /// Fills the broker reports for today, structured rather than buried in <see cref="DetailsJson"/>.
    ///
    /// <para>
    /// They are lifted out because they are the one part of a snapshot worth keeping as ROWS: a fill is
    /// the event that changes a position, and answering "when did order X fill, and at what price" by
    /// scanning JSON blobs is the difference between an audit trail and a haystack. Empty when the log
    /// could not be read at all — which is not the same statement as "nothing filled today", so
    /// <see cref="Healthy"/> has to be read alongside it.
    /// </para>
    /// </summary>
    public IReadOnlyList<BrokerFill> Fills { get; init; } = [];

    /// <summary>Exact native orders currently resting at the broker.</summary>
    public IReadOnlyList<BrokerWorkingOrder> OpenOrders { get; init; } = [];

    /// <summary>
    /// Today's order lifecycle rows, including queued, accepted, rejected, and cancelled events.
    /// These close the short propagation gap where an accepted order is visible in activity before it
    /// appears in the outstanding book.
    /// </summary>
    public IReadOnlyList<BrokerOrderEvent> OrderEvents { get; init; } = [];

    /// <summary>Current custody positions used to keep recurring SELLs within available holdings.</summary>
    public IReadOnlyList<BrokerPosition> Positions { get; init; } = [];

    public decimal? AvailableCashPkr { get; init; }

    public static BrokerReconciliationSnapshot Unsupported(string reason) =>
        new(false, false, reason, DateTime.UtcNow);
}

/// <summary>
/// One executed quantity as the broker reports it. <paramref name="OrderNo"/> is the exchange's own
/// order number, which is what ties a fill back to the order that produced it.
/// </summary>
public sealed record BrokerFill(
    string OrderNo,
    string Symbol,
    string? Side,
    int Quantity,
    decimal Price,
    DateTime FilledUtc);

/// <param name="OrderType">
/// The order's CURRENT type in the vocabulary core uses — <c>LIMIT</c>, <c>STOPLOSS</c>, <c>MARKET</c> —
/// or null when the broker does not report one. It is what tells a stop that is still waiting apart
/// from one that has TRIGGERED: at a venue where a triggered stop-limit becomes an ordinary resting
/// limit, the same order changes type from STOPLOSS to LIMIT and keeps its number. Null is not
/// "LIMIT" — nothing reading it may act on an absent value.
/// </param>
/// <param name="AlternateOrderNo">
/// A second identifier the broker reports for the same order, when it has one. At AHL a stop keeps
/// its short connection-scoped number and gains a long exchange id when it triggers, and which of the
/// two lands in which column of the book row is not measured — so a caller matching one of OUR numbers
/// must try both.
/// </param>
public sealed record BrokerWorkingOrder(
    string OrderNo,
    string Symbol,
    string? Side,
    long? RemainingQuantity,
    decimal? Price,
    string? OrderType = null,
    string? AlternateOrderNo = null);

public sealed record BrokerOrderEvent(
    string OrderNo,
    string Symbol,
    string? Side,
    string? Action,
    int? Quantity,
    decimal? Price,
    DateTime ObservedUtc);

public sealed record BrokerPosition(string Symbol, decimal Quantity);

public interface IBrokerStateReader
{
    Task<BrokerReconciliationSnapshot> ReadSnapshotAsync(CancellationToken ct = default);
}

public sealed class TradingReconciliationState
{
    private readonly object _gate = new();
    private BrokerReconciliationSnapshot _current =
        BrokerReconciliationSnapshot.Unsupported("Broker reconciliation has not run.");

    public BrokerReconciliationSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public void Update(BrokerReconciliationSnapshot snapshot)
    {
        lock (_gate) _current = snapshot;
    }
}
