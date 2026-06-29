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

    /// <summary>The limit price requested by the signal, before any day-band clamp.</summary>
    public decimal? RequestedPrice { get; set; }

    /// <summary>The limit price actually submitted (may be clamped to the day's Lower Lock / Upper Cap).</summary>
    public decimal? SubmittedPrice { get; set; }

    /// <summary>
    /// Human-readable note set only when the limit was clamped into the day's price band
    /// (e.g. "Limit clamped down from 22.50 to the day's Upper Cap 22.45."). Null when no adjustment.
    /// </summary>
    public string? PriceAdjustment { get; set; }
}
