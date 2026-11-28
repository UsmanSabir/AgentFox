using TradingAgent.Watchlist;

namespace TradingAgent.Persistence;

/// <summary>
/// Every native order a protective stop placed, one row each, so coverage can be judged order by order against
/// the book (<see cref="ProtectiveStopDecisions.LiveCoverage"/>) instead of from the running total
/// <see cref="ProtectiveStop.PlacedQuantity"/>, which cannot tell "placed" from "still there".
///
/// <para>
/// A separate interface rather than more of <see cref="ITradingRepository"/>, as
/// <c>IClientOrderOperationRepository</c> is: the stop worker asks for it and falls back to the ledger total when
/// it is absent, so every other implementation of the repository keeps compiling and keeps its behaviour.
/// </para>
///
/// <para>
/// <b>Retention (§0.1):</b> rows are pruned by <see cref="PruneProtectiveStopOrdersAsync"/> from the retention
/// worker. Only today's rows are ever read, since the venue clears its book at the close.
/// </para>
/// </summary>
public interface IProtectiveStopOrderLedger
{
    /// <summary>Every stop's orders for one session.</summary>
    Task<IReadOnlyList<ProtectiveStopOrder>> GetProtectiveStopOrdersAsync(
        DateOnly sessionDate, CancellationToken ct = default);

    /// <summary>
    /// Records a placement in one transaction: the stop's own row (session, the new running coverage, the last
    /// order number) and the order's ledger row. <paramref name="placedTotal"/> is the coverage AFTER this order —
    /// what still covered before it plus what it placed — so a stop re-placed after its first order left the book
    /// does not count both.
    /// </summary>
    Task<bool> RecordProtectiveStopOrderAsync(
        ProtectiveStopOrder order, int placedTotal, CancellationToken ct = default);

    /// <summary>Writes back what a coverage reading learned: an order first missed, seen again, or gone.</summary>
    Task UpdateProtectiveStopOrdersAsync(
        IReadOnlyList<ProtectiveStopOrder> orders, CancellationToken ct = default);

    Task<int> PruneProtectiveStopOrdersAsync(DateOnly before, CancellationToken ct = default);
}
