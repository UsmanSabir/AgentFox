namespace TradingAgent.Research;

/// <summary>
/// One completed daily OHLC bar for a PSX security, as published by the exchange's historical
/// market summary. <see cref="PreviousClose"/> is the portal's LDCP (last day closing price) and is
/// nullable because the table occasionally omits it; O/H/L/C are required for a bar to exist at all,
/// so a row missing any of them is dropped rather than zero-filled.
/// </summary>
public sealed record PsxCandle
{
    public string Symbol { get; init; } = "";

    /// <summary>Trading session this bar belongs to (the session date for intraday bars too).</summary>
    public DateOnly Date { get; init; }

    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal? PreviousClose { get; init; }
    public long Volume { get; init; }

    /// <summary>
    /// True when this bar is still forming — the live market-watch bar for the current session, or
    /// the in-progress bucket of an intraday series — rather than a settled bar.
    /// </summary>
    public bool IsLive { get; init; }

    /// <summary>Bar width in minutes. 1440 (one session) for daily bars.</summary>
    public int IntervalMinutes { get; init; } = DailyIntervalMinutes;

    /// <summary>
    /// Start of the bucket for an intraday bar; null for a daily bar. Intraday series are ordered by
    /// this, so <see cref="Date"/> alone is never used to sequence bars inside one session.
    /// </summary>
    public DateTime? BucketStartUtc { get; init; }

    public const int DailyIntervalMinutes = 1440;

    public bool IsIntraday => IntervalMinutes < DailyIntervalMinutes;

    /// <summary>Single sort key that orders daily and intraday bars alike.</summary>
    public DateTime SortKeyUtc =>
        BucketStartUtc ?? Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}

/// <summary>One executed trade from the portal's intraday tick feed.</summary>
public sealed record PsxTick(DateTime TimeUtc, decimal Price, long Quantity);

/// <summary>
/// Live (or last-traded) snapshot for one symbol from the exchange's market watch. This is the
/// forming candle for the current session: <see cref="Open"/>/<see cref="High"/>/<see cref="Low"/>
/// are today's range so far and <see cref="Current"/> is the last trade. All numeric fields are
/// nullable — the market watch publishes zeros/blanks for symbols that have not traded, and those
/// must read as "unknown", never as a real price of zero.
/// </summary>
public sealed record PsxLiveQuote
{
    public string Symbol { get; init; } = "";
    /// <summary>Issuer name published on the PSX market-watch symbol link.</summary>
    public string? CompanyName { get; init; }
    public string? Sector { get; init; }
    public decimal? PreviousClose { get; init; }
    public decimal? Open { get; init; }
    public decimal? High { get; init; }
    public decimal? Low { get; init; }
    public decimal? Current { get; init; }
    public decimal? ChangePercent { get; init; }
    public long? Volume { get; init; }
    public DateTime RetrievedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Which feed this came from — see <see cref="ILiveQuoteSource.Name"/> ("psx", "ahk"). Carried
    /// per quote rather than per snapshot because a snapshot can be merged from more than one source
    /// (the broker feed covers a subscribed subset, PSX covers the rest), and "how old and how
    /// trustworthy is this number" is then a per-symbol question.
    /// </summary>
    public string Source { get; init; } = "psx";

    // ── Depth (broker feed only) ──────────────────────────────────────────────
    // The PSX data portal publishes no order book at all, so these are null on any PSX-sourced
    // quote. They are the substantive gain from the broker feed: a limit price set against a real
    // best bid/ask is priced to actually fill, where one set against the last trade is a guess.

    /// <summary>Best bid, and the size resting at it.</summary>
    public decimal? BestBid { get; init; }
    public long? BestBidSize { get; init; }

    /// <summary>Best ask, and the size resting at it.</summary>
    public decimal? BestAsk { get; init; }
    public long? BestAskSize { get; init; }

    /// <summary>Session VWAP as published by the feed.</summary>
    public decimal? AveragePrice { get; init; }

    /// <summary>Number of trades so far this session.</summary>
    public long? TradeCount { get; init; }

    /// <summary>Last trade time exactly as the feed formatted it; not parsed, only displayed.</summary>
    public string? LastTradeTime { get; init; }

    /// <summary>Board state as published by the broker feed.</summary>
    public string? BoardState { get; init; }

    /// <summary>Best ask minus best bid, when both sides are quoted.</summary>
    public decimal? Spread =>
        BestBid is { } bid && BestAsk is { } ask && ask >= bid ? ask - bid : null;

    /// <summary>
    /// Projects the quote onto a forming daily candle for <paramref name="date"/>, or null when the
    /// symbol has not traded (no last price, or a degenerate all-zero row).
    /// </summary>
    public PsxCandle? ToCandle(DateOnly date)
    {
        if (Current is not > 0) return null;

        var open  = Open is > 0 ? Open.Value : Current.Value;
        var high  = High is > 0 ? High.Value : Math.Max(open, Current.Value);
        var low   = Low  is > 0 ? Low.Value  : Math.Min(open, Current.Value);

        return new PsxCandle
        {
            Symbol        = Symbol,
            Date          = date,
            Open          = open,
            High          = Math.Max(high, Current.Value),
            Low           = Math.Min(low, Current.Value),
            Close         = Current.Value,
            PreviousClose = PreviousClose,
            Volume        = Volume ?? 0,
            IsLive        = true
        };
    }
}

/// <summary>
/// Daily candle history for a set of symbols plus the live tick that tops it up, along with the
/// trading dates actually retrieved and any symbols the exchange feed did not cover.
/// </summary>
public sealed record CandleHistory
{
    public IReadOnlyDictionary<string, IReadOnlyList<PsxCandle>> Series { get; init; }
        = new Dictionary<string, IReadOnlyList<PsxCandle>>();

    public IReadOnlyDictionary<string, PsxLiveQuote> Live { get; init; }
        = new Dictionary<string, PsxLiveQuote>();

    /// <summary>Completed sessions covered, oldest first.</summary>
    public IReadOnlyList<DateOnly> Sessions { get; init; } = [];

    public DateTime RetrievedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Non-fatal problems (a date that could not be fetched, a symbol with no rows).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
