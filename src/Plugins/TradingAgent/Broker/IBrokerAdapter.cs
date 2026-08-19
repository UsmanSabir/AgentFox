using System.Globalization;
using TradingAgent.Market;
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

        // Fills need BOTH a positive fillVolume and evidence that something actually traded, because
        // fillVolume alone lies. Captured live on 2026-08-19: an order rejected for an out-of-band price
        // came back as action "REJ" with fillVolume 1, price 0 and totalValue 0 — so the original rule
        // (fillVolume > 0) counted a rejected order as a completed fill of the whole quantity. That is the
        // worst direction for this particular error to run in: reconciliation would report a position the
        // account does not hold, and the protective-stop path would then try to protect it.
        //
        // The guard stays conservative about UNKNOWN codes, which was the right instinct in the original:
        // an unrecognised action is not assumed to be a fill or a non-fill, it is judged on whether a real
        // price came with it. Only the two codes now confirmed to be non-fills are excluded by name.
        var fills = activity?
            .Where(a => a.FillVolume is > 0
                     && a.Price is > 0
                     && !IsNonFillAction(a.Action))
            .ToList() ?? [];

        // Mapped once, here, so the ledger rows and the details blob cannot disagree about what filled.
        // The activity log carries a local wall-clock time only ("12:18:13"), so the DATE comes from the
        // session being read; stamping UtcNow instead would be wrong by up to a whole reconciliation
        // interval, and these timestamps get compared against bar times.
        var structuredFills = fills
            .Where(f => !string.IsNullOrWhiteSpace(f.OrderNo))
            .Select(f => new BrokerFill(
                f.OrderNo!.Trim(),
                f.Scrip?.Trim().ToUpperInvariant() ?? "",
                f.Type?.Trim(),
                (int)Math.Round(f.FillVolume ?? 0m),
                f.Price ?? 0m,
                ParseActivityTimeUtc(f.Time, checkedUtc)))
            .ToList();

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
            // Fills still travel on an INCOMPLETE snapshot: a fill that was read is a fact whether or
            // not the balance came back, and dropping it here would lose a real execution because an
            // unrelated endpoint failed.
            return new BrokerReconciliationSnapshot(
                Supported: true,
                Healthy: false,
                Reason: "Broker state is incomplete: " + string.Join("; ", failures) + ".",
                CheckedUtc: checkedUtc,
                DetailsJson: details)
            { Fills = structuredFills };
        }

        return new BrokerReconciliationSnapshot(
            Supported: true,
            Healthy: true,
            Reason: $"Read {outstanding.Orders.Count} resting order(s), {fills.Count} fill(s) today, " +
                    $"{holdings!.Count} holding(s) and the cash balance from the broker.",
            CheckedUtc: checkedUtc,
            DetailsJson: details)
        { Fills = structuredFills };
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

    /// <summary>
    /// Action codes the portal's activity log uses for events that did NOT trade. Confirmed live:
    /// <c>QUE</c> queued, <c>REJ</c> rejected, <c>CLX</c> cancelled. Only REJ and CLX are listed because
    /// they are the two that arrive carrying a non-zero <c>fillVolume</c> — QUE reports zero and is
    /// excluded by the volume test on its own.
    /// </summary>
    private static bool IsNonFillAction(string? action) =>
        action is not null
        && (action.Equals("REJ", StringComparison.OrdinalIgnoreCase)
         || action.Equals("CLX", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Turns the activity log's local wall-clock time ("12:18:13") into UTC, on the PSX trading day the
    /// snapshot was taken. Falls back to the snapshot time when the field is missing or unparseable —
    /// never to a default date, which would silently file a real fill under the year 1.
    /// </summary>
    private static DateTime ParseActivityTimeUtc(string? time, DateTime fallbackUtc)
    {
        if (string.IsNullOrWhiteSpace(time)
            || !TimeSpan.TryParse(time.Trim(), CultureInfo.InvariantCulture, out var parsed))
            return fallbackUtc;

        var local = PsxTime.Today().ToDateTime(TimeOnly.FromTimeSpan(parsed));
        return TimeZoneInfo.ConvertTimeToUtc(local, PsxTime.Zone);
    }
}
