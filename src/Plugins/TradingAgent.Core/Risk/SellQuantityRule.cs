using TradingAgent.Models;
using TradingAgent.Reconciliation;

namespace TradingAgent.Risk;

public sealed record SellAvailabilityDecision(
    bool Known,
    int AvailableQuantity,
    string Reason);

public sealed record SellQuantityAdjustment(
    int GroupIndex,
    int OrderIndex,
    string Symbol,
    int RequestedQuantity,
    int SubmittedQuantity)
{
    public string Message =>
        $"SELL quantity reduced from {RequestedQuantity:N0} to {SubmittedQuantity:N0} "
        + $"because only {SubmittedQuantity:N0} {Symbol} share(s) remained available after "
        + "outstanding and same-batch SELL commitments.";
}

public sealed record SellSizingPlan(
    IReadOnlyList<IReadOnlyList<TradingSignal>> Groups,
    IReadOnlyList<SellQuantityAdjustment> Adjustments,
    string? Problem = null);

/// <summary>
/// Sizes independent SELL orders against the broker's fresh custody position minus already-resting
/// SELL quantities. Unknown state refuses the order; it is never interpreted as zero or as permission
/// to send the requested size.
/// </summary>
public static class SellQuantityRule
{
    public static bool HasIndependentSell(IReadOnlyList<IReadOnlyList<TradingSignal>> groups)
    {
        foreach (var group in groups)
        {
            var seenBuys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var signal in group)
            {
                var symbol = (signal.Symbol ?? "").Trim().ToUpperInvariant();
                if (string.Equals(signal.Action, "BUY", StringComparison.OrdinalIgnoreCase))
                {
                    seenBuys.Add(symbol);
                    continue;
                }
                if (string.Equals(signal.Action, "SELL", StringComparison.OrdinalIgnoreCase)
                    && symbol.Length > 0
                    && !seenBuys.Contains(symbol)
                    && signal.Quantity is > 0)
                    return true;
            }
        }
        return false;
    }

    public static SellAvailabilityDecision Available(
        BrokerReconciliationSnapshot snapshot,
        string symbol,
        DateTime nowUtc,
        TimeSpan maxAge,
        IReadOnlySet<string>? excludedOrderNumbers = null)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (!snapshot.Supported || !snapshot.Healthy || nowUtc - snapshot.CheckedUtc > maxAge)
            return new(false, 0,
                $"Sellable holdings for {symbol} are unavailable because broker reconciliation "
                + $"is not healthy and fresh: {snapshot.Reason}");

        var held = snapshot.Positions
            .Where(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            .Sum(p => Math.Max(0m, p.Quantity));

        var matchingSells = snapshot.OpenOrders
            .Where(o => string.Equals(o.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
                     && IsSell(o.Side)
                     && !(excludedOrderNumbers?.Contains((o.OrderNo ?? "").Trim()) ?? false))
            .ToList();

        if (matchingSells.Any(o => o.RemainingQuantity is null))
            return new(false, 0,
                $"An outstanding SELL for {symbol} has no remaining quantity, so another SELL "
                + "cannot be sized safely.");

        var committed = matchingSells.Sum(o => Math.Max(0m, o.RemainingQuantity!.Value));
        var available = Math.Max(0m, decimal.Floor(held - committed));
        return new(true, available >= int.MaxValue ? int.MaxValue : (int)available,
            $"{held:N0} held minus {committed:N0} already committed to outstanding SELL orders.");
    }

    /// <summary>
    /// Reduces standalone SELLs in request order, reserving shares across the whole batch. A SELL
    /// following a BUY for the same symbol inside one dependent group is a contingent exit and is
    /// left to that group's existing buy-then-sell handling.
    /// </summary>
    public static SellSizingPlan SizeIndependentSells(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups,
        BrokerReconciliationSnapshot snapshot,
        DateTime nowUtc,
        TimeSpan maxAge)
    {
        var output = groups.Select(g => (IReadOnlyList<TradingSignal>)g.ToList()).ToList();
        var adjustments = new List<SellQuantityAdjustment>();
        var remainingBySymbol = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var gi = 0; gi < groups.Count; gi++)
        {
            var seenBuys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rewritten = output[gi].ToList();
            for (var oi = 0; oi < groups[gi].Count; oi++)
            {
                var signal = groups[gi][oi];
                var symbol = (signal.Symbol ?? "").Trim().ToUpperInvariant();
                if (string.Equals(signal.Action, "BUY", StringComparison.OrdinalIgnoreCase))
                {
                    seenBuys.Add(symbol);
                    continue;
                }

                if (!string.Equals(signal.Action, "SELL", StringComparison.OrdinalIgnoreCase)
                    || symbol.Length == 0
                    || seenBuys.Contains(symbol)
                    || signal.Quantity is not > 0)
                    continue;

                if (!remainingBySymbol.TryGetValue(symbol, out var available))
                {
                    var decision = Available(snapshot, symbol, nowUtc, maxAge);
                    if (!decision.Known)
                        return new(groups, adjustments, decision.Reason);
                    available = decision.AvailableQuantity;
                }

                if (available <= 0)
                    return new(groups, adjustments,
                        $"No uncommitted {symbol} shares are available to sell; nothing was submitted.");

                var requested = signal.Quantity.Value;
                var submitted = Math.Min(requested, available);
                remainingBySymbol[symbol] = available - submitted;
                if (submitted == requested) continue;

                rewritten[oi] = CopyWithQuantity(signal, submitted);
                adjustments.Add(new(gi, oi, symbol, requested, submitted));
            }
            output[gi] = rewritten;
        }

        return new(output, adjustments);
    }

    private static TradingSignal CopyWithQuantity(TradingSignal signal, int quantity) => new()
    {
        IsSignal = signal.IsSignal,
        Action = signal.Action,
        Symbol = signal.Symbol,
        EntryPrice = signal.EntryPrice,
        Target = signal.Target,
        StopLoss = signal.StopLoss,
        Quantity = quantity,
        OrderType = signal.OrderType,
        LimitPrice = signal.LimitPrice,
        PreservePriceIntent = signal.PreservePriceIntent,
        FiresOnRisingPrice = signal.FiresOnRisingPrice,
        Confidence = signal.Confidence,
        ConfidenceReason = signal.ConfidenceReason,
        RawMessage = signal.RawMessage,
        Sender = signal.Sender,
        Timestamp = signal.Timestamp
    };

    private static bool IsSell(string? side) =>
        side is not null
        && (side.Equals("SELL", StringComparison.OrdinalIgnoreCase)
         || side.Equals("SEL", StringComparison.OrdinalIgnoreCase));
}
