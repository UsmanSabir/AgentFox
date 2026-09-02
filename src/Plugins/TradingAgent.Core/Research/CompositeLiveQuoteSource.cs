using Microsoft.Extensions.Logging;

namespace TradingAgent.Research;

/// <summary>
/// Merges several quote sources in priority order: the first source to cover a symbol wins, and
/// later sources fill only what the earlier ones left out.
///
/// <para>
/// <b>Fill rather than fail over.</b> The obvious design — "use the broker feed, or PSX if the
/// broker feed is down" — is wrong here, because the two sources differ in COVERAGE as well as
/// freshness. The broker feed carries only what has been subscribed (a few hundred symbols at most,
/// and only those that have ticked since the session started), while the PSX market watch carries
/// the whole market. Treating a partially-populated broker feed as "the live prices" would silently
/// drop every unsubscribed symbol from the snapshot, and a watchlist pass would read that as "no
/// price available" for symbols that PSX could have priced perfectly well.
/// </para>
///
/// <para>
/// So every enabled source is consulted, highest priority first, and each contributes only the
/// symbols not already claimed. Each quote keeps its own <see cref="PsxLiveQuote.Source"/> tag, so a
/// merged snapshot can still answer "where did THIS number come from" per symbol.
/// </para>
/// </summary>
public sealed class CompositeLiveQuoteSource : ILiveQuoteSource
{
    private readonly IReadOnlyList<ILiveQuoteSource> _sources;
    private readonly ILogger<CompositeLiveQuoteSource> _logger;

    /// <param name="sources">
    /// In priority order, most-preferred first. Registration order in the DI container is the
    /// priority order.
    /// </param>
    public CompositeLiveQuoteSource(
        IEnumerable<ILiveQuoteSource> sources,
        ILogger<CompositeLiveQuoteSource> logger)
    {
        // Guard against a composite being registered into its own source list, which would recurse
        // until the stack ran out — an easy mistake to make when adding a third source later.
        // Highest priority first; OrderByDescending is stable, so sources that declare nothing keep
        // their registration order and behave exactly as they did before precedence was declarable.
        _sources = sources
            .Where(s => s is not CompositeLiveQuoteSource)
            .OrderByDescending(s => s.Priority)
            .ToList();
        _logger = logger;
    }

    public string Name => "composite";

    public bool IsEnabled => _sources.Any(s => s.IsEnabled);

    public async Task<LiveQuoteSnapshot> GetQuotesAsync(CancellationToken ct = default)
    {
        var merged = new Dictionary<string, PsxLiveQuote>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var contributions = new List<string>();
        string? primary = null;
        var retrievedAt = DateTime.MinValue;

        foreach (var source in _sources)
        {
            if (!source.IsEnabled) continue;

            LiveQuoteSnapshot snapshot;
            try
            {
                snapshot = await source.GetQuotesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The interface forbids throwing, but a source is ordinary code and this composite
                // is on the path of every monitoring pass. One misbehaving source must not cost the
                // snapshot the others could still have produced.
                _logger.LogWarning(ex, "[Quotes] Source '{Source}' threw; skipping it this pass.", source.Name);
                warnings.Add($"Quote source '{source.Name}' failed ({ex.Message}).");
                continue;
            }

            warnings.AddRange(snapshot.Warnings);

            // First source to cover a symbol wins, and the ordering above is what makes "first" mean
            // "most preferred" rather than "registered earliest".
            var added = 0;
            foreach (var (symbol, quote) in snapshot.Quotes)
            {
                if (merged.TryAdd(symbol, quote)) added++;
            }

            if (added > 0)
            {
                primary ??= source.Name;
                contributions.Add($"{source.Name}:{added}");
                if (snapshot.RetrievedAtUtc > retrievedAt) retrievedAt = snapshot.RetrievedAtUtc;
            }
        }

        if (contributions.Count > 1)
        {
            _logger.LogDebug("[Quotes] Merged snapshot of {Total} symbol(s) from {Breakdown}.",
                merged.Count, string.Join(", ", contributions));
        }

        if (merged.Count == 0 && warnings.Count == 0)
            warnings.Add("No quote source returned any prices.");

        return new LiveQuoteSnapshot
        {
            Quotes         = merged,
            Source         = primary ?? "none",
            RetrievedAtUtc = retrievedAt == DateTime.MinValue ? DateTime.UtcNow : retrievedAt,
            Warnings       = warnings
        };
    }
}
