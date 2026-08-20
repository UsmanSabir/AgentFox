using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingAgent.Feed;

/// <summary>
/// One <c>GET /Home/GetFeed</c> response. Field names match the portal's JSON exactly (see
/// <c>docs/ahk-feed-api.md</c>); only <see cref="Feed"/> and <see cref="MarketStatus"/> are consumed
/// here — the depth arrays are modelled so their presence is visible but are deliberately not wired
/// into anything yet.
/// </summary>
public sealed class AhkFeedResponse
{
    [JsonPropertyName("feed")]          public List<AhkFeedQuote>? Feed { get; set; }
    [JsonPropertyName("exchangeStats")] public List<object>? ExchangeStats { get; set; }
    // Market depth. JsonElement rather than object because these arrive only when a MBO-FEED or
    // MBP-FEED subscription is active, and their element shape had never been captured — deserialising
    // to `object` produced values nothing could read, which is why these sat unused. Keeping the raw
    // element preserves the payload for AhkDepthBook to record and expose, so a typed model can be
    // written from observed data instead of guessed field names.
    [JsonPropertyName("mboFeed")]       public List<JsonElement>? MboFeed { get; set; }
    [JsonPropertyName("mbpFeed")]       public List<JsonElement>? MbpFeed { get; set; }
    [JsonPropertyName("marketStatus")]  public string? MarketStatus { get; set; }
}

/// <summary>
/// One symbol's quote as published by the portal.
///
/// <para>
/// Every numeric field is nullable and every one of them can legitimately be zero: the portal
/// publishes zeros for a symbol that has not traded today. "Zero" therefore means UNKNOWN, never a
/// real price of nothing — <see cref="TradingAgent.Research.AhkQuoteMapper"/> is where that
/// conversion happens, and it must not be bypassed.
/// </para>
/// </summary>
public sealed class AhkFeedQuote
{
    [JsonPropertyName("mkt")]         public string? Mkt { get; set; }
    [JsonPropertyName("symbol")]      public string? Symbol { get; set; }

    [JsonPropertyName("lastPrice")]   public decimal? LastPrice { get; set; }
    [JsonPropertyName("openPrice")]   public decimal? OpenPrice { get; set; }
    [JsonPropertyName("high")]        public decimal? High { get; set; }
    [JsonPropertyName("low")]         public decimal? Low { get; set; }

    /// <summary>The prior close the portal computes <see cref="Change"/> against, not today's close.</summary>
    [JsonPropertyName("closePrice")]  public decimal? ClosePrice { get; set; }

    /// <summary>ABSOLUTE change in price, not a percentage. The portal's UI formats it with 2dp.</summary>
    [JsonPropertyName("change")]      public decimal? Change { get; set; }

    [JsonPropertyName("average")]     public decimal? Average { get; set; }

    /// <summary>Best bid, and the size resting at it.</summary>
    [JsonPropertyName("buy")]         public decimal? Buy { get; set; }
    [JsonPropertyName("bVol")]        public long? BVol { get; set; }

    /// <summary>Best ask, and the size resting at it.</summary>
    [JsonPropertyName("sell")]        public decimal? Sell { get; set; }
    [JsonPropertyName("sVol")]        public long? SVol { get; set; }

    [JsonPropertyName("totalVolume")] public long? TotalVolume { get; set; }
    [JsonPropertyName("totTrd")]      public long? TotTrd { get; set; }
    [JsonPropertyName("lTrdVolume")]  public long? LTrdVolume { get; set; }
    [JsonPropertyName("lTrdTime")]    public string? LTrdTime { get; set; }

    [JsonPropertyName("state")]       public string? State { get; set; }
    [JsonPropertyName("dir")]         public string? Dir { get; set; }
    [JsonPropertyName("flag")]        public string? Flag { get; set; }
}

/// <summary>One entry of <c>GET /Home/GetSymolsList</c> (the portal's spelling).</summary>
public sealed class AhkSymbolListEntry
{
    [JsonPropertyName("market")]     public string? Market { get; set; }
    [JsonPropertyName("symbol")]     public string? Symbol { get; set; }
    [JsonPropertyName("symbolName")] public string? SymbolName { get; set; }
    [JsonPropertyName("sectorName")] public string? SectorName { get; set; }
    [JsonPropertyName("approved")]   public string? Approved { get; set; }
}

