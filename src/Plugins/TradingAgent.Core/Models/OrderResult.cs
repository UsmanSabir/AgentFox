namespace TradingAgent.Models;

public class OrderResult
{
    public bool Success { get; set; }
    public string? OrderId { get; set; }
    public string Action { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Message { get; set; } = "";

    /// <summary>
    /// Shares actually submitted, as resolved by the broker (which falls back to a configured default
    /// when the signal carried none). Null when the order never reached submission.
    ///
    /// <para>
    /// Recorded on the RESULT, not just the request: "BUY FFC @ 551" is not a reportable outcome —
    /// 45 shares and 4,500 shares are the same sentence and very different trades. Both the ledger
    /// row and the execution alert read this.
    /// </para>
    /// </summary>
    public int? Quantity { get; set; }

    /// <summary>
    /// Quantity requested before the sell-availability gate reduced it. Null when no reduction was
    /// necessary; <see cref="Quantity"/> is always the amount actually sent to the broker.
    /// </summary>
    public int? RequestedQuantity { get; set; }

    /// <summary>Human-readable explanation when a SELL was reduced to available holdings.</summary>
    public string? QuantityAdjustment { get; set; }
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

    /// <summary>
    /// True when the broker refused this order for a reason about TIMING rather than about the order —
    /// it would likely be accepted later, unchanged.
    ///
    /// <para>
    /// The distinction is not academic. CONFIRMED 2026-08-28 from a packet capture of AHL's OWN desktop
    /// client: "Order Rej: Last order request not Complete" is that broker's wording for <i>this board
    /// is not accepting orders</i>. It says nothing about a previous order — the official client
    /// received it twice for its own orders while the server had just pushed
    /// <c>ORDER_MST|...|REG|Break</c>, and displayed the text verbatim in an error dialog. An earlier
    /// reading of this comment claimed the condition was account-wide and re-armed by each attempt;
    /// that theory fitted the timings and the capture disproved it.
    /// </para>
    ///
    /// <para>
    /// Nothing retries automatically on the strength of this flag. It exists so a caller can BACK OFF —
    /// see <c>ProtectiveStopWorker</c>. Backing off is still the right response, for the plainer reason
    /// that a shut board will refuse every attempt until it reopens, so retrying on the ordinary cadence
    /// buys nothing and fills the operator's channels with identical failures.
    /// </para>
    /// </summary>
    public bool TransientRejection { get; set; }
}
