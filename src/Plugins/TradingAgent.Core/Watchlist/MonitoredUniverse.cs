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
///   <item><see cref="ForExecutionAsync"/> — the explicitly selected execution source: configured
///   AllowedSymbols (the default) or the editable watchlist. This authoritative snapshot is passed
///   into <see cref="Risk.TradingRiskEngine"/> at the execution boundary.</item>
///   <item><see cref="ForMonitoringAsync"/> — watchlist ∪ AllowedSymbols. What is CHARTED, SCANNED,
///   and ALERTED on. A superset is safe here: the output is information, not an order.</item>
///   <item><see cref="ForArchiveAsync"/> — which symbols get deep daily history. Same as monitoring by
///   default, because a monitored symbol with no history produces no weekly structure. Costs no extra
///   requests: a session fetch already returns every symbol in the market, so this only adds rows.</item>
///   <item><see cref="ManualOnlyAsync"/> — the DENY set: symbols no automation may originate an order
///   for. Subtracted from nothing above, because a manual-only symbol is still watched, charted,
///   scanned and alerted on; what it loses is unattended execution.</item>
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

    /// <summary>
    /// Last known manual-only set from the watchlist, WITHOUT the configured list folded in. Written
    /// under <see cref="_gate"/>, read without it by <see cref="IsManualOnlySnapshot"/> — a reference
    /// swap of an immutable set, so a racing reader sees the old set or the new one, never a torn one.
    /// </summary>
    private volatile HashSet<string> _manualOnlyFromWatchlist = new(StringComparer.OrdinalIgnoreCase);

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
    /// The normalized, configured allow-list. Used for first-run seeding and Reset even when the
    /// watchlist is the selected execution source.
    /// </summary>
    public IReadOnlyList<string> ConfiguredAllowedSymbols() => Normalize(_options.Value.AllowedSymbols);

    /// <summary>
    /// Symbols that may actually be traded. Watchlist mode deliberately reads through to storage on
    /// every execution attempt: a removed symbol must not remain tradable for a cache TTL. Failure to
    /// resolve mutable policy fails closed rather than falling back to a wider or different source.
    /// </summary>
    public async Task<IReadOnlyList<string>> ForExecutionAsync(CancellationToken ct = default)
    {
        if (_options.Value.ExecutionUniverseSource == TradingExecutionUniverseSource.AllowedSymbols)
            return ConfiguredAllowedSymbols();

        try
        {
            await SeedIfNeededAsync(ct: ct);
            var watchlist = await _repository.GetWatchlistAsync(ct);
            return Normalize(watchlist.Entries.Select(entry => entry.Symbol));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "[Watchlist] Could not resolve the selected execution universe; execution fails closed.");
            return [];
        }
    }

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

            var allowed = ConfiguredAllowedSymbols();
            var resolved = new List<string>(allowed);
            var seen = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);

            try
            {
                await SeedIfNeededAsync(allowed, ct);
                var watchlist = await _repository.GetWatchlistAsync(ct);
                // One read, two answers. The monitor calls this every pass, so folding the deny set in
                // here is what keeps IsManualOnlySnapshot warm for the synchronous callers without a
                // second query against the same table.
                var manual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in watchlist.Entries)
                {
                    if (seen.Add(entry.Symbol)) resolved.Add(entry.Symbol);
                    if (!entry.AutoTradeEnabled) manual.Add(entry.Symbol);
                }
                _manualOnlyFromWatchlist = manual;
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
            : ConfiguredAllowedSymbols();

    /// <summary>True when the symbol is in the selected execution universe.</summary>
    public async Task<bool> IsTradableAsync(string symbol, CancellationToken ct = default) =>
        (await ForExecutionAsync(ct)).Contains(symbol.Trim(), StringComparer.OrdinalIgnoreCase);

    // ── Manual-only: the deny set ─────────────────────────────────────────────
    // Deliberately NOT part of any list above. A manual-only symbol keeps every bit of its analysis;
    // what it loses is the right of automation to originate an order for it. That is a different
    // question from "may this order exist", which is why it is answered here and enforced at the
    // automation boundary rather than in TradingRiskEngine — see TradingAgentOptions.ManualOnlySymbols.

    /// <summary>The configured half of the deny set: durable, and not editable over the web API.</summary>
    public IReadOnlyList<string> ConfiguredManualOnly() => Normalize(_options.Value.ManualOnlySymbols);

    /// <summary>
    /// Every symbol automation must not trade: the configured list UNION each watchlist entry with
    /// automation switched off. Authoritative — reads through to the watchlist.
    /// </summary>
    public async Task<IReadOnlySet<string>> ManualOnlyAsync(CancellationToken ct = default)
    {
        // Refreshes _manualOnlyFromWatchlist as a side effect when the cache is cold, and is a cheap
        // no-op when it is warm.
        await ForMonitoringAsync(ct);
        return Combine(_manualOnlyFromWatchlist);
    }

    /// <summary>True when no automation may originate an order for this symbol.</summary>
    public async Task<bool> IsManualOnlyAsync(string symbol, CancellationToken ct = default) =>
        (await ManualOnlyAsync(ct)).Contains(symbol.Trim().ToUpperInvariant());

    /// <summary>
    /// The first symbol in <paramref name="symbols"/> that is manual-only, or null. Authoritative.
    /// Returning the offender rather than a bool is what lets the caller name it in the refusal.
    /// </summary>
    public async Task<string?> FirstManualOnlyAsync(
        IEnumerable<string?> symbols, CancellationToken ct = default)
    {
        var deny = await ManualOnlyAsync(ct);
        foreach (var symbol in symbols)
        {
            var normalized = symbol?.Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(normalized) && deny.Contains(normalized)) return normalized;
        }
        return null;
    }

    /// <summary>
    /// Synchronous, best-effort answer for callers that cannot await — the configured list (always
    /// current) plus the last watchlist read (up to <see cref="CacheFor"/> stale, or empty before the
    /// first read).
    ///
    /// <para>
    /// Deliberately the weaker of the two, and safe because it is never the only check: it exists so
    /// an unattended path can refuse EARLY with a reason naming the symbol, while
    /// <see cref="Manager.TradingManager"/> re-asks authoritatively at the execution boundary. A stale
    /// miss here therefore delays the refusal to the boundary; it does not lose it.
    /// </para>
    /// </summary>
    public bool IsManualOnlySnapshot(string? symbol)
    {
        var normalized = symbol?.Trim().ToUpperInvariant();
        return !string.IsNullOrEmpty(normalized) && Combine(_manualOnlyFromWatchlist).Contains(normalized);
    }

    /// <summary>The first manual-only symbol among <paramref name="symbols"/>, best-effort. See above.</summary>
    public string? FirstManualOnlySnapshot(IEnumerable<string?> symbols)
    {
        var deny = Combine(_manualOnlyFromWatchlist);
        foreach (var symbol in symbols)
        {
            var normalized = symbol?.Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(normalized) && deny.Contains(normalized)) return normalized;
        }
        return null;
    }

    /// <summary>
    /// Config ∪ watchlist. Both halves only ever ADD to the deny set: there is no "allow" entry that
    /// can cancel a configured one, so an operator cannot loosen the durable floor from the UI.
    /// </summary>
    private HashSet<string> Combine(IReadOnlySet<string> fromWatchlist)
    {
        var combined = new HashSet<string>(ConfiguredManualOnly(), StringComparer.OrdinalIgnoreCase);
        combined.UnionWith(fromWatchlist);
        return combined;
    }

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

        var seed = allowed ?? ConfiguredAllowedSymbols();
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
    public string CurrentSeedHash() => SeedHash(ConfiguredAllowedSymbols());

    private static List<string> Normalize(IEnumerable<string> symbols) =>
        symbols.Select(s => s.Trim().ToUpperInvariant())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
