using System.Collections.Concurrent;
using TradingAgent.Research;

namespace TradingAgent.Feed;

/// <summary>
/// The current state of every symbol the feed has reported this session, as an in-memory book.
///
/// <para>
/// <b>Why a book rather than passing responses through.</b> It could not be established whether
/// <c>GetFeed</c> returns a full snapshot of the subscribed set or only what changed since the last
/// poll (see <c>docs/ahk-feed-api.md</c>). A book is correct under both readings: an upsert of a
/// full snapshot is idempotent, and an upsert of a delta accumulates into the same state. Reading a
/// response directly would silently break the moment the feed turned out to be a delta — symbols
/// would blink in and out of existence between polls, and a monitoring pass would read that as
/// "price unavailable" for whatever had not ticked in the last two seconds.
/// </para>
///
/// <para>
/// Entries are keyed by <c>(market, symbol)</c> because a symbol trades on more than one board and
/// the odd-lot board's prices are not the regular board's. They are never removed within a session;
/// staleness is decided at read time against a per-quote timestamp, so a symbol that stops trading
/// mid-session ages out rather than vanishing.
/// </para>
/// </summary>
public sealed class AhkQuoteBook
{
    private readonly ConcurrentDictionary<AhkSymbolKey, PsxLiveQuote> _quotes = new();

    /// <summary>Symbols currently held, regardless of age.</summary>
    public int Count => _quotes.Count;

    /// <summary>When the book last took an update that carried a usable price.</summary>
    public DateTime? LastUpdateUtc { get; private set; }

    /// <summary>
    /// Folds one feed response into the book. Returns how many entries were written — zero is
    /// normal and simply means nothing has traded since the previous poll.
    /// </summary>
    public int Apply(IReadOnlyList<AhkFeedQuote> feed, DateTime nowUtc)
    {
        var applied = 0;

        foreach (var raw in feed)
        {
            var key = AhkSymbolKey.Of(raw.Mkt, raw.Symbol);
            if (!key.IsValid) continue;

            var quote = AhkQuoteMapper.ToLiveQuote(raw, nowUtc);
            if (quote is null) continue;

            // AddOrUpdate rather than an assignment: a later poll may carry a partial record where
            // the portal republished only some fields, and merging keeps the last known good value
            // for whatever this message did not mention.
            _quotes.AddOrUpdate(key, quote, (_, existing) => AhkQuoteMapper.Merge(existing, quote));
            applied++;
        }

        if (applied > 0) LastUpdateUtc = nowUtc;
        return applied;
    }

    /// <summary>
    /// Every quote on <paramref name="market"/> that is no older than <paramref name="maxAge"/>,
    /// keyed by bare symbol for callers that do not model boards.
    ///
    /// <para>
    /// The age cut is the safety property here. A polled feed that has silently died is
    /// indistinguishable from a market where nothing is trading — both produce no new data — and
    /// serving the last known price indefinitely is how a stop-loss comes to be evaluated against a
    /// number from hours ago. Past the cut a symbol is simply absent, and the composite source falls
    /// back to PSX for it.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, PsxLiveQuote> Snapshot(string market, TimeSpan maxAge, DateTime nowUtc)
    {
        var wanted = market.Trim().ToUpperInvariant();
        var result = new Dictionary<string, PsxLiveQuote>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, quote) in _quotes)
        {
            if (!string.Equals(key.Market, wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (nowUtc - quote.RetrievedAtUtc > maxAge) continue;
            result[key.Symbol] = quote;
        }

        return result;
    }

    /// <summary>
    /// Drops book entries for <paramref name="market"/> that are not in <paramref name="symbols"/>,
    /// and returns how many were removed. Called after the subscription changes.
    ///
    /// <para>
    /// Without this, removing a symbol from the watchlist stops the portal sending it but leaves its
    /// last quote in the book, still inside the freshness window — so it would keep being served as a
    /// live price for up to <c>MaxQuoteAgeSeconds</c> after it stopped being watched, and would keep
    /// inflating the fresh-symbol count that operators use to judge whether the feed is healthy.
    /// Entries on other boards are left alone; only the subscribed market is managed here.
    /// </para>
    /// </summary>
    public int RetainOnly(string market, IReadOnlyCollection<string> symbols)
    {
        var wanted = market.Trim();
        var keep = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        foreach (var key in _quotes.Keys)
        {
            if (!string.Equals(key.Market, wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (keep.Contains(key.Symbol)) continue;
            if (_quotes.TryRemove(key, out _)) removed++;
        }

        return removed;
    }

    /// <summary>Drops everything. Used when the session is replaced, so one day's prices cannot leak into the next.</summary>
    public void Clear()
    {
        _quotes.Clear();
        LastUpdateUtc = null;
    }
}
