using System.Globalization;
using AgentFox.Plugins;
using Microsoft.Extensions.Logging;
using TradingAgent.Config;
using TradingAgent.Observability;
using TradingAgent.Market;
using System.Text.Json;
using TradingAgent.Feed;
using TradingAgent.Models;
using TradingAgent.Reconciliation;
using TradingAgent.Risk;

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
    private readonly IRuntimePluginOptions<AhkConfig> _config;
    private readonly ILogger<AhkBrowserBrokerAdapter> _logger;
    private readonly TradingActivityLog? _activity;

    public AhkBrowserBrokerAdapter(
        AhkBroker broker,
        AhkPortalClient portal,
        IRuntimePluginOptions<AhkConfig> config,
        ILogger<AhkBrowserBrokerAdapter> logger,
        TradingActivityLog? activity = null)
    {
        _broker = broker;
        _portal = portal;
        _config = config;
        _logger = logger;
        _activity = activity;
    }

    public Task<IReadOnlyDictionary<string, decimal?>> GetMarketPricesAsync(IReadOnlyList<string> symbols) =>
        _broker.GetMarketPricesAsync(symbols);

    /// <summary>
    /// Places every group, over the JSON API when <see cref="AhkConfig.PreferDirectApiForPlacement"/> is on
    /// and a session exists, and through the browser otherwise.
    ///
    /// <para>
    /// Group semantics are the broker's and are preserved exactly: orders inside a group are DEPENDENT, so
    /// the first failure stops the rest of that group while later groups still run. Getting this wrong
    /// would be silent — a protective stop whose entry failed would go out on its own.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyList<OrderResult>>> PlaceOrderGroupsAsync(
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups)
    {
        if (!_config.Current.PreferDirectApiForPlacement)
            return await _broker.PlaceOrderGroupsAsync(groups);

        // A periodic caller must never establish a session (a login per pass gets an account locked), so
        // the API path is taken only when one already exists. No session means the browser path, which
        // owns logging in.
        if (!_portal.HasSession)
        {
            _logger.LogInformation(
                "[BrokerAdapter] Direct API placement is enabled but no session is live; using the browser.");
            return await _broker.PlaceOrderGroupsAsync(groups);
        }

        // One band read for the whole batch. The server does NOT enforce the price band — an out-of-band
        // order is accepted by the endpoint and rejected by the exchange seconds later (captured live) —
        // so on this path the band is entirely ours to police.
        var bands = await _portal.GetPriceBandsAsync();

        var output = new List<IReadOnlyList<OrderResult>>();
        foreach (var group in groups)
        {
            var results = new List<OrderResult>();
            foreach (var signal in group)
            {
                var result = await PlaceOneAsync(signal, bands);
                results.Add(result);
                if (!result.Success) break; // dependent within a group, as on the browser path
            }
            output.Add(results);
        }

        return output;
    }

    /// <summary>
    /// Submits one signal over the JSON API, falling back to the browser only when the portal proved
    /// nothing was placed. See <see cref="AhkConfig.PreferDirectApiForPlacement"/> for why that condition
    /// and no other.
    /// </summary>
    private async Task<OrderResult> PlaceOneAsync(
        TradingSignal signal, IReadOnlyList<AhkPriceBand> bands)
    {
        var cfg = _config.Current;
        var symbol = signal.Symbol.Trim().ToUpperInvariant();
        var qty = signal.Quantity ?? cfg.DefaultQty;
        var isBuy = signal.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase);
        var isStop = signal.OrderType.Equals("STOPLOSS", StringComparison.OrdinalIgnoreCase);
        var isMarket = signal.OrderType.Equals("MARKET", StringComparison.OrdinalIgnoreCase);

        // Market orders have never been captured on this endpoint, and the way it refuses a field it does
        // not like is to answer 200 with an empty body and place nothing. Routing one here would look like
        // a placed order to any reader who trusts the response. The browser path knows how to do it.
        if (isMarket)
        {
            _logger.LogInformation(
                "[BrokerAdapter] {Symbol} is a MARKET order, which is not verified on the JSON API; "
                + "using the browser for this one.", symbol);
            return await PlaceViaBrowserAsync(signal);
        }

        if (signal.EntryPrice is not > 0m)
        {
            _logger.LogInformation(
                "[BrokerAdapter] {Symbol} carries no price, so the browser path resolves it.", symbol);
            return await PlaceViaBrowserAsync(signal);
        }

        var band = bands.FirstOrDefault(b =>
            string.Equals(b.Symbol?.Trim(), symbol, StringComparison.OrdinalIgnoreCase)
            && string.Equals(b.Market?.Trim(), "REG", StringComparison.OrdinalIgnoreCase));

        // A missing band disables our optional local clamp; it does not prove the order itself is bad.
        // Submit the operator's unchanged price and let the broker/exchange apply the authoritative rule.
        // Crucially, this is NOT allowed to reuse a different symbol's band.
        var price = signal.EntryPrice!.Value;
        string? adjustment = null;
        if (band is null)
        {
            _logger.LogWarning(
                "[BrokerAdapter] No fresh REG price band for {Symbol}; submitting the requested price "
                + "unchanged for broker/exchange validation.", symbol);
        }
        else
        {
            (price, adjustment) = ClampToBand(price, band);
        }

        decimal? limitPrice = null;
        if (isStop)
        {
            var raw = signal.LimitPrice
                ?? decimal.Round(price * (1m - Math.Clamp(cfg.StopLimitSlippagePercent, 0m, 20m) / 100m), 2);
            limitPrice = band is null ? raw : ClampToBand(raw, band).Price;
        }

        if (PriceIntentRule.Validate(signal, price, limitPrice) is { } priceProblem)
        {
            _logger.LogWarning("[BrokerAdapter] {Symbol}: {Reason}", symbol, priceProblem);
            return new OrderResult
            {
                Success = false,
                Action = isBuy ? "BUY" : "SELL",
                Symbol = symbol,
                Quantity = qty,
                Message = priceProblem,
                RequestedPrice = signal.EntryPrice,
                SubmittedPrice = null
            };
        }

        var request = new PlaceOrderApiRequest(
            Side: isBuy ? "BUY" : "SEL",
            Symbol: symbol,
            Market: "REG",
            // AhkOrderTypes, never a literal: "Stop Loss" (the portal's own label) is discarded silently
            // and "StopLoss" is accepted.
            OrderType: isStop ? AhkOrderTypes.StopLoss : AhkOrderTypes.Limit,
            Volume: qty,
            Price: price,
            LimitPrice: limitPrice,
            Pin: cfg.TradingPin);

        var api = await _portal.PlaceOrderAsync(request);

        if (!api.Submitted)
        {
            // Proven not placed either way, so a retry cannot duplicate anything. But only an ENCODING
            // refusal is worth retrying: the dialog builds its request from the portal's own selects, so it
            // can succeed where a hand-built field failed. A refusal the portal explained ("Market is
            // closed") will be refused identically through the dialog, and retrying it only launches a
            // browser to be told no a second time.
            if (!api.RefusedByFieldEncoding)
            {
                _logger.LogWarning("[BrokerAdapter] The portal refused {Action} {Symbol}: {Message}",
                    signal.Action, symbol, api.Message);
                _activity?.Warn("Orders", $"The broker refused {signal.Action} {symbol}", api.Message);

                return new OrderResult
                {
                    Success = false,
                    Action = isBuy ? "BUY" : "SELL",
                    Symbol = symbol,
                    Quantity = qty,
                    Message = api.Message,
                    RequestedPrice = signal.EntryPrice,
                    SubmittedPrice = price,
                    PriceAdjustment = adjustment
                };
            }

            _logger.LogWarning(
                "[BrokerAdapter] The JSON API would not accept the fields for {Action} {Symbol}: {Message} "
                + "Falling back to the browser.", signal.Action, symbol, api.Message);
            _activity?.Warn("Orders",
                $"Direct API refused {signal.Action} {symbol}; retrying through the browser", api.Message);
            return await PlaceViaBrowserAsync(signal);
        }

        var success = api.IsLive;
        return new OrderResult
        {
            // Unknown counts as NOT success: an unconfirmed order must not be reported as placed, and the
            // reconciliation pass exists to settle it.
            Success = success,
            OrderId = api.OrderNo,
            Action = isBuy ? "BUY" : "SELL",
            Symbol = symbol,
            Quantity = qty,
            Message = api.Message + (adjustment is null ? "" : " " + adjustment),
            RequestedPrice = signal.EntryPrice,
            SubmittedPrice = price,
            PriceAdjustment = adjustment
        };
    }

    private async Task<OrderResult> PlaceViaBrowserAsync(TradingSignal signal)
    {
        var groups = new List<IReadOnlyList<TradingSignal>> { new List<TradingSignal> { signal } };
        var results = await _broker.PlaceOrderGroupsAsync(groups);
        return results.SelectMany(g => g).FirstOrDefault()
            ?? new OrderResult
            {
                Success = false,
                Action = signal.Action,
                Symbol = signal.Symbol,
                Message = "The browser path returned no result for this order."
            };
    }

    /// <summary>
    /// Clamps a price into the day's band, returning the note to record when it moved. PSX rejects
    /// anything outside the band, and on the JSON path nothing else is checking.
    /// </summary>
    private static (decimal Price, string? Adjustment) ClampToBand(decimal price, AhkPriceBand band)
    {
        if (band.UpperCap is > 0m && price > band.UpperCap)
            return (band.UpperCap.Value,
                $"Limit clamped down from {price:F2} to the day's Upper Cap {band.UpperCap:F2}.");

        if (band.LowerLock is > 0m && price < band.LowerLock)
            return (band.LowerLock.Value,
                $"Limit clamped up from {price:F2} to the day's Lower Lock {band.LowerLock:F2}.");

        return (price, null);
    }

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
        // "no session" is both honest and the only safe answer. The separate session worker owns
        // recovery under a global cooldown, and triggers an immediate pass after it succeeds.
        if (!_portal.HasSession)
        {
            var recovery = _portal.NextLoginAttemptUtc is { } retry && retry > DateTime.UtcNow
                ? $" Background recovery is active; the broker-safe login cooldown lasts until {retry:u}."
                : _portal.AutomaticRecoveryArmed
                    ? " Background recovery is active and will retry without requiring a dashboard action."
                    : " Recovery will start automatically after a broker session is first requested.";
            return new BrokerReconciliationSnapshot(
                Supported: true,
                Healthy: false,
                Reason: "No direct broker API session is established, so the account's state could not be read. " +
                        "An authenticated browser can exist separately. Session maintenance runs independently; " +
                        "the periodic reconciliation check itself never triggers a login." + recovery,
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
        if (outstanding.Ok && outstanding.Orders.Any(o => string.IsNullOrWhiteSpace(o.OrderNo)))
            failures.Add("an outstanding order was missing its broker order number");
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

        var structuredOpenOrders = outstanding.Ok
            ? outstanding.Orders
                .Where(o => !string.IsNullOrWhiteSpace(o.OrderNo))
                .Select(o => new BrokerWorkingOrder(
                    o.OrderNo!.Trim(),
                    o.Scrip?.Trim().ToUpperInvariant() ?? "",
                    o.Type?.Trim(),
                    o.Remaining,
                    o.Price))
                .ToList()
            : [];

        var structuredOrderEvents = activity?
            .Where(row => !string.IsNullOrWhiteSpace(row.OrderNo))
            .Select(row => new BrokerOrderEvent(
                row.OrderNo!.Trim(),
                row.Scrip?.Trim().ToUpperInvariant() ?? "",
                row.Type?.Trim(),
                row.Action?.Trim().ToUpperInvariant(),
                ActivityQuantity(row),
                row.Price,
                ParseActivityTimeUtc(row.Time, checkedUtc)))
            .ToList() ?? [];

        var structuredPositions = holdings?
            .Where(h => !string.IsNullOrWhiteSpace(h.Symbol) && h.QuantityTotal is not null)
            .Select(h => new BrokerPosition(
                h.Symbol!.Trim().ToUpperInvariant(), h.QuantityTotal!.Value))
            .ToList() ?? [];

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
            {
                Fills = structuredFills,
                OpenOrders = structuredOpenOrders,
                OrderEvents = structuredOrderEvents,
                Positions = structuredPositions,
                AvailableCashPkr = balance
            };
        }

        return new BrokerReconciliationSnapshot(
            Supported: true,
            Healthy: true,
            Reason: $"Read {outstanding.Orders.Count} resting order(s), {fills.Count} fill(s) today, " +
                    $"{holdings!.Count} holding(s) and the cash balance from the broker.",
            CheckedUtc: checkedUtc,
            DetailsJson: details)
        {
            Fills = structuredFills,
            OpenOrders = structuredOpenOrders,
            OrderEvents = structuredOrderEvents,
            Positions = structuredPositions,
            AvailableCashPkr = balance
        };
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

    private static int? ActivityQuantity(AhkActivityLogEntry row)
    {
        var quantity = row.Value ?? row.TotalVolume ?? row.Remaining;
        return quantity is > 0m
            ? (int)Math.Round(quantity.Value, MidpointRounding.AwayFromZero)
            : null;
    }

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
