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
    [JsonPropertyName("mboFeed")]       public List<object>? MboFeed { get; set; }
    [JsonPropertyName("mbpFeed")]       public List<object>? MbpFeed { get; set; }
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

/// <summary>Identity of a tradable line on the portal. A symbol trades on more than one board.</summary>
public readonly record struct AhkSymbolKey(string Market, string Symbol)
{
    public static AhkSymbolKey Of(string? market, string? symbol) =>
        new((market ?? "").Trim().ToUpperInvariant(), (symbol ?? "").Trim().ToUpperInvariant());

    public bool IsValid => Market.Length > 0 && Symbol.Length > 0;

    public override string ToString() => $"{Market}:{Symbol}";
}
