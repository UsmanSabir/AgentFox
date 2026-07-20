namespace TradingAgent.Market;

public readonly record struct MarketStatus(
    bool IsOpen,
    DateTime PktNow,
    string Reason,
    DateTime? NextOpenPkt = null,
    string ScheduleSource = "regular");

public interface IMarketCalendar
{
    MarketStatus GetStatus(DateTime? utcNow = null);
}
