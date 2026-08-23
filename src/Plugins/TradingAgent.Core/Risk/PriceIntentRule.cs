using TradingAgent.Models;

namespace TradingAgent.Risk;

/// <summary>Protects an immutable persistent instruction from adverse daily price-band clamping.</summary>
public static class PriceIntentRule
{
    public static string? Validate(
        TradingSignal signal, decimal submittedPrice, decimal? submittedLimitPrice)
    {
        if (!signal.PreservePriceIntent || signal.EntryPrice is not { } requested) return null;

        var type = signal.OrderType.Trim().ToUpperInvariant();
        if (type == "STOPLOSS")
        {
            if (submittedPrice != requested
                || (signal.LimitPrice is { } requestedLimit && submittedLimitPrice != requestedLimit))
            {
                return "The order cannot be submitted inside today's price band without changing "
                     + "its stop trigger or limit. The persistent intent was left unchanged.";
            }
            return null;
        }

        var buy = signal.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase);
        if ((buy && submittedPrice > requested) || (!buy && submittedPrice < requested))
        {
            return $"Today's price band would change the {signal.Action.ToUpperInvariant()} limit "
                 + $"from {requested:0.##} to the worse price {submittedPrice:0.##}. The persistent "
                 + "intent was left unchanged.";
        }

        return null;
    }
}
