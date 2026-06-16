namespace TradingAgent.Models;

public class OrderResult
{
    public bool Success { get; set; }
    public string? OrderId { get; set; }
    public string Action { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Message { get; set; } = "";
    public string? ScreenshotBefore { get; set; }
    public string? ScreenshotAfter { get; set; }
}
