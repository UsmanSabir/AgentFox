namespace TradingAgent.Persistence;

using System.Text.Json;
using TradingAgent.Manager;
using TradingAgent.Reconciliation;

public interface ITradingRepository
{
    Task<string> CreateProposalAsync(
        string idempotencyKey,
        string proposalJson,
        string policyVersion,
        CancellationToken ct = default);

    Task<TradingLedgerStatus> GetStatusAsync(CancellationToken ct = default);

    Task<IReadOnlyList<TradeProposalRecord>> GetProposalsAsync(
        int limit = 100,
        CancellationToken ct = default);

    Task<IReadOnlyList<TradingExecutionRecord>> GetExecutionsAsync(
        int limit = 100,
        CancellationToken ct = default);

    Task<IReadOnlyList<TradingEventRecord>> GetEventsAsync(
        int limit = 200,
        CancellationToken ct = default);

    Task<IReadOnlyList<ReconciliationRunRecord>> GetReconciliationRunsAsync(
        int limit = 100,
        CancellationToken ct = default);

    Task RecordReconciliationAsync(
        BrokerReconciliationSnapshot snapshot,
        CancellationToken ct = default);

    Task<ExecutionClaim> TryBeginExecutionAsync(
        string idempotencyKey,
        string requestJson,
        string policyVersion,
        CancellationToken ct = default);

    Task CompleteExecutionAsync(
        string executionId,
        string state,
        string resultJson,
        CancellationToken ct = default);

    Task AppendEventAsync(
        string executionId,
        string eventType,
        string payloadJson,
        CancellationToken ct = default);

    /// <summary>
    /// Persists one settled trading session's daily bars and records the date as covered — including
    /// when <paramref name="bars"/> is empty, which marks a known non-trading day so the backfill
    /// never asks the portal for it again. Bars are upserted, so re-running a date is idempotent.
    /// </summary>
    Task SaveDailySessionAsync(
        DateOnly sessionDate,
        IReadOnlyList<TradingAgent.Research.PsxCandle> bars,
        CancellationToken ct = default);

    /// <summary>
    /// Archived daily bars for one symbol, oldest first, most recent <paramref name="maxBars"/> kept.
    /// This is the local history that makes weekly levels (and a warm start) possible.
    /// </summary>
    Task<IReadOnlyList<TradingAgent.Research.PsxCandle>> GetDailyBarsAsync(
        string symbol,
        int maxBars,
        CancellationToken ct = default);

