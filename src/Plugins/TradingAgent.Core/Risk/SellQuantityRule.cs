using TradingAgent.Models;
using TradingAgent.Reconciliation;

namespace TradingAgent.Risk;

/// <param name="Known">
/// The broker book could be read. False means nothing here may be acted on — see invariant 4.
/// </param>
/// <param name="AvailableQuantity">Custody minus what is already committed to resting SELL orders.</param>
/// <param name="Reason">The arithmetic, in the operator's words.</param>
/// <param name="HeldQuantity">
/// What CUSTODY says, before any commitment is subtracted. Null when <paramref name="Known"/> is false.
///
/// <para>
/// <b>Carried separately because zero available has two completely different meanings and the
/// arithmetic cannot tell them apart.</b> "You own 2471 and every one is committed to a resting stop"
/// clears the moment that stop is superseded or the venue clears the book at the close, so a triggered
/// exit should wait. "You own none" never clears, and an order waiting on it waits for ever.
/// </para>
///
/// <para>
/// MEASURED 2026-09-11: an armed trailing SELL for 2471 CNERGY outlived the position a sibling order had
/// already sold, and was triggered and refused 29 times in 53 minutes on the identical message, because
/// the only fact distinguishing the two cases was folded away into a single zero. See
/// <see cref="ArmedSellOutlook"/>.
/// </para>
/// </param>
public sealed record SellAvailabilityDecision(
    bool Known,
    int AvailableQuantity,
    string Reason,
    int? HeldQuantity = null);

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
        var heldWhole = Math.Max(0m, decimal.Floor(held));
        return new(true, available >= int.MaxValue ? int.MaxValue : (int)available,
            $"{held:N0} held minus {committed:N0} already committed to outstanding SELL orders.",
            heldWhole >= int.MaxValue ? int.MaxValue : (int)heldWhole);
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

    /// <summary>
    /// The one spelling of "this resting order is a SELL". Public so a caller that has to NAME the
    /// orders behind a commitment (see <see cref="SellRefusalRule.RestingSellsNotPlacedHere"/>) matches
    /// exactly the orders whose quantity <see cref="Available"/> counted — a second private copy of this
    /// vocabulary would let the two disagree silently.
    /// </summary>
    public static bool IsSell(string? side) =>
        side is not null
        && (side.Equals("SELL", StringComparison.OrdinalIgnoreCase)
         || side.Equals("SEL", StringComparison.OrdinalIgnoreCase));
}
