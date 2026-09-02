namespace TradingAgent.Research;

/// <summary>
/// A source of live quotes for the WHOLE market in one call.
///
/// <para>
/// The market-wide shape is the contract, not an implementation detail: every consumer in this
/// plugin (the watchlist pass, the proposal drift sweep, the candle top-up) takes one snapshot and
/// serves every symbol from it. A per-symbol source dropped in behind this interface would turn a
/// 2-minute monitoring cadence into hundreds of requests per pass, which is how a feed gets an
/// account rate-limited or flagged. Implementations that poll must poll once for everything;
/// implementations fed by a push stream should keep an in-memory book and answer from it, so
/// <see cref="GetQuotesAsync"/> stays cheap enough to call on every pass.
/// </para>
///
/// <para>
/// Every implementation is <b>fail-soft</b>. A dead feed returns an empty snapshot carrying a
/// warning; it never throws out of <see cref="GetQuotesAsync"/>. Live prices sharpen a decision
/// but no decision in this plugin is allowed to depend on them being available, and an exception
/// escaping here would take down a monitoring pass that could still have run on settled closes.
/// </para>
/// </summary>
public interface ILiveQuoteSource
{
    /// <summary>
    /// Which source wins when more than one can price the same symbol. Higher wins.
    ///
    /// <para>
    /// <b>Not to be confused with <c>ILiveFeedStatusProvider.Priority</c>, which orders a status
    /// display and nothing else.</b> This one decides whose price a trading decision sees.
    /// </para>
    ///
    /// <para>
    /// It exists because precedence used to be DI REGISTRATION ORDER, which an edition cannot change:
    /// <c>AddCore</c> registers core's sources first, so a plugin's source was always consulted last —
    /// and <c>PsxMarketWatchQuoteSource</c> returns the entire market on every call with
    /// <c>IsEnabled</c> unconditionally true, claiming every symbol. A push feed registered by an
    /// edition was therefore dead on arrival as a price source however healthy it was.
    /// </para>
    ///
    /// <para>
    /// Defaults to zero, and ties keep registration order, so a source that says nothing behaves
    /// exactly as it did.
    /// </para>
    /// </summary>
    int Priority => 0;


    /// <summary>
    /// Short stable identifier used in warnings, logs, and each quote's <see cref="PsxLiveQuote.Source"/>
    /// tag (for example "psx" or "ahk"). Shown to operators, so it must say where a price came from.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// True when this source is configured and expected to produce quotes. A source that is switched
    /// off (no credentials, feature disabled) reports false so the composite can skip it silently
    /// rather than reporting an outage every pass for something nobody turned on.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// The current snapshot for every symbol the source covers. Never throws — see the class remarks.
    /// </summary>
    Task<LiveQuoteSnapshot> GetQuotesAsync(CancellationToken ct = default);
}

/// <summary>
/// One market-wide read from an <see cref="ILiveQuoteSource"/>, with the provenance a reader needs
/// to judge it: which source answered, when, and what went wrong if the answer is thin.
/// </summary>
public sealed record LiveQuoteSnapshot
{
    public IReadOnlyDictionary<string, PsxLiveQuote> Quotes { get; init; }
        = new Dictionary<string, PsxLiveQuote>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which source produced this snapshot. For a merged snapshot, the primary that led it.</summary>
    public string Source { get; init; } = "";

    public DateTime RetrievedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Non-fatal problems, phrased for an operator. A source that failed entirely still returns a
    /// snapshot — an empty one — with the reason here, because "no prices and no explanation" is the
    /// failure mode that costs an afternoon.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool IsEmpty => Quotes.Count == 0;

    public static LiveQuoteSnapshot Empty(string source, string? warning = null) => new()
    {
        Source   = source,
        Warnings = warning is null ? [] : [warning]
    };
}