/// <summary>
/// One entry of <c>GET /Home/GetUpperLowerCap</c> — the day's price band. PSX rejects any order
/// outside it, which is why <c>Ahk.ClampPriceToBand</c> exists; this endpoint serves the whole
/// market at once, unlike the order-dialog scrape it can replace.
/// </summary>
public sealed class AhkPriceBand
{
    [JsonPropertyName("symbol")]    public string? Symbol { get; set; }
    [JsonPropertyName("market")]    public string? Market { get; set; }
    [JsonPropertyName("upperCap")]  public decimal? UpperCap { get; set; }
    [JsonPropertyName("lowerLock")] public decimal? LowerLock { get; set; }
}

/// <summary>
/// One resting (unfilled) order from <c>GET /Home/GetOutstanding</c>.
///
/// <para>
/// <see cref="OrderNo"/> is the handle everything else keys off — it is what
/// <c>POST /Home/CancelOrder</c> takes as <c>orignalorderno</c>. <see cref="Type"/> uses the
/// portal's three-letter vocabulary, <c>BUY</c> or <c>SEL</c>.
/// </para>
/// </summary>
public sealed class AhkOutstandingOrder
{
    [JsonPropertyName("orderNo")]   public string? OrderNo { get; set; }

    /// <summary>The house/exchange-side order number. Shown alongside, but not what cancel takes.</summary>
    [JsonPropertyName("hOrderNo")]  public string? HOrderNo { get; set; }

    /// <summary>Ticker. The portal calls it "scrip" here and "symbol" in the feed.</summary>
    [JsonPropertyName("scrip")]     public string? Scrip { get; set; }

    [JsonPropertyName("market")]    public string? Market { get; set; }

    /// <summary><c>BUY</c> or <c>SEL</c>.</summary>
    [JsonPropertyName("type")]      public string? Type { get; set; }

    [JsonPropertyName("price")]     public decimal? Price { get; set; }

    /// <summary>Unfilled quantity still working. A partially filled order shows the remainder here.</summary>
    [JsonPropertyName("remaining")] public long? Remaining { get; set; }

    [JsonPropertyName("account")]   public string? Account { get; set; }
    [JsonPropertyName("trader")]    public string? Trader { get; set; }
    [JsonPropertyName("time")]      public string? Time { get; set; }
    [JsonPropertyName("action")]    public string? Action { get; set; }
    [JsonPropertyName("flag")]      public string? Flag { get; set; }

    /// <summary>One-line rendering for tool output and approval prompts.</summary>
    public string Describe() =>
        $"#{OrderNo} {Type} {Remaining?.ToString() ?? "?"} {Scrip} @ {Price?.ToString("F2") ?? "?"}" +
        (string.IsNullOrWhiteSpace(Time) ? "" : $" ({Time})");
}

/// <summary>
/// The outcome of reading the outstanding order book — deliberately distinguishing "the account has
/// no working orders" from "the book could not be read".
///
/// <para>
/// Those two were previously the same value (an empty list), and that conflation is dangerous rather
/// than merely untidy. It was hit for real on 2026-08-18: the broker blocked account access mid-test,
/// <c>GetOutstanding</c> began returning an empty array, and everything downstream read that as "no
/// orders". A cancel was reported as verified-complete on that basis while the order was still live,
/// and a resting order was believed cancelled when it had not been. An unreadable book must fail
/// loudly; it must never look like an empty one.
/// </para>
/// </summary>
public readonly record struct OrderBookRead(
    bool Ok, IReadOnlyList<AhkOutstandingOrder> Orders, string? Error)
{
    public static OrderBookRead Success(IReadOnlyList<AhkOutstandingOrder> orders) =>
        new(true, orders, null);

    public static OrderBookRead Failed(string error) =>
        new(false, [], error);

    /// <summary>True only when the book was genuinely read AND genuinely empty.</summary>
    public bool IsConfirmedEmpty => Ok && Orders.Count == 0;
}

/// <summary>Identity of a tradable line on the portal. A symbol trades on more than one board.</summary>
public readonly record struct AhkSymbolKey(string Market, string Symbol)
{
    public static AhkSymbolKey Of(string? market, string? symbol) =>
        new((market ?? "").Trim().ToUpperInvariant(), (symbol ?? "").Trim().ToUpperInvariant());

    public bool IsValid => Market.Length > 0 && Symbol.Length > 0;

    public override string ToString() => $"{Market}:{Symbol}";
}

