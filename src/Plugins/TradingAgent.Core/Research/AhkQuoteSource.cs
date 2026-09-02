using AgentFox.Plugins;
using TradingAgent.Config;
using TradingAgent.Feed;

namespace TradingAgent.Research;

/// <summary>
/// The broker's live feed as an <see cref="ILiveQuoteSource"/>. Serves entirely from the in-memory
/// book that <see cref="AhkFeedWorker"/> maintains, so a read costs nothing and never blocks a
/// monitoring pass on the network.
///
/// <para>
/// This source is deliberately incapable of fetching. If the worker is not running, or has fallen
/// over, or the market is shut, the book simply has no fresh entries and this reports empty — which
/// is what makes the PSX fallback in <see cref="CompositeLiveQuoteSource"/> engage automatically
/// rather than needing to be told.
/// </para>
/// </summary>
public sealed class AhkQuoteSource : ILiveQuoteSource
{
    /// <summary>
    /// Above the PSX market watch. The broker's own feed is the venue this account trades on and is
    /// polled in seconds, where the market watch is a market-wide scrape cached for up to a minute.
    /// This was already the effective order — core registers this source first — so naming it changes
    /// nothing today; it makes the intent explicit and gives an edition something to outrank.
    /// </summary>
    public int Priority => 100;

    private readonly AhkQuoteBook _book;
    private readonly AhkFeedWorker _worker;
    private readonly IRuntimePluginOptions<AhkFeedConfig> _config;

    public AhkQuoteSource(
        AhkQuoteBook book,
        AhkFeedWorker worker,
        IRuntimePluginOptions<AhkFeedConfig> config)
    {
        _book = book;
        _worker = worker;
        _config = config;
    }

    public string Name => AhkQuoteMapper.SourceName;

    public bool IsEnabled => _config.Current.Enabled;

    public Task<LiveQuoteSnapshot> GetQuotesAsync(CancellationToken ct = default)
    {
        var cfg = _config.Current;

        if (!cfg.Enabled)
            return Task.FromResult(LiveQuoteSnapshot.Empty(Name));

        if (!_worker.IsHealthy)
        {
            return Task.FromResult(LiveQuoteSnapshot.Empty(
                Name, "The broker feed is failing its polls; prices fall back to the PSX market watch."));
        }

        var maxAge = TimeSpan.FromSeconds(Math.Max(30.0, cfg.MaxQuoteAgeSeconds));
        var market = string.IsNullOrWhiteSpace(cfg.Market) ? "REG" : cfg.Market;
        var quotes = _book.Snapshot(market, maxAge, DateTime.UtcNow);

        var warnings = new List<string>();
        if (quotes.Count == 0 && _book.Count > 0)
        {
            // The book holds symbols but every one has aged out — the feed has gone quiet without
            // reporting an error. Worth saying out loud, because the alternative reading ("nothing
            // is trading") is indistinguishable and much more reassuring than it deserves to be.
            warnings.Add(
                $"The broker feed has published nothing for over {maxAge.TotalMinutes:F0} minutes; " +
                "its quotes are being treated as stale.");
        }

        return Task.FromResult(new LiveQuoteSnapshot
        {
            Quotes         = quotes,
            Source         = Name,
            RetrievedAtUtc = _book.LastUpdateUtc ?? DateTime.UtcNow,
            Warnings       = warnings
        });
    }
}
