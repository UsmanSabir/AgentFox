using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Persistence;

namespace TradingAgent.Watchlist;

/// <summary>
/// The single answer to "which symbols does this apply to" — and, deliberately, three different
/// answers depending on what is being asked.
///
/// <para>
/// Before this existed there was one list, <see cref="TradingAgentOptions.AllowedSymbols"/>, doing two
/// unrelated jobs: it was both the analysis universe and the tradable universe. That conflation is why
/// a user could not watch a symbol without also making it tradable, and why anything added to the
/// watchlist would otherwise have had no archived history and therefore no weekly levels.
/// </para>
///
/// <list type="bullet">
///   <item><see cref="ForExecutionAsync"/> — AllowedSymbols ONLY. What may be ORDERED. This is the
///   list <see cref="Risk.TradingRiskEngine"/> enforces (it reads configuration directly, and is not
///   changed by any of this). Editing the watchlist must never widen it.</item>
///   <item><see cref="ForMonitoringAsync"/> — watchlist ∪ AllowedSymbols. What is CHARTED, SCANNED,
///   and ALERTED on. A superset is safe here: the output is information, not an order.</item>
///   <item><see cref="ForArchiveAsync"/> — which symbols get deep daily history. Same as monitoring by
///   default, because a monitored symbol with no history produces no weekly structure. Costs no extra
///   requests: a session fetch already returns every symbol in the market, so this only adds rows.</item>
/// </list>
///
/// <para>
/// The watchlist is seeded from AllowedSymbols on first use and independent afterwards. Results are
/// cached briefly because these are called per analysis, and invalidated on every mutation.
/// </para>
/// </summary>
public sealed class MonitoredUniverse
{
    /// <summary>
    /// How long a resolved universe is reused. Short on purpose: an edit calls <see cref="Invalidate"/>
    /// so the TTL only covers changes made by another process against the same database file.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ITradingRepository _repository;
    private readonly ILogger<MonitoredUniverse> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<string>? _cachedMonitoring;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public MonitoredUniverse(
        IOptions<TradingAgentOptions> options,
        ITradingRepository repository,
        ILogger<MonitoredUniverse> logger)
    {
        _options = options;
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Symbols that may actually be traded: the configured allow-list, normalized. Synchronous and
    /// database-free by design — this must not become dependent on mutable state.
    /// </summary>
    public IReadOnlyList<string> ForExecution() => Normalize(_options.Value.AllowedSymbols);

    /// <summary>Symbols to chart, scan, and raise alerts for: the watchlist plus the tradable list.</summary>
    public async Task<IReadOnlyList<string>> ForMonitoringAsync(CancellationToken ct = default)
    {
        if (_cachedMonitoring is { } cached && DateTime.UtcNow - _cachedAtUtc < CacheFor)
            return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cachedMonitoring is { } raced && DateTime.UtcNow - _cachedAtUtc < CacheFor)
                return raced;

            var allowed = ForExecution();
            var resolved = new List<string>(allowed);
            var seen = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);

            try
            {
                await SeedIfNeededAsync(allowed, ct);
                var watchlist = await _repository.GetWatchlistAsync(ct);
                foreach (var entry in watchlist.Entries)
                {
                    if (seen.Add(entry.Symbol)) resolved.Add(entry.Symbol);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Monitoring must degrade to the configured list rather than stop. Not cached, so the
                // next call retries the database instead of holding on to a truncated universe.
                _logger.LogWarning(ex,
                    "[Watchlist] Could not read the watchlist; monitoring falls back to AllowedSymbols.");
                return allowed;
            }

            _cachedMonitoring = resolved;
            _cachedAtUtc = DateTime.UtcNow;
            return resolved;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Symbols whose daily history is archived. The monitoring universe unless
    /// <see cref="TradingWatchlistOptions.ArchiveWatchlistSymbols"/> is turned off, which trades weekly
    /// levels for watchlist symbols against a smaller database.
    /// </summary>
    public async Task<IReadOnlyList<string>> ForArchiveAsync(CancellationToken ct = default) =>
        _options.Value.Watchlist.ArchiveWatchlistSymbols
            ? await ForMonitoringAsync(ct)
            : ForExecution();

    /// <summary>True when the symbol may be ordered (i.e. is in AllowedSymbols).</summary>
    public bool IsTradable(string symbol) =>
        ForExecution().Contains(symbol.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Drops the cached universe. Call after any watchlist mutation.</summary>
    public void Invalidate()
    {
        _cachedMonitoring = null;
        _cachedAtUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Seeds the watchlist from the configured list on first use. Separate from the reset action: this
    /// only ever fires when the watchlist has never been seeded, so a user who empties it deliberately
    /// does not get it refilled behind their back.
    /// </summary>
    public async Task SeedIfNeededAsync(IReadOnlyList<string>? allowed = null, CancellationToken ct = default)
    {
        if (!_options.Value.Watchlist.SeedFromAllowedSymbols) return;

        var seed = allowed ?? ForExecution();
        if (seed.Count == 0) return;

        await _repository.EnsureWatchlistSeededAsync(seed, SeedHash(seed), ct);
    }

    /// <summary>
    /// Fingerprint of a seed list, order-insensitive. Stored with the watchlist so the UI can report
    /// that the configured allow-list has changed since seeding — a prompt to reset, never an
    /// automatic one.
    /// </summary>
    public static string SeedHash(IReadOnlyList<string> symbols)
    {
        var canonical = string.Join(
            '|', symbols.Select(s => s.Trim().ToUpperInvariant()).OrderBy(s => s, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    /// <summary>Current fingerprint of the configured allow-list.</summary>
    public string CurrentSeedHash() => SeedHash(ForExecution());

    private static List<string> Normalize(IEnumerable<string> symbols) =>
        symbols.Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
