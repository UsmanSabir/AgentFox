namespace TradingAgent.Research;

/// <summary>
/// The PSX data portal's market watch as an <see cref="ILiveQuoteSource"/>. This is the behaviour
/// the plugin has always had, unchanged — it is wrapped rather than rewritten so that introducing
/// the broker feed cannot alter the fallback path.
///
/// <para>
/// Caching, the stale-snapshot-on-empty rule, and the single-flight gate all stay inside
/// <see cref="PsxDataClient.GetMarketWatchAsync"/>; this type adds only the interface and the
/// source tag. Always enabled: it needs no credentials, and it is the floor everything else falls
/// back to.
/// </para>
/// </summary>
public sealed class PsxMarketWatchQuoteSource : ILiveQuoteSource
{
    /// <summary>
    /// The floor. This source covers every listed symbol and is never disabled, so anything that can
    /// price a symbol at all should outrank it — otherwise it claims the symbol first and the better
    /// source is never asked.
    /// </summary>
    public int Priority => 0;

    public const string SourceName = "psx";

    private readonly PsxDataClient _dataClient;

    public PsxMarketWatchQuoteSource(PsxDataClient dataClient) => _dataClient = dataClient;

    public string Name => SourceName;

    public bool IsEnabled => true;

    public async Task<LiveQuoteSnapshot> GetQuotesAsync(CancellationToken ct = default)
    {
        try
        {
            var quotes = await _dataClient.GetMarketWatchAsync(ct);

            // Quotes parsed before Source existed default to "psx", but tagging explicitly keeps the
            // provenance true even if the parser is later reused for another portal.
            var tagged = new Dictionary<string, PsxLiveQuote>(quotes.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (symbol, quote) in quotes)
                tagged[symbol] = quote with { Source = SourceName };

            return new LiveQuoteSnapshot
            {
                Quotes         = tagged,
                Source         = SourceName,
                RetrievedAtUtc = DateTime.UtcNow
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LiveQuoteSnapshot.Empty(
                SourceName, $"The PSX market watch is unavailable ({ex.Message}).");
        }
    }
}
