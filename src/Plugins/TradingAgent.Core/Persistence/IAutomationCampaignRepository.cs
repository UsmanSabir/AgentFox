namespace TradingAgent.Persistence;

/// <summary>
/// Durable, edition-neutral state for a symbol automation campaign. Core stores and exposes the
/// lifecycle; the plugin that owns <see cref="ProfileId"/> decides what each state means.
/// </summary>
public sealed record AutomationCampaignRecord(
    string CampaignId,
    string Symbol,
    string ProfileId,
    string ProfileJson,
    string State,
    string Origin,
    decimal? PlannedBudgetPkr,
    decimal DeployedPkr,
    int MaxLegs,
    int CompletedLegs,
    int Quantity,
    decimal? AveragePrice,
    decimal? LastFillPrice,
    decimal? CurrentStop,
    decimal? HighWaterPrice,
    decimal? NextAddPrice,
    string? StatusMessage,
    DateTime StartedUtc,
    DateTime UpdatedUtc,
    DateTime? ClosedUtc,
    long Version);

public sealed record AutomationCampaignEventRecord(
    long Sequence,
    string CampaignId,
    string Symbol,
    string Kind,
    string Message,
    string? DetailJson,
    DateTime Utc);

public sealed record AutomationStrategyAssignmentRecord(
    string Symbol,
    string ProfileId,
    string? OverridesJson,
    DateTime UpdatedUtc);

/// <summary>
/// How one finished automation campaign actually turned out.
///
/// <para>
/// <b>Why core owns this.</b> An outcome is a property of a campaign, and campaigns already live
/// here as an edition-neutral lifecycle. Splitting the two across separate databases would put a
/// join across a process boundary for no gain. Core still learns no strategy rules: every field is
/// either an opaque plugin-owned id, a plain number, or a free-text reason it never interprets.
/// </para>
///
/// <para>
/// <b>Rows expire; <see cref="AutomationOutcomeDailyRecord"/> does not.</b> Raw rows answer "what
/// happened on this trade" and are pruned by age and count. The daily rollup answers "is this
/// working", is written as each outcome lands rather than derived later, and therefore survives the
/// pruning of the rows it came from. See <see cref="IAutomationCampaignRepository.PruneAutomationOutcomesAsync"/>.
/// </para>
/// </summary>
/// <param name="Mode">
/// The automation mode that opened the campaign. Load-bearing: results produced by modes that
/// submit nothing must never be averaged with results that reached a broker.
/// </param>
/// <param name="Simulated">
/// The fills were modelled rather than observed. True for any mode that cannot reach a broker, and
/// the reason a caller must never present these as realised.
/// </param>
/// <param name="InitialRiskPerShare">
/// Entry minus stop, pinned when the campaign opened. The denominator of
/// <paramref name="RealisedR"/>; null when the campaign had no recorded plan, which is why adopted
/// holdings have no R.
/// </param>
/// <param name="RealisedR">
/// Net result in units of initial risk. Null when it could not be computed, which is not zero.
/// </param>
/// <param name="CloseReason">Free text from the plugin. Core stores it and reads nothing into it.</param>
/// <param name="RegimeAtEntry">Market-wide context when the campaign opened, or null if not recorded.</param>
public sealed record AutomationOutcomeRecord(
    string CampaignId,
    string Symbol,
    string ProfileId,
    string? EntryStrategyId,
    string? ExitPlanId,
    string Mode,
    bool Simulated,
    DateTime OpenedUtc,
    DateTime ClosedUtc,
    int SessionsHeld,
    decimal? PlannedEntry,
    decimal? PlannedStop,
    decimal? PlannedTarget,
    decimal? InitialRiskPerShare,
    int Quantity,
    decimal DeployedPkr,
    decimal? AverageCost,
    decimal? RealisedNetPkr,
    decimal? RealisedR,
    string CloseReason,
    string? RegimeAtEntry,
    DateTime RecordedUtc);

/// <summary>
/// One day's results for one plan in one mode. Small by construction — a handful of rows per trading
/// day — so it can be kept far longer than the outcomes it summarises.
/// </summary>
/// <param name="Wins">Outcomes with a positive net result. Excludes those where it was unknown.</param>
/// <param name="Measured">
/// Outcomes that produced a usable <see cref="AutomationOutcomeRecord.RealisedR"/>. The honest
/// denominator for an average: <paramref name="Trades"/> counts campaigns that closed, and some of
/// them close without a computable result.
/// </param>
public sealed record AutomationOutcomeDailyRecord(
    string Day,
    string ProfileId,
    string Mode,
    int Trades,
    int Wins,
    int Losses,
    int Measured,
    decimal SumR,
    decimal SumNetPkr,
    DateTime UpdatedUtc);

