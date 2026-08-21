namespace TradingAgent.Models;

/// <summary>
/// Broker-neutral account view for dashboard and API consumers. A provider maps its native payload
/// onto these common fields and may retain extra labelled values in <c>Attributes</c>; adding another
/// broker must not require changing the dashboard contract.
/// </summary>
public sealed record BrokerAccountSnapshot
{
    public string BrokerId { get; init; } = "";
    public string BrokerName { get; init; } = "";
    public string? AccountLabel { get; init; }
    public bool BalancesAvailable { get; init; }
    public bool HoldingsAvailable { get; init; }
    public bool OrdersAvailable { get; init; }
    public IReadOnlyList<BrokerAccountBalance> Balances { get; init; } = [];
    public IReadOnlyList<BrokerAccountHolding> Holdings { get; init; } = [];
    public IReadOnlyList<BrokerAccountOrder> Orders { get; init; } = [];
    public DateTime RetrievedAtUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyDictionary<string, string?> Attributes { get; init; }
        = new Dictionary<string, string?>();
}

public sealed record BrokerAccountBalance
{
    /// <summary>Stable provider-independent key where possible, e.g. available_cash or buying_power.</summary>
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public decimal? Value { get; init; }
    public string? Currency { get; init; }
    public IReadOnlyDictionary<string, string?> Attributes { get; init; }
        = new Dictionary<string, string?>();
}

public sealed record BrokerAccountHolding
{
    public string InstrumentId { get; init; } = "";
    public string? Symbol { get; init; }
    public string? Exchange { get; init; }
    public string? AssetType { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? AverageCost { get; init; }
    public decimal? MarketPrice { get; init; }
    public decimal? CostValue { get; init; }
    public decimal? MarketValue { get; init; }
    public decimal? UnrealizedProfitLoss { get; init; }
    public decimal? UnrealizedProfitLossPercent { get; init; }
    public string? Currency { get; init; }
    public IReadOnlyDictionary<string, string?> Attributes { get; init; }
        = new Dictionary<string, string?>();
}

public sealed record BrokerAccountOrder
{
    public string OrderId { get; init; } = "";
    public string? ExternalOrderId { get; init; }
    public string InstrumentId { get; init; } = "";
    public string? Symbol { get; init; }
    public string? Exchange { get; init; }
    public string? Side { get; init; }
    public string? OrderType { get; init; }
    public string? Status { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? RemainingQuantity { get; init; }
    public decimal? Price { get; init; }
    public decimal? TriggerPrice { get; init; }
    public string? Currency { get; init; }
    public string? PlacedAt { get; init; }
    public IReadOnlyDictionary<string, string?> Attributes { get; init; }
        = new Dictionary<string, string?>();
}
