using System.Collections.Concurrent;
using System.Text.Json;

namespace TradingAgent.Feed;

/// <summary>
/// Holds the most recent market-depth payload per symbol, as published in the
/// <c>mboFeed</c> / <c>mbpFeed</c> arrays of <c>GET /Home/GetFeed</c>.
///
/// <para>
/// <b>Why the payload is kept raw.</b> The portal's depth arrays had never been captured when this was
/// written — nothing in the plugin had ever subscribed to <c>MBP-FEED</c> or <c>MBO-FEED</c>, so the
/// arrays were always empty and their element shape is unknown. Writing a typed model now would be
/// guessing at field names, and a wrong guess deserialises to a ladder full of nulls that looks like a
/// quiet book rather than a parsing failure. So the raw <see cref="JsonElement"/> is preserved, the
/// observed keys are recorded, and a typed model can be added from real data once
/// <see cref="ObservedMbpKeys"/> and <see cref="ObservedMboKeys"/> report what the portal actually
/// sends. Depth is a decision input; inventing its schema is not acceptable.
/// </para>
///
/// <para>
/// <b>MBO versus MBP.</b> MBP (market by price) aggregates resting quantity per price level — the
/// ladder a trader reads. MBO (market by order) lists individual orders. Both are captured, kept
/// separate, and never merged, because a level count and an order count are different quantities and
/// conflating them would misstate available liquidity.
/// </para>
/// </summary>
public sealed class AhkDepthBook
{
    /// <summary>One symbol's latest depth, both feeds, with the time each arrived.</summary>
    public sealed record DepthEntry(
        string Market,
        string Symbol,
        /// <summary>Raw <c>mbpFeed</c> rows for this symbol, exactly as published.</summary>
        IReadOnlyList<JsonElement> ByPrice,
        /// <summary>Raw <c>mboFeed</c> rows for this symbol, exactly as published.</summary>
        IReadOnlyList<JsonElement> ByOrder,
        DateTime? ByPriceAtUtc,
        DateTime? ByOrderAtUtc);

    private readonly ConcurrentDictionary<string, DepthEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Field names seen on <c>mbpFeed</c> rows. Populated on first arrival and exposed so the shape can
    /// be read off a running system rather than reverse-engineered again — this is the artefact that
    /// lets a typed model replace the raw one.
    /// </summary>
    public IReadOnlyCollection<string> ObservedMbpKeys => _mbpKeys.Keys.ToList();
    public IReadOnlyCollection<string> ObservedMboKeys => _mboKeys.Keys.ToList();

    private readonly ConcurrentDictionary<string, byte> _mbpKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _mboKeys = new(StringComparer.Ordinal);

    /// <summary>Symbol currently subscribed for depth, or null. See <see cref="AhkFeedWorker"/> for why
    /// this is a single symbol rather than a set.</summary>
    public string? SubscribedSymbol { get; set; }

    /// <summary>Total depth rows ever ingested, so "subscribed but silent" is distinguishable from
    /// "never subscribed".</summary>
    public long RowsSeen { get; private set; }

    private static string Key(string market, string symbol) => $"{market}:{symbol}";

    /// <summary>
    /// Ingests the depth arrays from one feed poll. Rows are grouped by their own symbol field when one
    /// can be found, and otherwise attributed to <paramref name="fallbackSymbol"/> — the portal only
    /// ever has one depth subscription active, so a row without an identifiable symbol still belongs to
    /// a known instrument rather than nowhere.
    /// </summary>
    public void Ingest(
        IReadOnlyList<JsonElement>? mbpRows,
        IReadOnlyList<JsonElement>? mboRows,
        string market,
        string? fallbackSymbol)
    {
        var now = DateTime.UtcNow;
        RecordKeys(mbpRows, _mbpKeys);
        RecordKeys(mboRows, _mboKeys);

        foreach (var (rows, isByPrice) in new[] { (mbpRows, true), (mboRows, false) })
        {
            if (rows is null or { Count: 0 }) continue;
            RowsSeen += rows.Count;

            foreach (var group in rows.GroupBy(r => SymbolOf(r) ?? fallbackSymbol))
            {
                if (group.Key is null) continue;
                var list = group.ToList();
                var key = Key(market, group.Key);

                _entries.AddOrUpdate(key,
                    _ => new DepthEntry(
                        market, group.Key,
                        isByPrice ? list : [],
                        isByPrice ? [] : list,
                        isByPrice ? now : null,
                        isByPrice ? null : now),
                    (_, existing) => isByPrice
                        ? existing with { ByPrice = list, ByPriceAtUtc = now }
                        : existing with { ByOrder = list, ByOrderAtUtc = now });
            }
        }
    }

    /// <summary>Latest depth for one symbol, or null when none has arrived.</summary>
    public DepthEntry? Get(string market, string symbol) =>
        _entries.TryGetValue(Key(market, symbol), out var entry) ? entry : null;

    /// <summary>Everything held, for diagnostics.</summary>
    public IReadOnlyList<DepthEntry> All() => _entries.Values.ToList();

    /// <summary>Drops everything — used when the session is replaced, since depth is session-scoped.</summary>
    public void Clear()
    {
        _entries.Clear();
        SubscribedSymbol = null;
    }

    /// <summary>
    /// Best-effort symbol extraction. Tries the spellings the portal uses elsewhere in the same
    /// response (<c>symbol</c>, <c>sym</c>, <c>scrip</c>) rather than inventing new ones. Returning
    /// null is expected and handled, not an error.
    /// </summary>
    private static string? SymbolOf(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in new[] { "symbol", "sym", "scrip", "Symbol" })
        {
            if (row.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim().ToUpperInvariant();
            }
        }
        return null;
    }

    private static void RecordKeys(IReadOnlyList<JsonElement>? rows, ConcurrentDictionary<string, byte> sink)
    {
        if (rows is null) return;
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            foreach (var property in row.EnumerateObject()) sink.TryAdd(property.Name, 0);
        }
    }
}