/// <summary>
/// One holding from <c>GET /Home/GetCollaterals?account=…</c> — the account's custody position,
/// and the endpoint that fills the portal's own <c>#collateralstable</c>.
///
/// <para>
/// This is the JSON replacement for the Exposure-dialog scrape in <c>AhkBroker.GetPortfolioAsync</c>.
/// Two neighbouring endpoints are deliberately NOT used for holdings, because a live capture on
/// 2026-08-18 showed what they actually are: <c>GetJSPorfolioDetails</c> returned <c>[]</c> on an
/// account holding eight positions (it is the intraday-trading view, empty when nothing traded that
/// day), and <c>GetExposureData</c> returns three pre-rendered HTML table fragments joined by
/// <c>'|'</c> — cash/collateral totals only, no per-symbol rows, and HTML rather than JSON.
/// <c>GetCollaterals</c> is the only one of the three that answers "what do I own".
/// </para>
/// </summary>
public sealed class AhkCollateralHolding
{
    [JsonPropertyName("symbol")]        public string? Symbol { get; set; }
    [JsonPropertyName("market")]        public string? Market { get; set; }

    /// <summary>Shares held. The portal's own column heading for this is "Quantity".</summary>
    [JsonPropertyName("quantityTotal")] public decimal? QuantityTotal { get; set; }

    /// <summary>Weighted average cost of the position.</summary>
    [JsonPropertyName("avgRateBuy")]    public decimal? AvgRateBuy { get; set; }

    [JsonPropertyName("avgRateSell")]   public decimal? AvgRateSell { get; set; }

    /// <summary>Mark-to-market price — the current valuation price, not necessarily the last trade.</summary>
    [JsonPropertyName("mtmPrice")]      public decimal? MtmPrice { get; set; }

    /// <summary>
    /// Market value. Verified against all eight live rows on 2026-08-18: this is exactly
    /// <see cref="MtmPrice"/> × <see cref="QuantityTotal"/>, so it is taken as reported rather
    /// than recomputed.
    /// </summary>
    [JsonPropertyName("amount")]        public decimal? Amount { get; set; }

    /// <summary>
    /// Unrealised P/L on the open position. Verified against all eight live rows as exactly
    /// (<see cref="MtmPrice"/> − <see cref="AvgRateBuy"/>) × <see cref="QuantityTotal"/>. The name is
    /// the portal's — it means "not yet settled", not "not yet calculated".
    /// </summary>
    [JsonPropertyName("unsettled")]     public decimal? Unsettled { get; set; }

    /// <summary>Realised P/L already settled. Distinct from <see cref="Unsettled"/>.</summary>
    [JsonPropertyName("plSettled")]     public decimal? PlSettled { get; set; }

    /// <summary>Quantity already sold but not yet settled out of the position.</summary>
    [JsonPropertyName("sold")]          public decimal? Sold { get; set; }

    /// <summary>Quantity committed to resting sell orders.</summary>
    [JsonPropertyName("pendingSell")]   public decimal? PendingSell { get; set; }

    /// <summary>Haircut percentage applied when the holding is counted as collateral.</summary>
    [JsonPropertyName("haircutPer")]    public decimal? HaircutPer { get; set; }

    /// <summary>Per-share value after the haircut.</summary>
    [JsonPropertyName("margVal")]       public decimal? MargVal { get; set; }
}

/// <summary>
/// One order-lifecycle event from <c>GET /Home/GetActivityLog?symbol=&amp;type=&amp;account=</c>.
///
/// <para>
/// This is the audit trail of what happened to an order, and together with
/// <see cref="AhkOutstandingOrder"/> it is what gives <c>IBrokerStateReader</c> something real to
/// reconcile against. Several events share one <see cref="OrderNo"/> — a live capture on 2026-08-18
/// showed order 6427 appearing twice, once as <c>PEN</c> and later as <c>CLX</c>.
/// </para>
/// </summary>
public sealed class AhkActivityLogEntry
{
    [JsonPropertyName("orderNo")]     public string? OrderNo { get; set; }
    [JsonPropertyName("hOrderNo")]    public string? HOrderNo { get; set; }

