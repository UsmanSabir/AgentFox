namespace TradingAgent.Models;

public class TradingSignal
{
    public bool IsSignal { get; set; }
    public string Action { get; set; } = "UNKNOWN";     // BUY, SELL, HOLD, UNKNOWN
    public string Symbol { get; set; } = "";
    public decimal? EntryPrice { get; set; }
    public decimal? Target { get; set; }
    public decimal? StopLoss { get; set; }
    public int? Quantity { get; set; }
    public string OrderType { get; set; } = "LIMIT";    // LIMIT or MARKET
    public string Confidence { get; set; } = "NONE";    // HIGH, MEDIUM, LOW, NONE
    public string ConfidenceReason { get; set; } = "";
    public string RawMessage { get; set; } = "";
    public string Sender { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
