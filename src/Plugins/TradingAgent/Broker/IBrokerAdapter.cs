using System.Text.Json;
using TradingAgent.Feed;
using TradingAgent.Models;
using TradingAgent.Reconciliation;

namespace TradingAgent.Broker;

public interface IBrokerAdapter
{
    Task<IReadOnlyDictionary<string, decimal?>> GetMarketPricesAsync(IReadOnlyList<string> symbols);

    Task<IReadOnlyList<IReadOnlyList<OrderResult>>> PlaceOrderGroupsAsync(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups);
}

public sealed class AhkBrowserBrokerAdapter : IBrokerAdapter, IBrokerStateReader
{
    private readonly AhkBroker _broker;
    private readonly AhkPortalClient _portal;

    public AhkBrowserBrokerAdapter(AhkBroker broker, AhkPortalClient portal)
    {
        _broker = broker;
        _portal = portal;
    }

    public Task<IReadOnlyDictionary<string, decimal?>> GetMarketPricesAsync(IReadOnlyList<string> symbols) =>
        _broker.GetMarketPricesAsync(symbols);

    public Task<IReadOnlyList<IReadOnlyList<OrderResult>>> PlaceOrderGroupsAsync(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups) =>
        _broker.PlaceOrderGroupsAsync(groups);

    /// <summary>
    /// Reads the broker's own view of the account: resting orders, today's order events, holdings and
    /// cash — all four over the portal's JSON API, taking no browser gate.
    ///
    /// <para>
    /// This previously returned <c>Unsupported</c> on the grounds that there was "no reliable
    /// supported API for fills, positions, and balances". There is: <c>GetOutstanding</c>,
    /// <c>GetActivityLog</c>, <c>GetCollaterals</c> and <c>GetAccountBalance</c>, all captured live
    /// against the real portal. Order PLACEMENT stays on the browser; nothing here writes anything.
    /// </para>
    ///
    /// <para>
    /// <b>Every one of the four reads is required for health, deliberately.</b> This snapshot is what
    /// <c>TradingManager</c> consults before allowing an order in ApprovalRequired or BoundedAuto
    /// mode, and the question it answers is "can I still see what this account is doing?". A partial
    /// answer is not a smaller yes — a reconciliation that cannot see fills, or cannot see the
    /// resting book, is not reconciliation, and reporting it as healthy would put the gate's name to
    /// a check that did not happen. Unhealthy blocks orders, which is the safe direction.
    /// </para>
    /// </summary>
    public async Task<BrokerReconciliationSnapshot> ReadSnapshotAsync(CancellationToken ct = default)
    {
        var checkedUtc = DateTime.UtcNow;

        // Passive by construction: this runs on a 60-second timer, and establishing a session here
        // would mean harvesting cookies from the browser broker — which logs in when no session is
        // live. A dead session would then produce a login attempt every single tick. Reporting
        // "no session" is both honest and the only safe answer; the feed worker or a user-initiated
        // read establishes the session, and this picks it up on the next pass.
        if (!_portal.HasSession)
        {
            return new BrokerReconciliationSnapshot(
                Supported: true,
                Healthy: false,
                Reason: "No broker session is established, so the account's state could not be read. " +
                        "Reconciliation never logs in by itself; it reports what an existing session can see.",
                CheckedUtc: checkedUtc);
        }

        // Each read is preceded by the same check, because a session that dies MID-PASS drops
        // _sessionReady, and the next call would then re-establish it — the very login this method
        // is written to avoid, just four times over instead of once.
        var outstanding = await _portal.GetOutstandingAsync(ct: ct);
        if (!_portal.HasSession) return SessionLost(checkedUtc);

        var activity = await _portal.GetActivityLogAsync(ct: ct);
        if (!_portal.HasSession) return SessionLost(checkedUtc);

        var holdings = await _portal.GetCollateralsAsync(ct);
        if (!_portal.HasSession) return SessionLost(checkedUtc);

        var balance = await _portal.GetAccountBalanceAsync(ct);

        var failures = new List<string>();
        if (!outstanding.Ok) failures.Add(outstanding.Error ?? "the outstanding order book could not be read");
        if (activity is null) failures.Add("today's activity log could not be read");
        if (holdings is null) failures.Add("the account's holdings could not be read");
        if (balance is null) failures.Add("the available cash balance could not be read");

        // Fills come from the activity log's own fillVolume rather than being inferred from an
        // action code: the codes seen live were PEN and CLX, and an unrecognised future code must
        // never be guessed into meaning "filled".
        var fills = activity?.Where(a => a.FillVolume is > 0).ToList() ?? [];

        var details = JsonSerializer.Serialize(new
        {
            source = "portal JSON API",
            account = _portal.AccountCode,
            market_status = _portal.LastMarketStatus,
            resting_orders = outstanding.Ok ? outstanding.Orders.Count : (int?)null,
            activity_events = activity?.Count,
            fills_today = activity is null ? (int?)null : fills.Count,
            holdings = holdings?.Count,
            available_cash_pkr = balance,
            open_orders = outstanding.Ok
                ? outstanding.Orders.Select(o => new
                {
                    order_no = o.OrderNo,
                    symbol = o.Scrip,
                    side = o.Type,
                    price = o.Price,
                    remaining = o.Remaining
                }).ToArray()
                : null,
            positions = holdings?.Select(h => new
            {
                symbol = h.Symbol,
                quantity = h.QuantityTotal,
                average_price = h.AvgRateBuy,
                market_price = h.MtmPrice,
                // Rounded for the same reason PortfolioReader rounds: the portal computes these in
                // binary floating point, so a real position reported a market value of
                // 50049.00000000001. This JSON is read by operators and by the agent, and a total
                // carrying eleven decimal places reads as a broken number.
                market_value = Money(h.Amount),
                unrealised_pl = Money(h.Unsettled)
            }).ToArray()
        });

        if (failures.Count > 0)
        {
            // Supported stays TRUE: the capability exists, this pass just could not complete it.
            // Reporting it as unsupported would say the API does not exist, and an operator reading
            // that would go looking for a missing feature instead of a session or connectivity fault.
            return new BrokerReconciliationSnapshot(
                Supported: true,
                Healthy: false,
                Reason: "Broker state is incomplete: " + string.Join("; ", failures) + ".",
                CheckedUtc: checkedUtc,
                DetailsJson: details);
        }

        return new BrokerReconciliationSnapshot(
            Supported: true,
            Healthy: true,
            Reason: $"Read {outstanding.Orders.Count} resting order(s), {fills.Count} fill(s) today, " +
                    $"{holdings!.Count} holding(s) and the cash balance from the broker.",
            CheckedUtc: checkedUtc,
            DetailsJson: details);
    }

    /// <summary>Rounds a PKR amount to paisa, preserving null as unknown.</summary>
    private static decimal? Money(decimal? value) =>
        value is null ? null : Math.Round(value.Value, 2, MidpointRounding.AwayFromZero);

    private static BrokerReconciliationSnapshot SessionLost(DateTime checkedUtc) =>
        new(Supported: true,
            Healthy: false,
            Reason: "The broker session expired while the account's state was being read, so the " +
                    "snapshot is incomplete. It will be retaken once a session is live again.",
            CheckedUtc: checkedUtc);
}
