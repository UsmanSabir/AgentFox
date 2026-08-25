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
}
