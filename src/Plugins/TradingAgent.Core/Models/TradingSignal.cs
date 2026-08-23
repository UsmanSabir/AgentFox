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

    /// <summary>
    /// LIMIT, MARKET, or STOPLOSS. A STOPLOSS order is a TRIGGER plus a limit: the broker holds it
    /// until the market reaches <see cref="EntryPrice"/> (the trigger) and then works it as a limit
    /// order at <see cref="LimitPrice"/>. The portal exposes this natively as its "Stop Loss" type,
    /// which is preferable to a locally-monitored stop because it rests at the broker and survives
    /// this process being down.
    /// </summary>
    public string OrderType { get; set; } = "LIMIT";

    /// <summary>
    /// The limit price for a STOPLOSS order, i.e. the worst price accepted once the trigger is hit.
    /// Ignored for LIMIT and MARKET. Leave null to derive it from the trigger and the configured
    /// slippage allowance — a stop limit set exactly AT the trigger frequently fails to fill in the
    /// fast move that triggered it.
    /// </summary>
    public decimal? LimitPrice { get; set; }

    /// <summary>
    /// Refuse a daily price-band adjustment that would violate the user's original price rather
    /// than silently turning a persistent order into a different instruction on a later session.
    /// </summary>
    public bool PreservePriceIntent { get; set; }

    /// <summary>
    /// For a stop order that came from an armed trigger: whether that trigger fires as the price
    /// RISES (true) or FALLS (false). Null when the order was submitted directly and has no trigger
    /// of its own.
    ///
    /// <para>
    /// This exists because the trigger's direction is what decides whether a stop's limit is fillable,
    /// and the direction used to be lost in the projection from armed order to signal — leaving the
    /// risk engine to guess it from the side and refuse legitimate orders. See
    /// <c>TradingAgent.Risk.StopLimitRule</c>.
    /// </para>
    /// </summary>
    public bool? FiresOnRisingPrice { get; set; }
    public string Confidence { get; set; } = "NONE";    // HIGH, MEDIUM, LOW, NONE
    public string ConfidenceReason { get; set; } = "";
    public string RawMessage { get; set; } = "";
    public string Sender { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
