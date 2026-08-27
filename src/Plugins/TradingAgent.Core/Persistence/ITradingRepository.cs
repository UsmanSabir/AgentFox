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

    // ── Proposal lifecycle ────────────────────────────────────────────────────
    // A proposal used to be write-only: created, listed, never resolved, so the table only grew and
    // "pending proposals" only climbed. These give it the states that make it a work queue — the
    // WhatsApp signal that arrived overnight can now be executed, rejected, or aged out.

    /// <summary>One proposal by id, or null.</summary>
    Task<TradeProposalRecord?> GetProposalAsync(string proposalId, CancellationToken ct = default);

    /// <summary>
    /// Moves a proposal from <paramref name="expectedStatus"/> to <paramref name="newStatus"/>, and
    /// returns false when it was not in the expected state.
    ///
    /// <para>
    /// Compare-and-set rather than a blind update, because that is what makes a double click safe: the
    /// second request finds the row already moved on and declines instead of executing the same
    /// proposal twice.
    /// </para>
    /// </summary>
    Task<bool> TrySetProposalStateAsync(
        string proposalId,
        string expectedStatus,
        string newStatus,
        string? reason = null,
        string? executionId = null,
        CancellationToken ct = default);

    /// <summary>Proposals not yet in a terminal state — the actionable queue.</summary>
    Task<IReadOnlyList<TradeProposalRecord>> GetOpenProposalsAsync(CancellationToken ct = default);

    /// <summary>Deletes TERMINAL proposals older than <paramref name="before"/>; open ones are kept.</summary>
    Task<int> PruneProposalsAsync(DateTime before, CancellationToken ct = default);

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

    /// <summary>
    /// Manually closes an execution whose broker outcome is unknown, after the operator has checked
    /// the broker's own activity/order book. The compare-and-set prevents a stale or repeated click
    /// from rewriting an already resolved execution; the audit event is committed atomically with
    /// the state change.
    /// </summary>
    Task<bool> ResolveUnknownExecutionAsync(
        string executionId,
        string resolution,
        string auditPayloadJson,
        CancellationToken ct = default);

    /// <summary>
    /// Records one row per broker order attempt, keyed by the EXCHANGE's order number when there is one.
    ///
    /// <para>
    /// Before this existed the order number lived only inside <c>trading_executions.result_json</c>, so
    /// "what happened to order 0010TJZJC700RH12" could be answered by grep and by nothing else — and the
    /// <c>broker_orders</c> table designed for exactly this had been created and never written. Every
    /// attempt gets a row, including failures: an attempt whose outcome is unknown is the most important
    /// one to have recorded, and it is the one a JSON blob search is least likely to surface.
    /// </para>
    /// </summary>
    Task RecordBrokerOrdersAsync(
        string executionId,
        IReadOnlyList<TradingAgent.Models.OrderResult> orders,
        CancellationToken ct = default);

    /// <summary>
    /// Records fills the broker reported, idempotently — reconciliation re-reads the same activity log
    /// every minute, so the same fill arrives repeatedly and must not accumulate duplicate rows.
    /// </summary>
    Task<int> RecordFillsAsync(
        IReadOnlyList<TradingAgent.Reconciliation.BrokerFill> fills,
        CancellationToken ct = default);

    /// <summary>
    /// Recorded fills for one symbol from <paramref name="sinceUtc"/> onward, newest last.
    ///
    /// <para>
    /// <b>Why this needs a join.</b> The <c>fills</c> table carries only quantity, price and time —
    /// the symbol and the side live on the parent <c>broker_orders</c> row, inside its JSON. That
    /// JSON is one of two shapes: an <c>OrderResult</c> for an order this system placed (side under
    /// <c>Action</c>) or a <c>BrokerFill</c> for one reconciliation observed (side under
    /// <c>Side</c>). Both carry <c>Symbol</c>, and the query reads whichever side field is present.
    /// </para>
    ///
    /// <para>
    /// This is the only honest source of what a position actually sold for. Custody reaching zero
    /// says the shares are gone; it never says at what price, and inferring one from a resting stop
    /// or the day's close would produce an authoritative-looking number that is not what happened.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<RecordedFill>> GetFillsForSymbolAsync(
        string symbol,
        DateTime sinceUtc,
        CancellationToken ct = default);

    // ── Persistent DAY orders ────────────────────────────────────────────────

    Task<string> SavePersistentOrderAsync(
        TradingAgent.Trading.PersistentOrderIntent intent,
        CancellationToken ct = default);

    Task<TradingAgent.Trading.PersistentOrderIntent?> GetPersistentOrderAsync(
        string intentId,
        CancellationToken ct = default);

    Task<IReadOnlyList<TradingAgent.Trading.PersistentOrderIntent>> GetPersistentOrdersAsync(
        bool openOnly = true,
        CancellationToken ct = default);

    Task<IReadOnlyList<TradingAgent.Trading.PersistentOrderPlacement>> GetPersistentOrderPlacementsAsync(
        string intentId,
        CancellationToken ct = default);

    /// <summary>Claims the one allowed placement for a trading date.</summary>
    Task<TradingAgent.Trading.PersistentOrderAttemptClaim> TryClaimPersistentOrderAttemptAsync(
        string intentId,
        DateOnly sessionDate,
        CancellationToken ct = default);

    /// <summary>
    /// Claims an additional same-day attempt only when the latest recorded attempt is definitively
    /// failed. This is reserved for an explicit operator retry after a fresh broker-book check.
    /// </summary>
    Task<TradingAgent.Trading.PersistentOrderAttemptClaim> TryClaimPersistentOrderRetryAsync(
        string intentId,
        DateOnly sessionDate,
        CancellationToken ct = default);

    Task RecordPersistentOrderPlacementAsync(
        TradingAgent.Trading.PersistentOrderPlacement placement,
        string intentState,
        string? stateReason,
        CancellationToken ct = default);

    Task<bool> SetPersistentOrderPlacementStateAsync(
        string placementId,
        string state,
        string? message = null,
        CancellationToken ct = default);

    Task<bool> SetPersistentOrderProgressAsync(
        string intentId,
        int filledQuantity,
        string state,
        string? reason,
        CancellationToken ct = default);

    Task<bool> TrySetPersistentOrderStateAsync(
        string intentId,
        IReadOnlyCollection<string> expectedStates,
        string newState,
        string? reason = null,
        CancellationToken ct = default);

    /// <summary>
    /// The account's custody position for <paramref name="symbol"/> as of the most recent HEALTHY
    /// reconciliation run strictly before <paramref name="beforeUtc"/> — 0 if that snapshot held none,
    /// null if no healthy run exists that far back at all (no evidence to reason from). Used to settle
    /// a persistent order's "attention" state left over from a prior trading date: the broker's
    /// activity log only ever covers today, but a persisted reconciliation snapshot does not expire.
    /// </summary>
    Task<decimal?> FindHoldingQuantityBeforeAsync(
        string symbol,
        DateTime beforeUtc,
        CancellationToken ct = default);

    Task AppendEventAsync(
        string executionId,
        string eventType,
        string payloadJson,
        CancellationToken ct = default);

    /// <summary>
    /// Persists one settled trading session's daily bars and records the date as covered for
    /// <paramref name="requestedSymbols"/>. Bars are upserted, so re-running a date is idempotent.
    ///
    /// <para>
    /// Coverage is recorded per (date, symbol) rather than per date because a session fetch returns the
    /// whole market at once and is then filtered to the archive universe. With a date-only marker, a
    /// symbol added to that universe afterwards could never be filled in: the date already counted as
    /// covered, so the backfill skipped it forever and the symbol stayed permanently short of the
    /// history weekly levels need. <paramref name="requestedSymbols"/> is the universe the fetch was
    /// filtered against — the symbols we can honestly claim to have looked for. A symbol absent from it
    /// was never requested; a requested symbol with no bar simply did not trade, and is still covered.
    /// </para>
    ///
    /// <para>
    /// Use <see cref="SaveNonTradingDayAsync"/> for a date the market was closed. Passing an empty
    /// <paramref name="bars"/> here records a session that traded but held nothing we asked for, which
    /// is a different fact and must not silence the whole date.
    /// </para>
    /// </summary>
    Task SaveDailySessionAsync(
        DateOnly sessionDate,
        IReadOnlyList<TradingAgent.Research.PsxCandle> bars,
        IReadOnlyCollection<string> requestedSymbols,
        CancellationToken ct = default);

    /// <summary>
    /// Records a date the market did not trade at all, which is covered for every symbol — now and for
    /// any symbol added later — so the backfill never asks the portal for it again.
    /// </summary>
    Task SaveNonTradingDayAsync(DateOnly sessionDate, CancellationToken ct = default);

    /// <summary>
    /// Archived daily bars for one symbol, oldest first, most recent <paramref name="maxBars"/> kept.
    /// This is the local history that makes weekly levels (and a warm start) possible.
    /// </summary>
    Task<IReadOnlyList<TradingAgent.Research.PsxCandle>> GetDailyBarsAsync(
        string symbol,
        int maxBars,
        CancellationToken ct = default);

    /// <summary>
    /// Dates in the range already retrieved for <em>every</em> symbol in <paramref name="symbols"/> —
    /// the dates a backfill for that set can skip. A non-trading day counts for any symbol; a trading
    /// day counts only once each symbol has been requested for it. An empty
    /// <paramref name="symbols"/> falls back to "any date on record".
    /// </summary>
    Task<IReadOnlySet<DateOnly>> GetCoveredDailyDatesAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        IReadOnlyCollection<string> symbols,
        CancellationToken ct = default);

    /// <summary>
    /// Per-symbol count of dates in the range already retrieved for that symbol, so a caller holding
    /// the trading calendar can report how many sessions each symbol is still short of. Non-trading days
    /// count toward every symbol; a symbol with no coverage of its own at all is absent rather than
    /// present with zero.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetCoveredDailyDateCountsAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        IReadOnlyCollection<string> symbols,
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
    /// themselves are upserted. Per-symbol coverage for those dates is dropped with it, or the date
    /// would be refetched while still counting as covered for the symbols recorded prematurely.
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

    /// <summary>
    /// Adds an index universe to the watchlist, or replaces the watchlist with it. Existing rows that
    /// remain in the target keep their alerts, pin, notes and manual-only setting; only genuinely new
    /// rows receive defaults. This changes execution eligibility only when Watchlist is the explicitly
    /// configured execution source.
    /// </summary>
    Task<WatchlistBulkApplyResult> ApplyWatchlistSymbolsAsync(
        IReadOnlyList<string> symbols,
        bool replace,
        string source,
        CancellationToken ct = default);

    /// <summary>Removes a symbol. False when it was not present. Archived bars are kept.</summary>
    Task<bool> RemoveWatchlistSymbolAsync(string symbol, CancellationToken ct = default);

    /// <summary>Updates the per-symbol fields the user controls. False when the symbol is unknown.</summary>
    /// <param name="autoTradeEnabled">
    /// False makes the symbol manual-only: automation may no longer originate an order for it, while
    /// the operator still can. Editable at runtime because it only ever NARROWS what automation may
    /// do. It is independent of which universe supplies base execution eligibility.
    /// </param>
    Task<bool> UpdateWatchlistSymbolAsync(
        string symbol,
        bool? alertsEnabled,
        string? notes,
        bool? pinned = null,
        bool? autoTradeEnabled = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sets the runtime automation preference for every watched symbol in one transaction. Returns
    /// the number of rows affected. Configured <c>ManualOnlySymbols</c> remain an independent floor
    /// and cannot be lifted by setting this value to true.
    /// </summary>
    Task<int> SetWatchlistAutoTradeEnabledAsync(
        bool autoTradeEnabled,
        CancellationToken ct = default);

    /// <summary>Persists the complete display order after a drag operation.</summary>
    Task<bool> ReorderWatchlistAsync(
        IReadOnlyList<string> symbols,
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

    // ── Monitor state and alerts ──────────────────────────────────────────────

    /// <summary>Per-symbol monitor state from the previous pass, keyed by symbol.</summary>
    Task<IReadOnlyDictionary<string, TradingAgent.Watchlist.SymbolMonitorState>> GetMonitorStatesAsync(
        CancellationToken ct = default);

    /// <summary>Upserts the state produced by a pass.</summary>
    Task SaveMonitorStateAsync(
        TradingAgent.Watchlist.SymbolMonitorState state,
        CancellationToken ct = default);

    /// <summary>Persists a raised alert and returns its id.</summary>
    Task<string> SaveAlertAsync(
        TradingAgent.Watchlist.DetectedAlert alert,
        DateOnly sessionDate,
        CancellationToken ct = default);

    /// <summary>
    /// True when an equivalent alert was already raised since <paramref name="since"/> — same symbol,
    /// same kind, and the same level (rounded, so a level that shifts by a paisa is still the same
    /// level). This is the cooldown that stops one situation being reported repeatedly.
    /// </summary>
    Task<bool> HasRecentAlertAsync(
        string symbol,
        TradingAgent.Watchlist.AlertKind kind,
        decimal? levelPrice,
        DateTime since,
        CancellationToken ct = default);

    /// <summary>Alerts newest first, optionally filtered by symbol and state.</summary>
    Task<IReadOnlyList<TradingAgent.Watchlist.AlertRecord>> GetAlertsAsync(
        string? symbol = null,
        string? state = null,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>One alert by id, or null. Indexed lookup rather than scanning the recent list.</summary>
    Task<TradingAgent.Watchlist.AlertRecord?> GetAlertAsync(
        string alertId,
        CancellationToken ct = default);

    /// <summary>Moves an alert to <paramref name="state"/> (acknowledged/dismissed). False if unknown.</summary>
    Task<bool> SetAlertStateAsync(
        string alertId,
        string state,
        CancellationToken ct = default);

    /// <summary>
    /// Moves selected alerts, or every matching alert when <paramref name="alertIds"/> is null, to a
    /// new state in one transaction. <paramref name="fromState"/> protects already-resolved rows from
    /// a bulk "mark read" action.
    /// </summary>
    Task<int> SetAlertsStateAsync(
        IReadOnlyCollection<string>? alertIds,
        string state,
        string? fromState = null,
        CancellationToken ct = default);

    /// <summary>Count of alerts in the <c>new</c> state, per symbol, for the watchlist badges.</summary>
    Task<IReadOnlyDictionary<string, int>> GetOpenAlertCountsAsync(CancellationToken ct = default);

    /// <summary>Deletes alerts raised before <paramref name="before"/>. Returns how many were removed.</summary>
    Task<int> PruneAlertsAsync(DateTime before, CancellationToken ct = default);

    // ── Armed orders ──────────────────────────────────────────────────────────
    // Orders waiting on a price level or an alert event. Durable, so an armed trigger survives a
    // restart — a stop that forgets itself when the process bounces is not a stop.

    Task<string> SaveArmedOrderAsync(
        TradingAgent.Watchlist.ArmedOrder order,
        CancellationToken ct = default);

    /// <summary>Armed orders; <paramref name="armedOnly"/> false includes fired/cancelled history.</summary>
    Task<IReadOnlyList<TradingAgent.Watchlist.ArmedOrder>> GetArmedOrdersAsync(
        bool armedOnly = true,
        CancellationToken ct = default);

    /// <summary>
    /// Compare-and-set on the current state, so a trigger seen by two overlapping passes cannot fire
    /// twice. False when it was not in the expected state.
    /// </summary>
    Task<bool> TrySetArmedOrderStateAsync(
        string armedId,
        string expectedState,
        string newState,
        string? reason = null,
        string? executionId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Ratchets a trailing percent trigger's reference price, and the level it projects to, in the
    /// favourable direction only.
    ///
    /// <para>
    /// <paramref name="ratchetUp"/> says which direction that is: up for a drop trigger (a trailing
    /// stop follows the high), down for a rise trigger. The comparison is applied in the UPDATE
    /// itself, so a pass carrying a staler price cannot loosen a trail another pass already
    /// tightened. False means nothing was written — normally because the reference had already moved
    /// further, which is not an error.
    /// </para>
    /// </summary>
    Task<bool> TrySetArmedOrderTrailAsync(
        string armedId,
        decimal reference,
        decimal triggerPrice,
        bool ratchetUp,
        CancellationToken ct = default);

    /// <summary>Raises an armed backstop's quantity as additional entry fills are confirmed.</summary>
    Task<bool> TrySetArmedOrderQuantityAsync(
        string armedId,
        int quantity,
        CancellationToken ct = default);

    // ── Protective stops ──────────────────────────────────────────────────────
    // A standing intent to keep a position protected at a level. Durable and re-materialised as a
    // native day order each session, because this venue clears outstanding orders at the close — a
    // one-shot child order would protect the position for one day and then silently stop.

    Task<string> SaveProtectiveStopAsync(
        TradingAgent.Watchlist.ProtectiveStop stop,
        CancellationToken ct = default);

    /// <summary>Stops not yet closed; <paramref name="openOnly"/> false includes the history.</summary>
    Task<IReadOnlyList<TradingAgent.Watchlist.ProtectiveStop>> GetProtectiveStopsAsync(
        bool openOnly = true,
        CancellationToken ct = default);

    /// <summary>Compare-and-set on the current state. False when it was not in the expected one.</summary>
    Task<bool> TrySetProtectiveStopStateAsync(
        string stopId,
        string expectedState,
        string newState,
        string? reason = null,
        CancellationToken ct = default);

    /// <summary>
    /// Promotes a stop to <c>active</c> against a confirmed holdings increase, raising the protected
    /// quantity. Never lowers it: a later, smaller delta must not shrink protection a bigger fill
    /// already established.
    /// </summary>
    Task<bool> RecordProtectiveStopFillAsync(
        string stopId,
        int confirmedQuantity,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Records a native placement. Coverage accumulates within a session and resets when the session
    /// rolls, mirroring the venue clearing resting orders overnight.
    /// </summary>
    Task<bool> RecordProtectiveStopPlacementAsync(
        string stopId,
        DateOnly sessionDate,
        int placedQuantity,
        string? orderNo,
        CancellationToken ct = default);

    /// <summary>
    /// Refreshes the pre-entry holding a fill will be measured against. Confined to
    /// <c>pending_fill</c>, because overwriting it after the entry has gone in would erase the very
    /// number that proves a fill happened.
    /// </summary>
    Task<bool> RecordProtectiveStopBaselineAsync(
        string stopId,
        int baselineQuantity,
        CancellationToken ct = default);

    /// <summary>Links (or unlinks) the locally-armed SELL that covers the gaps the native stop cannot.</summary>
    Task<bool> SetProtectiveStopBackstopAsync(
        string stopId,
        string? backstopArmedId,
        CancellationToken ct = default);
}

/// <summary>One watched symbol.</summary>
/// <param name="AlertsEnabled">
/// False MUTES the symbol: it is still analyzed and its state still advances, nothing is raised.
/// </param>
/// <param name="AutoTradeEnabled">
/// False makes the symbol MANUAL-ONLY: no automation may originate an order for it, entry or exit,
/// while the operator still can. Orthogonal to <paramref name="AlertsEnabled"/> — a manual-only symbol
/// normally wants its alerts louder, not quieter, since you are the one acting on them.
/// Defaults to true, so an existing database keeps behaving exactly as it did.
/// </param>
public sealed record WatchlistEntry(
    string Symbol,
    DateTime AddedUtc,
    string Source,
    int SortOrder,
    bool Pinned,
    bool AlertsEnabled,
    string? Notes,
    bool AutoTradeEnabled = true);

/// <summary>
/// The watchlist plus its seeding provenance. <see cref="SeedHash"/> is the hash of the
/// AllowedSymbols it was seeded from; comparing it with the current configuration is how the UI can
/// say "the configured list has changed since seeding" without anything re-seeding automatically.
/// </summary>
public sealed record WatchlistSnapshot(
    IReadOnlyList<WatchlistEntry> Entries,
    DateTime? SeededUtc,
    string? SeedHash);

/// <summary>Result of an additive or replacing watchlist preset operation.</summary>
public sealed record WatchlistBulkApplyResult(
    int Total,
    int Added,
    int Removed,
    int Preserved);

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

/// <summary>
/// A persisted proposal. <see cref="Status"/> moves
/// <c>proposed → executing → executed | rejected | expired</c>; the terminal states are why this is a
/// work queue rather than the write-only log it used to be.
/// </summary>
public sealed record TradeProposalRecord(
    string ProposalId,
    string Status,
    JsonElement Proposal,
    string PolicyVersion,
    DateTime CreatedUtc,
    DateTime UpdatedUtc)
{
    /// <summary>Execution this proposal became, once executed.</summary>
    public string? ExecutionId { get; init; }

    /// <summary>Why it was rejected or expired — recorded so a terminal state is explicable.</summary>
    public string? StateReason { get; init; }
}

/// <summary>
/// One fill as it was actually recorded, with the symbol and side recovered from its parent order.
/// </summary>
/// <param name="Side">
/// <c>BUY</c> or <c>SELL</c> as the broker or the placement reported it, upper-cased. Null when the
/// parent row carried neither field — a fill that cannot be attributed to a side is reported rather
/// than guessed, because guessing turns a purchase into a sale.
/// </param>
public sealed record RecordedFill(
    string Symbol,
    string? Side,
    int Quantity,
    decimal Price,
    DateTime FilledUtc);

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
