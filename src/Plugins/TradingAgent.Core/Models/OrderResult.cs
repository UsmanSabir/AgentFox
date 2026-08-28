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
    /// The distinction is not academic, and it is not about politeness to the broker. CONFIRMED live
    /// 2026-08-28 against AHL: "Order Rej: Last order request not Complete" is stated account-wide, it
    /// expires on its own, and each new attempt RE-ARMS it. A caller retrying on a fixed interval
    /// therefore holds itself out indefinitely — a protective stop retried every 60 seconds failed for
    /// hours, while the only two orders that reached the market all day were each the first attempt
    /// after a quiet gap. A caller that cannot tell this apart from "you cannot sell more than 0 shares"
    /// will either hammer something that can never succeed, or give up on something that would have
    /// worked in ten minutes.
    /// </para>
    ///
    /// <para>
    /// Nothing retries automatically on the strength of this flag. It exists so a caller can BACK OFF —
    /// see <c>ProtectiveStopWorker</c>, which stops attempting for a growing interval rather than
    /// re-arming the condition every pass.
    /// </para>
    /// </summary>
    public bool TransientRejection { get; set; }
}
