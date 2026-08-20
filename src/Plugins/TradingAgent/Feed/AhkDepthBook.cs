using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace TradingAgent.Feed;

/// <summary>
/// One price level of the aggregated book (MBP — market by price), as the portal publishes it.
///
/// <para>
/// <b>Each row carries BOTH sides.</b> The unprefixed fields are the BID ladder and the
/// <c>s</c>-prefixed ones the ASK ladder, zipped together by index: row 0 is the best bid beside the
/// best ask, row 1 the second level of each, and so on. It is not a flat list of levels with a side
/// marker, so the two sides must be unzipped before either can be read as a ladder — treating a row
/// as a single side would pair every bid with the wrong quantity.
/// </para>
/// </summary>
public sealed class AhkDepthLevelRow
{
    [JsonPropertyName("orders")]  public int? BidOrders { get; set; }
    [JsonPropertyName("volume")]  public long? BidVolume { get; set; }
    [JsonPropertyName("price")]   public decimal? BidPrice { get; set; }
    [JsonPropertyName("sOrders")] public int? AskOrders { get; set; }
    [JsonPropertyName("sVolume")] public long? AskVolume { get; set; }
    [JsonPropertyName("sPrice")]  public decimal? AskPrice { get; set; }
}

/// <summary>One resting order of the un-aggregated book (MBO — market by order). Same paired layout.</summary>
public sealed class AhkDepthOrderRow
{
    [JsonPropertyName("price")]     public decimal? BidPrice { get; set; }
    [JsonPropertyName("volume")]    public long? BidVolume { get; set; }
    [JsonPropertyName("flag")]      public string? BidFlag { get; set; }
    /// <summary>Always null in captures — the exchange does not disclose counterparty order numbers.</summary>
    [JsonPropertyName("orderNo")]   public string? BidOrderNo { get; set; }
    [JsonPropertyName("sPrice")]    public decimal? AskPrice { get; set; }
    [JsonPropertyName("sVolume")]   public long? AskVolume { get; set; }
    [JsonPropertyName("sFlag")]     public string? AskFlag { get; set; }
    [JsonPropertyName("sOrderNo")]  public string? AskOrderNo { get; set; }
}

/// <summary>
/// Holds the most recent market-depth ladder per symbol, from the <c>mbpFeed</c> / <c>mboFeed</c>
/// arrays of <c>GET /Home/GetFeed</c>.
///
/// <para>
/// Three properties of the portal's publishing shaped this class, all established from a live capture
/// on 2026-08-20 with the market open (PPL on Page5):
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Depth is published only when it CHANGES, as a full replacement.</b> Most polls carry empty
/// arrays. So an empty array means "nothing changed", never "the book is empty", and the last known
/// ladder has to be retained — a consumer that cleared on every empty poll would see the book blink
/// out several times a second.
/// </item>
/// <item>
/// <b>The array is fixed-length and zero-padded.</b> Thirteen rows arrive whether or not there are
/// thirteen levels, with unused rows all zeros. Those are dropped on ingest: a zero price is not a
/// real level, and leaving them in makes "lowest ask" resolve to 0 and inflates total depth with
/// nothing. This is the same "zero means unknown" rule the quote path already applies.
/// </item>
/// <item>
/// <b>Rows carry no symbol.</b> The portal follows one depth symbol at a time, so rows are attributed
/// to whichever symbol is currently subscribed. That is why <see cref="SubscribedSymbol"/> is
/// authoritative rather than decorative.
/// </item>
/// </list>
/// </summary>
public sealed class AhkDepthBook
{
    /// <summary>One symbol's latest depth, both feeds, with the time each arrived.</summary>
    public sealed record DepthEntry(
        string Market,
        string Symbol,
        /// <summary>Aggregated levels, padding removed, best first.</summary>
        IReadOnlyList<AhkDepthLevelRow> Levels,
        /// <summary>Individual resting orders, padding removed.</summary>
        IReadOnlyList<AhkDepthOrderRow> Orders,
        DateTime? LevelsAtUtc,
        DateTime? OrdersAtUtc)
    {
        /// <summary>Best bid — the first level with a real price.</summary>
        public decimal? BestBid => Levels.FirstOrDefault(l => l.BidPrice is > 0)?.BidPrice;
        public decimal? BestAsk => Levels.FirstOrDefault(l => l.AskPrice is > 0)?.AskPrice;

        public long? BidVolumeAtTouch => Levels.FirstOrDefault(l => l.BidPrice is > 0)?.BidVolume;
        public long? AskVolumeAtTouch => Levels.FirstOrDefault(l => l.AskPrice is > 0)?.AskVolume;

        /// <summary>Ask minus bid, or null when either side is empty (a one-sided book is normal).</summary>
        public decimal? Spread =>
            BestBid is { } bid && BestAsk is { } ask ? ask - bid : null;

        /// <summary>Total resting quantity per side across the visible ladder.</summary>
        public long TotalBidVolume => Levels.Where(l => l.BidPrice is > 0).Sum(l => l.BidVolume ?? 0);
        public long TotalAskVolume => Levels.Where(l => l.AskPrice is > 0).Sum(l => l.AskVolume ?? 0);

        /// <summary>
        /// Book imbalance, −1 (all offered) to +1 (all bid). The single most useful scalar from depth:
        /// it says which side is heavier without needing the whole ladder. Null when the book is empty.
        /// </summary>
        public decimal? Imbalance
        {
            get
            {
                var total = TotalBidVolume + TotalAskVolume;
                return total == 0 ? null
                    : Math.Round((decimal)(TotalBidVolume - TotalAskVolume) / total, 4);
            }
        }
    }