/// <summary>
/// How often one gate refused a candidate on one day. The counting unit is the plugin's stable gate
/// code, never its message: a message carries the candidate's own numbers and so never repeats.
/// </summary>
public sealed record AutomationGateRejectionRecord(
    string Day,
    string StrategyId,
    string GateCode,
    int Count,
    DateTime UpdatedUtc);

/// <summary>
/// A narrow persistence seam for strategy plugins. It deliberately stores opaque profile JSON and
/// plain lifecycle fields, so core never learns premium strategy rules.
/// </summary>
public interface IAutomationCampaignRepository
{
    Task<IReadOnlyList<AutomationCampaignRecord>> GetAutomationCampaignsAsync(
        bool openOnly = true, CancellationToken ct = default);

    Task<AutomationCampaignRecord?> GetAutomationCampaignAsync(
        string symbol, bool openOnly = true, CancellationToken ct = default);

    Task<bool> SaveAutomationCampaignAsync(
        AutomationCampaignRecord campaign,
        long? expectedVersion = null,
        CancellationToken ct = default);

    Task AppendAutomationCampaignEventAsync(
        AutomationCampaignEventRecord item,
        CancellationToken ct = default);

    Task<IReadOnlyList<AutomationCampaignEventRecord>> GetAutomationCampaignEventsAsync(
        string? symbol = null,
        string? campaignId = null,
        int limit = 100,
        CancellationToken ct = default);

    Task<IReadOnlyList<AutomationStrategyAssignmentRecord>> GetAutomationStrategyAssignmentsAsync(
        CancellationToken ct = default);

    Task SaveAutomationStrategyAssignmentAsync(
        AutomationStrategyAssignmentRecord assignment,
        CancellationToken ct = default);

    Task DeleteAutomationStrategyAssignmentAsync(string symbol, CancellationToken ct = default);

    /// <summary>
    /// Reads one opaque plugin-owned state blob, or null when it was never written.
    ///
    /// <para>
    /// Core stores the string and interprets nothing — the same contract as <c>ProfileJson</c>. It
    /// exists so a strategy plugin can survive a restart without inventing its own file or table, and
    /// so operational state lands in the same database as the orders it governs, and is therefore
    /// backed up and copied as one thing.
    /// </para>
    /// </summary>
    Task<string?> GetAutomationStateAsync(string key, CancellationToken ct = default);

    Task SaveAutomationStateAsync(string key, string valueJson, CancellationToken ct = default);

    // ── Outcomes ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Records how a campaign turned out, and folds it into the day's rollup in the same
    /// transaction. Idempotent on <see cref="AutomationOutcomeRecord.CampaignId"/>: a campaign
    /// re-observed as closed after a restart must not be counted twice, which is why the rollup is
    /// updated here rather than by the caller.
    /// </summary>
    /// <returns>True when a new outcome was recorded; false when this campaign was already recorded.</returns>
    Task<bool> SaveAutomationOutcomeAsync(
        AutomationOutcomeRecord outcome, CancellationToken ct = default);

    Task<IReadOnlyList<AutomationOutcomeRecord>> GetAutomationOutcomesAsync(
        string? symbol = null, string? profileId = null, int limit = 100,
        CancellationToken ct = default);

    /// <summary>Rollups from <paramref name="sinceDay"/> (inclusive, <c>yyyy-MM-dd</c>) onward.</summary>
    Task<IReadOnlyList<AutomationOutcomeDailyRecord>> GetAutomationOutcomeDailyAsync(
        string? sinceDay = null, CancellationToken ct = default);

    /// <summary>Adds to one day's count for a gate. Called once per pass with the pass's totals.</summary>
    Task AddAutomationGateRejectionsAsync(
        string day,
        string strategyId,
        IReadOnlyDictionary<string, int> countsByGateCode,
        CancellationToken ct = default);

    Task<IReadOnlyList<AutomationGateRejectionRecord>> GetAutomationGateRejectionsAsync(
        string? sinceDay = null, string? strategyId = null, CancellationToken ct = default);

    /// <summary>
    /// Enforces retention across all three outcome tables. Every argument is a hard limit rather than
    /// a hint, and the call is safe to repeat.
    ///
    /// <para>
    /// Raw outcomes are bounded by <b>both</b> age and row count, whichever binds first, so a busy
    /// period cannot outgrow the budget between sweeps. Rollups and gate counts are bounded by age
    /// alone because their row counts are structurally small.
    /// </para>
    /// </summary>
    /// <returns>How many rows were deleted from each table, for the caller to log.</returns>
    Task<(int Outcomes, int Daily, int GateRejections)> PruneAutomationOutcomesAsync(
        int outcomeRetentionDays,
        int outcomeMaxRows,
        int dailyRetentionDays,
        int gateRejectionRetentionDays,
        CancellationToken ct = default);
}
