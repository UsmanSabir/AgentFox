namespace TradingAgent.Reconciliation;

public sealed record BrokerReconciliationSnapshot(
    bool Supported,
    bool Healthy,
    string Reason,
    DateTime CheckedUtc,
    string DetailsJson = "{}")
{
    public static BrokerReconciliationSnapshot Unsupported(string reason) =>
        new(false, false, reason, DateTime.UtcNow);
}

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