    private readonly ConcurrentDictionary<string, DepthEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Symbol currently subscribed for depth, or null. One at a time, matching the portal.</summary>
    public string? SubscribedSymbol { get; set; }

    /// <summary>Real (non-padding) depth rows ever ingested, so "subscribed but silent" is
    /// distinguishable from "never subscribed".</summary>
    public long RowsSeen { get; private set; }

    private static string Key(string market, string symbol) => $"{market}:{symbol}";

    /// <summary>
    /// Ingests one poll's depth arrays. Empty arrays are ignored rather than treated as an empty book —
    /// see the class remarks. Each non-empty array fully replaces that side's previous contents.
    /// </summary>
    public void Ingest(
        IReadOnlyList<AhkDepthLevelRow>? levels,
        IReadOnlyList<AhkDepthOrderRow>? orders,
        string market,
        string? symbol)
    {
        symbol = symbol?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(symbol)) return;

        // Drop the zero-filled tail. A row with no price on either side is padding, not a level.
        var realLevels = levels?
            .Where(l => l.BidPrice is > 0 || l.AskPrice is > 0)
            .ToList();
        var realOrders = orders?
            .Where(o => o.BidPrice is > 0 || o.AskPrice is > 0)
            .ToList();

        if (realLevels is null or { Count: 0 } && realOrders is null or { Count: 0 }) return;

        var now = DateTime.UtcNow;
        var key = Key(market, symbol);
        RowsSeen += (realLevels?.Count ?? 0) + (realOrders?.Count ?? 0);

        _entries.AddOrUpdate(key,
            _ => new DepthEntry(
                market, symbol,
                realLevels ?? [], realOrders ?? [],
                realLevels is { Count: > 0 } ? now : null,
                realOrders is { Count: > 0 } ? now : null),
            (_, existing) => existing with
            {
                // Only replace a side that actually arrived; the two do not always publish together,
                // and clearing the other would make the ladder flicker.
                Levels       = realLevels is { Count: > 0 } ? realLevels : existing.Levels,
                Orders       = realOrders is { Count: > 0 } ? realOrders : existing.Orders,
                LevelsAtUtc  = realLevels is { Count: > 0 } ? now : existing.LevelsAtUtc,
                OrdersAtUtc  = realOrders is { Count: > 0 } ? now : existing.OrdersAtUtc
            });
    }

    /// <summary>Latest depth for one symbol, or null when none has arrived.</summary>
    public DepthEntry? Get(string market, string symbol) =>
        _entries.TryGetValue(Key(market, symbol), out var entry) ? entry : null;

    public IReadOnlyList<DepthEntry> All() => _entries.Values.ToList();

    /// <summary>Drops everything. Depth is session-scoped: a new session carries no subscription, so a
    /// retained "following PPL" would claim one that no longer exists.</summary>
    public void Clear()
    {
        _entries.Clear();
        SubscribedSymbol = null;
    }
}
