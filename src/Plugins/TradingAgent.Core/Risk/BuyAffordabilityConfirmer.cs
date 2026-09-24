using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Reconciliation;

namespace TradingAgent.Risk;

/// <summary>
/// Whether the broker will fund a BUY, and whether a fresh read backs that answer.
/// </summary>
/// <param name="Affordability">
/// The figure to refuse on, backed by a broker read when <paramref name="BrokerConfirmed"/> is set and
/// otherwise a cached figure that already covered the order.
/// </param>
/// <param name="BrokerConfirmed">
/// A read was taken FOR this question and this is its answer. The only state in which a caller may
/// refuse an order.
/// </param>
/// <param name="ConfirmationFailure">
/// Why the confirming read could not be taken. Non-null means the order goes through to the broker
/// unchecked, which is what happened before this class existed and is strictly no worse.
/// </param>
public sealed record ConfirmedBuyAffordability(
    BuyAffordability Affordability,
    bool BrokerConfirmed,
    string? ConfirmationFailure,
    BrokerReconciliationSnapshot Snapshot);

/// <summary>
/// The one place that decides whether a cached buying-power figure is good enough to refuse a BUY on,
/// and the one place that spends a broker read when it is not.
///
/// <para>
/// The BUY-side counterpart of <see cref="SellAvailabilityConfirmer"/>, and deliberately built to the
/// same shape so the two cannot drift on the rule that matters: a cached answer never refuses. It is a
/// separate class rather than a second method on that one because the questions differ in the
/// direction staleness runs — see <see cref="BuyAffordabilityRule.NeedsBrokerConfirmation"/> — and a
/// shared implementation would have to carry both asymmetries in one body.
/// </para>
///
/// <para>
/// Reads go through <see cref="BrokerReconciliationWorker.RunNowAsync"/> for the reasons stated on the
/// sell confirmer: it is single-flight, so callers arriving together share one pass, and it PUBLISHES
/// what it read, so the snapshot every other consumer sees is corrected as a side effect.
/// </para>
/// </summary>
public sealed class BuyAffordabilityConfirmer
{
    private readonly TradingReconciliationState _state;
    private readonly BrokerReconciliationWorker _worker;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<BuyAffordabilityConfirmer> _logger;

    public BuyAffordabilityConfirmer(
        TradingReconciliationState state,
        BrokerReconciliationWorker worker,
        IOptions<TradingAgentOptions> options,
        ILogger<BuyAffordabilityConfirmer> logger)
    {
        _state = state;
        _worker = worker;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// What the broker will fund, confirmed with it if and only if the cached figure would refuse this
    /// order.
    ///
    /// <para>Never throws: a failed read is reported as <see cref="ConfirmedBuyAffordability.ConfirmationFailure"/>.</para>
    /// </summary>
    public async Task<ConfirmedBuyAffordability> ForBuyAsync(
        string symbol, decimal orderValuePkr, CancellationToken ct = default)
    {
        var maxAge = TimeSpan.FromSeconds(Math.Max(10, _options.Value.ReconciliationMaxAgeSeconds));
        var cachedSnapshot = _state.Current;
        var cached = BuyAffordabilityRule.Available(cachedSnapshot, DateTime.UtcNow, maxAge);

        if (!BuyAffordabilityRule.NeedsBrokerConfirmation(cached, orderValuePkr))
            return new(cached, BrokerConfirmed: false, ConfirmationFailure: null, cachedSnapshot);

        BrokerReconciliationSnapshot fresh;
        try
        {
            fresh = await _worker.RunNowAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "[BuyAffordability] {Symbol} buying power could not be confirmed with the broker for "
                + "an order worth {Value:N2} PKR; the order is not refused here.",
                symbol, orderValuePkr);
            return new(cached, BrokerConfirmed: false, ConfirmationFailure: ex.Message, cachedSnapshot);
        }

        var confirmed = BuyAffordabilityRule.Available(fresh, DateTime.UtcNow, maxAge);

        // Worth a line whenever the cache was wrong, because this is the ONLY place that staleness is
        // observable. Downstream, a refusal taken on a stale figure looks exactly like a correct one.
        if (confirmed.Known != cached.Known || confirmed.BuyingPowerPkr != cached.BuyingPowerPkr)
            _logger.LogInformation(
                "[BuyAffordability] {Symbol} cached {Cached} but the broker says {Confirmed} for an "
                + "order worth {Value:N2} PKR. Using the broker's figure.",
                symbol,
                cached.Known ? cached.BuyingPowerPkr.ToString("N2") : "unknown",
                confirmed.Known ? confirmed.BuyingPowerPkr.ToString("N2") : "unknown",
                orderValuePkr);

        return new(confirmed, BrokerConfirmed: true, ConfirmationFailure: null, fresh);
    }
}
