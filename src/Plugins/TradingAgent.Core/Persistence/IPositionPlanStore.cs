using TradingAgent.Watchlist;

namespace TradingAgent.Persistence;

/// <summary>
/// Where position plans live (plan step 9). A separate interface from <see cref="ITradingRepository"/>, as
/// <see cref="IProtectiveStopOrderLedger"/> is, so every other repository implementation keeps compiling and readers
/// simply find no plans.
///
/// <para><b>Retention (§0.1):</b> one row per symbol, replaced on save and removed by <see cref="DeletePositionPlanAsync"/>;
/// it does not grow with time.</para>
/// </summary>
public interface IPositionPlanStore
{
    Task<IReadOnlyList<PositionPlan>> GetPositionPlansAsync(CancellationToken ct = default);

    Task<PositionPlan?> GetPositionPlanAsync(string symbol, CancellationToken ct = default);

    /// <summary>
    /// Saves the plan if the stored version still equals <paramref name="expectedVersion"/> (0 = there is none yet).
    /// Returns the saved plan with its new version, or null when another write came first.
    /// </summary>
    Task<PositionPlan?> TrySavePositionPlanAsync(PositionPlan plan, int expectedVersion, CancellationToken ct = default);

    Task<bool> DeletePositionPlanAsync(string symbol, CancellationToken ct = default);
}
