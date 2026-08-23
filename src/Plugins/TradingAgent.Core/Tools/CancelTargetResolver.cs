using TradingAgent.Feed;

namespace TradingAgent.Tools;

/// <summary>
/// Chooses which resting order a cancel request refers to, or refuses to choose.
///
/// <para>
/// Separated from <see cref="CancelOrderTool"/> and made pure so the refusal rules can be tested
/// without a broker. Refusing is the point of this type, not an edge case in it: a cancel gives up a
/// queue position irreversibly, and the order in question may be a protective stop. Cancelling the
/// wrong one is not something the user can undo by asking again.
/// </para>
/// </summary>
public static class CancelTargetResolver
{
    /// <summary>The resolved order, or the reason no single order could be identified. Never both.</summary>
    public readonly record struct Result(AhkOutstandingOrder? Order, string? Error);

    /// <summary>
    /// Resolves against <paramref name="book"/>: by exact <paramref name="orderNo"/> when given,
    /// otherwise by <paramref name="symbol"/> filtered to <paramref name="side"/> — and only when
    /// exactly one order matches.
    /// </summary>
    public static Result Resolve(
        IReadOnlyList<AhkOutstandingOrder> book, string? orderNo, string symbol, string side)
    {
        if (book.Count == 0)
        {
            return new(null, symbol.Length > 0
                ? $"The account has no working orders for {symbol}. Nothing to cancel."
                : "The account has no working orders. Nothing to cancel.");
        }

        if (!string.IsNullOrWhiteSpace(orderNo))
        {
            var wanted = orderNo.Trim();
            var match = book.FirstOrDefault(o =>
                string.Equals(o.OrderNo?.Trim(), wanted, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                // Naming the real order numbers matters: the usual cause is that the order already
                // filled or was cancelled, and the caller needs to see the current truth rather than
                // be told "no" and retry the same stale number.
                var available = string.Join(", ", book.Select(o => o.OrderNo));
                return new(null,
                    $"No working order numbered '{wanted}' exists on the account. It may already " +
                    $"have filled or been cancelled. Working order numbers are: {available}.");
            }

            return new(match, null);
        }

        var candidates = book
            .Where(o => string.Equals(o.Scrip?.Trim(), symbol, StringComparison.OrdinalIgnoreCase))
            .Where(o => AhkOrderSide.Matches(o.Type, side))
            .ToList();

        return candidates.Count switch
        {
            0 => new(null,
                $"The account has no working {(side == AhkOrderSide.All ? "" : side + " ")}order for {symbol}."),
            1 => new(candidates[0], null),
            _ => new(null,
                $"{candidates.Count} working orders match {symbol}: " +
                string.Join("; ", candidates.Select(o => o.Describe())) +
                ". Ask the user which one to cancel and pass its order_no — do not choose one.")
        };
    }
}
