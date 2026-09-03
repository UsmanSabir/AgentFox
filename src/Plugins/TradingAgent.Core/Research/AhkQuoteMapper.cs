using TradingAgent.Feed;

namespace TradingAgent.Research;

/// <summary>
/// Converts the AHK portal's feed records into the <see cref="PsxLiveQuote"/> shape the rest of the
/// plugin already consumes, so the broker feed is a drop-in alternative to the PSX market watch
/// rather than a second parallel model everything has to learn.
/// </summary>
public static class AhkQuoteMapper
{
    /// <summary>Source tag written onto every quote this mapper produces.</summary>
    public const string SourceName = "ahk";

    /// <summary>
    /// Maps one feed record, or null when it carries nothing usable.
    ///
    /// <para>
    /// The critical rule is that <b>zero means unknown</b>. The portal publishes 0.00 for every
    /// price field of a symbol that has not traded today, and those zeros reach here as real
    /// decimals. Letting one through as a price would be worse than having no feed at all: a stop
    /// evaluated against a "price" of zero triggers instantly, and a percentage computed against a
    /// zero previous close is a division by zero. Every numeric field therefore goes through
    /// <see cref="Positive"/> or <see cref="PositiveVolume"/>.
    /// </para>
    /// </summary>
    public static PsxLiveQuote? ToLiveQuote(AhkFeedQuote raw, DateTime nowUtc)
    {
        var symbol = raw.Symbol?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(symbol)) return null;

        var last          = Positive(raw.LastPrice);
        var previousClose = Positive(raw.ClosePrice);

        // The portal publishes an ABSOLUTE change; every consumer here wants a percentage. Deriving
        // it needs a real previous close — without one the change is unattributable, so it stays null
        // rather than being invented from the open or the last price.
        decimal? changePercent = null;
        if (raw.Change is { } change && previousClose is { } prev && prev > 0m)
            changePercent = change / prev * 100m;

        return new PsxLiveQuote
        {
            Symbol         = symbol,
            Current        = last,
            Open           = Positive(raw.OpenPrice),
            High           = Positive(raw.High),
            Low            = Positive(raw.Low),
            PreviousClose  = previousClose,
            ChangePercent  = changePercent,
            Volume         = PositiveVolume(raw.TotalVolume),

            BestBid        = Positive(raw.Buy),
            BestBidSize    = PositiveVolume(raw.BVol),
            BestAsk        = Positive(raw.Sell),
            BestAskSize    = PositiveVolume(raw.SVol),
            AveragePrice   = Positive(raw.Average),
            TradeCount     = PositiveVolume(raw.TotTrd),
            LastTradeTime  = string.IsNullOrWhiteSpace(raw.LTrdTime) ? null : raw.LTrdTime.Trim(),
            BoardState     = string.IsNullOrWhiteSpace(raw.State) ? null : raw.State.Trim(),

            Source         = SourceName,
            RetrievedAtUtc = nowUtc
        };
    }

    /// <summary>
    /// Folds a newer record over an older one, keeping the older value wherever the newer one says
    /// nothing. Needed because a feed message may republish only the fields that moved; overwriting
    /// wholesale would blank out a symbol's high and low the moment a message carried only a bid
    /// change.
    ///
    /// <para>
    /// <b>The merged quote's age is the age of its PRICE, not of the message that produced it.</b>
    /// Carrying a previous <see cref="PsxLiveQuote.Current"/> forward while stamping it with the new
    /// arrival time made a stale price permanently fresh: <c>AhkQuoteBook.Snapshot</c> expires on
    /// <c>RetrievedAtUtc</c>, so a symbol the portal republishes without trading had its clock reset
    /// every poll (2s by default) and <c>MaxQuoteAgeSeconds</c> could never reach it. An arbitrarily
    /// old price was then handed to armed-order evaluation as a current one.
    /// </para>
    ///
    /// <para>
    /// The consequence to expect: symbols that genuinely have not traded now age out and disappear
    /// from the quote book, so consumers see no price rather than an old one. That is the intended
    /// direction — the engine's rule is that an unknown value is a refusal, not a default — but it does
    /// mean a quiet symbol stops being quotable after <c>MaxQuoteAgeSeconds</c> instead of appearing
    /// to trade at its last known price forever.
    /// </para>
    /// </summary>
    public static PsxLiveQuote Merge(PsxLiveQuote existing, PsxLiveQuote update) => update with
    {
        // A message that carries no price tells us nothing about when this price formed, so it does
        // not get to say the price is new.
        RetrievedAtUtc = update.Current is null ? existing.RetrievedAtUtc : update.RetrievedAtUtc,
        Current       = update.Current       ?? existing.Current,
        Open          = update.Open          ?? existing.Open,
        High          = update.High          ?? existing.High,
        Low           = update.Low           ?? existing.Low,
        PreviousClose = update.PreviousClose ?? existing.PreviousClose,
        ChangePercent = update.ChangePercent ?? existing.ChangePercent,
        Volume        = update.Volume        ?? existing.Volume,
        BestBid       = update.BestBid       ?? existing.BestBid,
        BestBidSize   = update.BestBidSize   ?? existing.BestBidSize,
        BestAsk       = update.BestAsk       ?? existing.BestAsk,
        BestAskSize   = update.BestAskSize   ?? existing.BestAskSize,
        AveragePrice  = update.AveragePrice  ?? existing.AveragePrice,
        TradeCount    = update.TradeCount    ?? existing.TradeCount,
        LastTradeTime = update.LastTradeTime ?? existing.LastTradeTime,
        BoardState    = update.BoardState    ?? existing.BoardState,
        Sector        = update.Sector        ?? existing.Sector
    };

    /// <summary>A price of zero or less is the portal saying "no data", never a real price.</summary>
    private static decimal? Positive(decimal? value) => value is > 0m ? value : null;

    /// <summary>
    /// Volumes differ from prices: zero shares traded is a genuine, meaningful observation for a
    /// symbol that has not traded yet, so only negatives are rejected.
    /// </summary>
    private static long? PositiveVolume(long? value) => value is >= 0 ? value : null;
}
