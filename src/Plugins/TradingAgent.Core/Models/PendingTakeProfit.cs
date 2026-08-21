namespace TradingAgent.Models;

/// <summary>
/// A take-profit SELL that could not be placed immediately (typically because the paired BUY limit had
/// not filled yet, so there were no shares to sell / "insufficient exposure"). It is persisted and
/// retried by the background worker until the broker ACCEPTS it (after which the limit rests at the
/// broker and fills on its own) or the attempt budget is exhausted.
/// </summary>
public sealed class PendingTakeProfit
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Symbol { get; set; } = "";
    public int Quantity { get; set; }

    /// <summary>The requested take-profit price. The broker re-clamps it into that day's band on each retry.</summary>
    public decimal TargetPrice { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime NextAttemptUtc { get; set; } = DateTime.UtcNow;
    public int Attempts { get; set; }
    public string LastError { get; set; } = "";
    public string RawMessage { get; set; } = "";
}