    /// <summary>Dates already retrieved from the portal within a range, whether or not they traded.</summary>
    Task<IReadOnlySet<DateOnly>> GetCoveredDailyDatesAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken ct = default);

    /// <summary>
    /// Forgets coverage for dates after <paramref name="settledThrough"/> so they are fetched again.
    ///
    /// <para>
    /// This repairs the one way the archive can go permanently wrong: a session recorded before it
    /// settled. Fetching the current trading day returns either a partial bar (stored, then never
    /// corrected because the date counts as covered) or an empty table that looks identical to a
    /// holiday (recorded as a non-trading day — a permanent hole). Coverage is only a resume marker, so
    /// dropping it for unsettled dates costs one request to redo and cannot lose data: the bars
    /// themselves are upserted.
    /// </para>
    /// </summary>
    Task<int> ClearDailyCoverageAfterAsync(
        DateOnly settledThrough,
        CancellationToken ct = default);

    /// <summary>Row and symbol counts for the daily archive, for status reporting.</summary>
    Task<DailyArchiveStatus> GetDailyArchiveStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists completed intraday bars. PSX serves intraday data for the CURRENT session only, so
    /// this archive is the only way multi-session intraday history can exist — it accrues from the day
    /// archiving is switched on. Bars are upserted on (symbol, interval, bucket start), making a
    /// repeated save of the same session idempotent.
    /// </summary>
    Task SaveIntradayBarsAsync(
        IReadOnlyList<TradingAgent.Research.PsxCandle> bars,
        CancellationToken ct = default);

    /// <summary>
    /// Reads the most recent archived intraday bars for one symbol and bar width, returned oldest
    /// first. <paramref name="beforeUtc"/> excludes the live session, which callers rebuild from ticks.
    /// </summary>
    Task<IReadOnlyList<TradingAgent.Research.PsxCandle>> GetIntradayBarsAsync(
        string symbol,
        int intervalMinutes,
        int maxBars,
        DateTime? beforeUtc = null,
        CancellationToken ct = default);

    // ── Watchlist ─────────────────────────────────────────────────────────────
    // The user's monitoring universe. Independent of AllowedSymbols after seeding: what may be
    // WATCHED is editable here, what may be TRADED stays in configuration.

    /// <summary>Watchlist entries in display order, plus when it was seeded and from what.</summary>
    Task<WatchlistSnapshot> GetWatchlistAsync(CancellationToken ct = default);

    /// <summary>
    /// Seeds the watchlist from <paramref name="seed"/> the first time only, and records the hash of
    /// that seed. Later calls are no-ops even when the configured list has changed — re-seeding would
    /// discard the user's edits, so drift is reported instead (see <see cref="WatchlistSnapshot"/>).
    /// Returns true when this call performed the seeding.
    /// </summary>
    Task<bool> EnsureWatchlistSeededAsync(
        IReadOnlyList<string> seed,
        string seedHash,
        CancellationToken ct = default);

    /// <summary>Adds a symbol. False when it is already present.</summary>
    Task<bool> AddWatchlistSymbolAsync(
        string symbol,
        string source,
        CancellationToken ct = default);

    /// <summary>Removes a symbol. False when it was not present. Archived bars are kept.</summary>
    Task<bool> RemoveWatchlistSymbolAsync(string symbol, CancellationToken ct = default);

    /// <summary>Updates the per-symbol fields the user controls. False when the symbol is unknown.</summary>
    Task<bool> UpdateWatchlistSymbolAsync(
        string symbol,
        bool? alertsEnabled,
        string? notes,
        CancellationToken ct = default);

    /// <summary>
    /// Clears the watchlist and reseeds it from <paramref name="seed"/>, re-stamping the seed hash.
    /// This is the explicit "reset to the configured allowed list" action; it discards user edits by
    /// design. Returns the number of symbols after reseeding.
    /// </summary>
    Task<int> ResetWatchlistAsync(
        IReadOnlyList<string> seed,
        string seedHash,
        CancellationToken ct = default);

    /// <summary>Per-symbol archived-bar counts, for reporting how much history a symbol has.</summary>
    Task<IReadOnlyDictionary<string, int>> GetDailyBarCountsAsync(
        IReadOnlyList<string> symbols,
        CancellationToken ct = default);
}

/// <summary>One watched symbol.</summary>
public sealed record WatchlistEntry(
    string Symbol,
    DateTime AddedUtc,
    string Source,
    int SortOrder,
    bool AlertsEnabled,
    string? Notes);

/// <summary>
/// The watchlist plus its seeding provenance. <see cref="SeedHash"/> is the hash of the
/// AllowedSymbols it was seeded from; comparing it with the current configuration is how the UI can
/// say "the configured list has changed since seeding" without anything re-seeding automatically.
/// </summary>
public sealed record WatchlistSnapshot(
    IReadOnlyList<WatchlistEntry> Entries,
    DateTime? SeededUtc,
    string? SeedHash);

/// <summary>
/// Size and reach of the archived daily-candle history. <see cref="CoveredDates"/> counts dates
/// retrieved from the portal (including non-trading days), which is what makes backfill progress
/// measurable.
/// </summary>
public sealed record DailyArchiveStatus(
    int Symbols,
    int Bars,
    int CoveredDates,
    DateOnly? EarliestSession,
    DateOnly? LatestSession);

public sealed record TradingLedgerStatus(
    int PendingProposals,
    int SubmittingExecutions,
    int UnknownExecutions,
    int AcceptedExecutions,
    DateTime CheckedUtc);

public sealed record TradeProposalRecord(
    string ProposalId,
    string Status,
    JsonElement Proposal,
    string PolicyVersion,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record TradingExecutionRecord(
    string ExecutionId,
    string State,
    JsonElement Request,
    JsonElement? Result,
    string PolicyVersion,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record TradingEventRecord(
    long EventId,
    string ExecutionId,
    string EventType,
    JsonElement Payload,
    DateTime CreatedUtc);

public sealed record ReconciliationRunRecord(
    string ReconciliationId,
    string State,
    JsonElement Details,
    DateTime StartedUtc,
    DateTime? CompletedUtc);
