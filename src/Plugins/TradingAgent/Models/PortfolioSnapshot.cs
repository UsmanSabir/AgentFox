namespace TradingAgent.Models;

/// <summary>
/// One holding row read from the broker's portfolio grid. Monetary values are PKR.
/// Nullable fields are ones the grid did not expose (or that could not be parsed) —
/// consumers must treat null as "unknown", never as zero.
/// </summary>
public sealed record HoldingPosition
{
    public string Symbol { get; init; } = "";
    public decimal? Quantity { get; init; }
    public decimal? AverageBuyPrice { get; init; }
    /// <summary>Total cost basis (avg buy price × qty) as reported — or derived when absent.</summary>
    public decimal? InvestmentValue { get; init; }
    public decimal? CurrentPrice { get; init; }
    /// <summary>Market value (current price × qty) as reported — or derived when absent.</summary>
    public decimal? CurrentValue { get; init; }
    public decimal? ProfitLoss { get; init; }
    public decimal? ProfitLossPercent { get; init; }
}

/// <summary>
/// Point-in-time view of the broker account: available cash plus all holdings. Warnings carry
/// non-fatal extraction problems (e.g. balance label not found) so the agent can report exactly
/// what is and is not known instead of guessing.
/// </summary>
public sealed record PortfolioSnapshot
{
    public decimal? AvailableBalancePkr { get; init; }
    /// <summary>Raw text of the balance label the value was read from, for auditability.</summary>
    public string? BalanceSource { get; init; }
    public IReadOnlyList<HoldingPosition> Holdings { get; init; } = [];
    public decimal? TotalInvestment { get; init; }
    public decimal? TotalCurrentValue { get; init; }
    public DateTime RetrievedAtUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
