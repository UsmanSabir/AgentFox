using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Reconciliation;

namespace TradingAgent.Risk;

/// <summary>
/// How many shares a SELL may actually be sized against, and whether a fresh broker read backs that
/// figure.
/// </summary>
/// <param name="Decision">
/// The availability to size or refuse on. Backed by a broker read when <paramref name="BrokerConfirmed"/>
/// is set, and otherwise a cached figure that already covered the request.
/// </param>
/// <param name="BrokerConfirmed">
/// A read was taken FOR this question and this is its answer. The only state in which a caller may
/// refuse an order, or cancel protection, on a zero.
/// </param>
/// <param name="ConfirmationFailure">
/// Why the confirming read could not be taken. Non-null means nothing local is trustworthy enough to
/// size this sell — see <see cref="MustDeferSizing"/>.
/// </param>
/// <param name="Snapshot">
/// The account picture <paramref name="Decision"/> was computed from. Carried so a caller explaining a
/// refusal can name the orders behind it from the SAME read that produced the number, rather than
/// re-reading the shared state and relying on the publish having landed.
/// </param>
public sealed record ConfirmedSellAvailability(
    SellAvailabilityDecision Decision,
    bool BrokerConfirmed,
    string? ConfirmationFailure,
    BrokerReconciliationSnapshot Snapshot)
{
    /// <summary>
    /// The cached figure would have short-changed this sell and the broker could not be asked. Carry
    /// the full requested quantity and let the execution boundary size it: <c>TradingManager</c>
    /// re-reads the broker book for every independent SELL, so deferring is safe, and it is what this
    /// codebase already does when a released stop makes the local snapshot knowingly wrong.
    ///
    /// <para>
    /// Clamping instead would silently undersize the sell on a figure just established as untrustworthy;
    /// refusing instead would invent a hurdle out of a broker hiccup (§0.2).
    /// </para>
    /// </summary>
    public bool MustDeferSizing => ConfirmationFailure is not null;
}

/// <summary>
/// The one place that decides whether a cached sell availability is good enough, and the one place that
/// spends a broker read when it is not.
///
/// <para>
/// It exists because the same four lines — <c>SellQuantityRule.Available(reconciliation.Current, symbol,
/// UtcNow, maxAge)</c> — were written at five call sites, each then acting on the answer as though it
/// were current. It is not: <see cref="TradingReconciliationState"/> is refreshed by
/// <see cref="BrokerReconciliationWorker"/> on a timer, and the age test inside
/// <see cref="SellQuantityRule.Available"/> only catches a DEAD worker, never stale CONTENT. A snapshot
/// can pass the age test and still describe the account as it was a whole poll interval ago — which on
/// the deployed configuration is 21 minutes. See <see cref="SellRefusalRule"/> for the incident.
/// </para>
///
/// <para>
/// Reads go through <see cref="BrokerReconciliationWorker.RunNowAsync"/> rather than
/// <see cref="IBrokerStateReader"/> directly, because it is single-flight — several callers arriving at
/// once share one pass instead of each opening their own — and because it PUBLISHES what it read, so
/// the snapshot every other consumer reads is corrected as a side effect of one caller's question.
/// </para>
///
/// <para>
/// The cost is bounded by <see cref="SellRefusalRule.NeedsBrokerConfirmation"/>: a read happens only
/// when the cached figure would refuse or shrink the sell, which is a path already about to produce a
/// worse outcome than one broker call.
/// </para>
/// </summary>
public sealed class SellAvailabilityConfirmer
{
    private readonly TradingReconciliationState _state;
    private readonly BrokerReconciliationWorker _worker;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<SellAvailabilityConfirmer> _logger;

    public SellAvailabilityConfirmer(
        TradingReconciliationState state,
        BrokerReconciliationWorker worker,
        IOptions<TradingAgentOptions> options,
        ILogger<SellAvailabilityConfirmer> logger)
    {
        _state = state;
        _worker = worker;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// What is free to sell of <paramref name="symbol"/> for a sell of <paramref name="requestedQuantity"/>,
    /// confirmed with the broker if and only if the cached answer would have short-changed it.
    ///
    /// <para>Never throws: a failed read is reported as <see cref="ConfirmedSellAvailability.MustDeferSizing"/>.</para>
    /// </summary>
    public async Task<ConfirmedSellAvailability> ForSellAsync(
        string symbol, int requestedQuantity, CancellationToken ct = default)
    {
        var maxAge = TimeSpan.FromSeconds(Math.Max(10, _options.Value.ReconciliationMaxAgeSeconds));
        var cachedSnapshot = _state.Current;
        var cached = SellQuantityRule.Available(cachedSnapshot, symbol, DateTime.UtcNow, maxAge);

        if (!SellRefusalRule.NeedsBrokerConfirmation(cached, requestedQuantity))
            return new(cached, BrokerConfirmed: false, ConfirmationFailure: null, cachedSnapshot);

        BrokerReconciliationSnapshot fresh;
        try
        {
            fresh = await _worker.RunNowAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "[SellAvailability] {Symbol} could not be confirmed with the broker for a sell of "
                + "{Requested}; sizing defers to the execution boundary.", symbol, requestedQuantity);
            return new(cached, BrokerConfirmed: false, ConfirmationFailure: ex.Message, cachedSnapshot);
        }

        var confirmed = SellQuantityRule.Available(fresh, symbol, DateTime.UtcNow, maxAge);

        // Worth a line whenever the cache was wrong, because this is the ONLY place the staleness is
        // observable. Every consequence of it downstream — a refusal, a smaller sell, a cancelled stop —
        // looks like a correct decision taken on correct data.
        if (confirmed.Known != cached.Known || confirmed.AvailableQuantity != cached.AvailableQuantity)
            _logger.LogInformation(
                "[SellAvailability] {Symbol} cached {Cached} but the broker says {Confirmed} for a sell "
                + "of {Requested}. Using the broker's figure. {Reason}",
                symbol,
                cached.Known ? cached.AvailableQuantity.ToString() : "unknown",
                confirmed.Known ? confirmed.AvailableQuantity.ToString() : "unknown",
                requestedQuantity,
                confirmed.Reason);

        return new(confirmed, BrokerConfirmed: true, ConfirmationFailure: null, fresh);
    }
}