    /// <summary>Ticker. Called "scrip" here, as in the outstanding book.</summary>
    [JsonPropertyName("scrip")]       public string? Scrip { get; set; }

    [JsonPropertyName("market")]      public string? Market { get; set; }

    /// <summary><c>BUY</c> or <c>SEL</c>.</summary>
    [JsonPropertyName("type")]        public string? Type { get; set; }

    /// <summary>
    /// The event. Confirmed against the live portal on 2026-08-19: <c>QUE</c> queued, <c>APT</c> accepted
    /// (a stop order awaiting its trigger), <c>REJ</c> rejected by the exchange, <c>CLX</c> cancelled;
    /// <c>PEN</c> was seen in an earlier session. This is the ONLY place an order's verdict appears — the
    /// <c>PlaceOrder</c> response is byte-identical for a queued and a rejected order.
    ///
    /// <para>
    /// Treat any unrecognised value as "something happened", never as a fill. And note the trap this
    /// comment previously walked into: <see cref="FillVolume"/> is NOT a fill indicator here. A REJ row
    /// arrived with <c>fillVolume 1</c>, <c>price 0</c> and <c>totalValue 0</c> — a full quantity on an
    /// order that never traded. A fill needs a positive quantity AND a real price AND an action that is
    /// not REJ or CLX.
    /// </para>
    /// </summary>
    [JsonPropertyName("action")]      public string? Action { get; set; }

    [JsonPropertyName("price")]       public decimal? Price { get; set; }

    /// <summary>Quantity for this event. Zero on the cancellation events observed.</summary>
    [JsonPropertyName("value")]       public decimal? Value { get; set; }

    /// <summary>Quantity filled. Zero on every event observed live — no fills occurred that session.</summary>
    [JsonPropertyName("fillVolume")]  public decimal? FillVolume { get; set; }

    [JsonPropertyName("totalVolume")] public decimal? TotalVolume { get; set; }
    [JsonPropertyName("totalValue")]  public decimal? TotalValue { get; set; }
    [JsonPropertyName("remaining")]   public decimal? Remaining { get; set; }

    /// <summary>Time of day, <c>HH:mm:ss</c>. The portal sends no date — these are today's events.</summary>
    [JsonPropertyName("time")]        public string? Time { get; set; }

    /// <summary>Observed null on every live row; the account code is on <see cref="Trader"/> instead.</summary>
    [JsonPropertyName("account")]     public string? Account { get; set; }

    [JsonPropertyName("trader")]      public string? Trader { get; set; }
    [JsonPropertyName("flag")]        public string? Flag { get; set; }
}

/// <summary>
/// One execution from <c>GET /Home/GetTradeLog?symbol=&amp;type=&amp;account=</c> — the fills log.
///
/// <para>
/// Field names are taken from the portal's own destructuring in <c>site.js</c>; the shape could not
/// be confirmed against data, because the account had no fills on the capture day and the endpoint
/// returned <c>[]</c>. Every field is therefore nullable and no consumer may assume one is present.
/// It has no <c>action</c> — a row here IS an execution, which is why fills are read from this
/// endpoint rather than inferred from <see cref="AhkActivityLogEntry"/>.
/// </para>
/// </summary>
public sealed class AhkTradeLogEntry
{
    [JsonPropertyName("orderNo")]     public string? OrderNo { get; set; }
    [JsonPropertyName("scrip")]       public string? Scrip { get; set; }
    [JsonPropertyName("market")]      public string? Market { get; set; }
    [JsonPropertyName("type")]        public string? Type { get; set; }
    [JsonPropertyName("price")]       public decimal? Price { get; set; }
    [JsonPropertyName("value")]       public decimal? Value { get; set; }
    [JsonPropertyName("fillVolume")]  public decimal? FillVolume { get; set; }
    [JsonPropertyName("totalVolume")] public decimal? TotalVolume { get; set; }
    [JsonPropertyName("totalValue")]  public decimal? TotalValue { get; set; }
    [JsonPropertyName("remaining")]   public decimal? Remaining { get; set; }
    [JsonPropertyName("time")]        public string? Time { get; set; }
    [JsonPropertyName("account")]     public string? Account { get; set; }
    [JsonPropertyName("trader")]      public string? Trader { get; set; }
}
