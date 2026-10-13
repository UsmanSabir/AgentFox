using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TradingAgent.Broker;
using TradingAgent.Observability;
using TradingAgent.Persistence;
using TradingAgent.Reconciliation;
using TradingAgent.Watchlist;

namespace TradingAgent.Trading;

/// <summary>A request to cancel ONE order the operator can see resting in the broker's own book.</summary>
/// <param name="OrderNo">The number the book shows for it. Either of an order's two ids is accepted.</param>
/// <param name="Symbol">
/// Required. A number alone never names an order here: short numbers repeat across symbols and across
/// days (CLAUDE.md §6a), so the symbol is what stops a stale or mistyped number cancelling somebody
/// else's position.
/// </param>
/// <param name="AcknowledgeManaged">
/// The operator has been told this order is managed by something in this system and wants it cancelled
/// anyway. Without it, a managed order is refused with the explanation, and nothing is sent.
/// </param>
public sealed record WorkingOrderCancelRequest(string? OrderNo, string? Symbol, bool AcknowledgeManaged = false);

/// <summary>Which part of this system owns a resting order, if any — and therefore what cancels it.</summary>
public enum WorkingOrderOwnerKind
{
    /// <summary>Nothing here placed it, or nothing here can prove it did. A plain broker cancel.</summary>
    None,

    /// <summary>
    /// One of our protective stops. A broker cancel alone would be undone: the recurring stop pass
    /// re-places a stop it believes is missing. So the stop is closed as well.
    /// </summary>
    ProtectiveStop,

    /// <summary>
    /// A keep-working order. Same hazard: the intent re-places on its next cycle. So the whole intent
    /// is cancelled, which is the route that already settles a vanished order against a possible fill.
    /// </summary>
    PersistentOrder,

    /// <summary>
    /// Something an edition knows about and core does not. It changes nothing about HOW the order is
    /// cancelled — it adds a sentence the operator must read before it is.
    /// </summary>
    Edition
}

/// <param name="Id">The owning row's id — a stop id or an intent id. Null for <see cref="WorkingOrderOwnerKind.None"/>.</param>
/// <param name="Consequence">What cancelling it does beyond removing the order, in the operator's words.</param>
public sealed record WorkingOrderOwner(WorkingOrderOwnerKind Kind, string? Id, string Consequence);

/// <summary>
/// The outcome of a cancel, stated as what is now TRUE rather than as whether a request succeeded.
/// </summary>
/// <param name="Outcome">
/// <c>cancelled</c> · <c>gone_possibly_filled</c> · <c>not_confirmed</c> · <c>needs_acknowledgement</c> ·
/// <c>not_resting</c> · <c>unreadable</c> · <c>invalid</c>, or a keep-working intent's own terminal state
/// (<c>fulfilled</c>, <c>attention</c>, <c>cancelling</c>) when that route decided.
/// </param>
public sealed record WorkingOrderCancelResult(
    string Outcome,
    string Message,
    string? OrderNo = null,
    string? Symbol = null,
    IReadOnlyList<WorkingOrderOwner>? Owners = null)
{
    /// <summary>The order is no longer resting. Says nothing about WHY — see <see cref="Outcome"/>.</summary>
    public bool Gone => Outcome is "cancelled" or "gone_possibly_filled" or "fulfilled";
}

/// <summary>
/// Who owns a resting order. Pure, so the rule that decides whether a cancel will simply be undone is
/// tested without a broker or a database.
/// </summary>
public static class WorkingOrderCancelRule
{
    /// <summary>
    /// The owners of <paramref name="target"/> among the open stops and open keep-working intents.
    ///
    /// <para>
    /// <b>Matched on symbol AND either of the order's ids, never on a number alone.</b> A stop records
    /// its short number at placement, and once it triggers the book leads with the long exchange id
    /// instead (measured 2026-09-22) — so a test on the primary id would stop recognising our own stop
    /// at exactly the moment it becomes a live sell, and the cancel would then be undone by the stop
    /// pass re-placing it. <see cref="BrokerWorkingOrder.Is"/> is the one rule for that.
    /// </para>
    /// </summary>
    public static IReadOnlyList<WorkingOrderOwner> Classify(
        BrokerWorkingOrder target,
        IReadOnlyList<ProtectiveStop> openStops,
        IReadOnlyList<(PersistentOrderIntent Intent, IReadOnlyList<PersistentOrderPlacement> Placements)> openIntents)
    {
        var owners = new List<WorkingOrderOwner>();

        foreach (var stop in openStops)
        {
            if (!SameSymbol(stop.Symbol, target.Symbol) || !target.Is(stop.LastOrderNo)) continue;
            owners.Add(new WorkingOrderOwner(
                WorkingOrderOwnerKind.ProtectiveStop, stop.StopId,
                $"This is the protective stop on {stop.Symbol} (trigger {stop.StopTrigger:0.####}). "
                + "Cancelling it also DISARMS the stop, so the position will have no stop until you "
                + "place a new one."));
        }

        foreach (var (intent, placements) in openIntents)
        {
            if (intent.IsTerminal || !SameSymbol(intent.Symbol, target.Symbol)) continue;
            var claims = target.Is(intent.LastOrderNo)
                         || placements.Any(placement => target.Is(placement.BrokerOrderNo));
            if (!claims) continue;
            owners.Add(new WorkingOrderOwner(
                WorkingOrderOwnerKind.PersistentOrder, intent.IntentId,
                $"This is a keep-working {intent.Action} of {intent.Quantity:N0} {intent.Symbol}. "
                + "Cancelling it ends the whole instruction, so it will NOT be placed again on a later "
                + "cycle or a later day."));
        }

        return owners;
    }

    /// <summary>
    /// Whether the cancel must stop and ask. Only a MANAGED order does: a plain order's confirmation is
    /// the button's own, and asking twice would teach the operator to click through the one that matters.
    /// </summary>
    public static bool NeedsAcknowledgement(IReadOnlyList<WorkingOrderOwner> owners, bool acknowledged) =>
        !acknowledged && owners.Count > 0;

    /// <summary>
    /// Refuses when core owners disagree about the ONE row, which it cannot safely resolve: a stop and a
    /// keep-working order naming the same broker number is a ledger defect, and cancelling through
    /// either route would leave the other believing its order is still resting.
    /// </summary>
    public static bool IsAmbiguous(IReadOnlyList<WorkingOrderOwner> owners) =>
        owners.Count(o => o.Kind is WorkingOrderOwnerKind.ProtectiveStop or WorkingOrderOwnerKind.PersistentOrder) > 1;

    private static bool SameSymbol(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Cancels one order the operator picked from the broker's own book, by the route that will actually
/// keep it cancelled.
///
/// <para>
/// <b>Why this is not just <see cref="IBrokerOrderCanceller"/>.</b> A broker cancel of an order this
/// system manages is quietly undone: the stop pass re-places a stop it believes is missing, and a
/// keep-working intent re-places on its next cycle. So ownership is checked FIRST, a managed order is
/// cancelled through its owner, and the operator is told what that owner will stop doing.
/// </para>
///
/// <para>
/// <b>Checked against a live read of the book, never the cached snapshot.</b> The operator is acting on
/// a table that may be a minute old, and the number must be resting — on the named symbol — at the
/// moment the cancel goes out. The read is published, so every other consumer sees it too.
/// </para>
///
/// <para>
/// No kill-switch gate, deliberately, for the reason <c>CancelOrderTool</c> gives: cancelling removes
/// risk, and an emergency stop that also blocked cancels would trap the account in the exposure the
/// switch was flipped to escape.
/// </para>
/// </summary>
public sealed class WorkingOrderCancellationService
{
    private readonly IBrokerStateReader _book;
    private readonly TradingReconciliationState _reconciliation;
    private readonly ITradingRepository _repository;
    private readonly IBrokerOrderCanceller _canceller;
    private readonly PersistentOrderWorker _persistentOrders;
    private readonly ILogger<WorkingOrderCancellationService> _logger;
    private readonly TradingActivityLog? _activity;

    public WorkingOrderCancellationService(
        IBrokerStateReader book,
        TradingReconciliationState reconciliation,
        ITradingRepository repository,
        IBrokerOrderCanceller canceller,
        PersistentOrderWorker persistentOrders,
        ILogger<WorkingOrderCancellationService> logger,
        TradingActivityLog? activity = null)
    {
        _book = book;
        _reconciliation = reconciliation;
        _repository = repository;
        _canceller = canceller;
        _persistentOrders = persistentOrders;
        _logger = logger;
        _activity = activity;
    }

    /// <param name="editionOwners">
    /// An edition's own knowledge of the order, consulted after core's and added to what the operator
    /// must acknowledge. It can only ADD a warning; it cannot change the route, which keeps the one rule
    /// about what undoes a cancel in one place.
    /// </param>
    public async Task<WorkingOrderCancelResult> CancelAsync(
        WorkingOrderCancelRequest request,
        string actor,
        Func<BrokerWorkingOrder, CancellationToken, Task<IReadOnlyList<WorkingOrderOwner>>>? editionOwners = null,
        CancellationToken ct = default)
    {
        var orderNo = request.OrderNo?.Trim() ?? "";
        var symbol = request.Symbol?.Trim().ToUpperInvariant() ?? "";
        if (orderNo.Length == 0 || symbol.Length == 0)
            return new("invalid", "Say which order to cancel: both its number and its symbol are needed.");

        BrokerReconciliationSnapshot snapshot;
        try
        {
            snapshot = await _book.ReadSnapshotAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            snapshot = BrokerReconciliationSnapshot.Unsupported(ex.Message);
        }

        if (!snapshot.Supported || !snapshot.Healthy)
            return new("unreadable",
                "The broker's order book could not be read just now, so nothing was cancelled: "
                + snapshot.Reason, orderNo, symbol);
        _reconciliation.Update(snapshot);

        // Symbol first, then number: a number resting on a DIFFERENT symbol is not this order.
        var target = snapshot.OpenOrders.FirstOrDefault(order =>
            string.Equals(order.Symbol.Trim(), symbol, StringComparison.OrdinalIgnoreCase) && order.Is(orderNo));
        if (target is null)
            return new("not_resting",
                $"Order #{orderNo} is no longer resting on {symbol}, so nothing was cancelled. It may "
                + "already have filled or been cancelled — refresh the portfolio to see the current book.",
                orderNo, symbol);

        var owners = new List<WorkingOrderOwner>(await CoreOwnersAsync(target, ct));
        if (editionOwners is not null)
            owners.AddRange(await editionOwners(target, ct));

        if (WorkingOrderCancelRule.IsAmbiguous(owners))
            return new("ambiguous",
                $"Order #{orderNo} is claimed by more than one record in this system, so it was not "
                + "cancelled from here — cancelling through either would leave the other believing it "
                + "still rests. Cancel it in the broker's own app, then check Protective stops and "
                + "Keep-working orders.", orderNo, symbol, owners);

        if (WorkingOrderCancelRule.NeedsAcknowledgement(owners, request.AcknowledgeManaged))
            return new("needs_acknowledgement",
                string.Join(" ", owners.Select(o => o.Consequence)), orderNo, symbol, owners);

        var managed = owners.FirstOrDefault(o =>
            o.Kind is WorkingOrderOwnerKind.ProtectiveStop or WorkingOrderOwnerKind.PersistentOrder);
        var result = managed?.Kind switch
        {
            WorkingOrderOwnerKind.ProtectiveStop => await CancelStopAsync(managed.Id!, target, ct),
            WorkingOrderOwnerKind.PersistentOrder => await CancelIntentAsync(managed.Id!, target, ct),
            _ => await CancelPlainAsync(target, ct)
        };
        result = result with { Owners = owners };

        _logger.LogWarning(
            "[Orders] {Actor} cancelled working order #{OrderNo} ({Side} {Symbol}) from the broker book: "
            + "{Outcome}. {Message}", actor, target.OrderNo, target.Side, target.Symbol, result.Outcome,
            result.Message);
        if (result.Gone)
            _activity?.Info("Orders", $"{target.Symbol}: order #{target.OrderNo} cancelled by {actor}", result.Message);
        else
            _activity?.Warn("Orders", $"{target.Symbol}: cancel of order #{target.OrderNo} not confirmed", result.Message);

        return result;
    }

    private async Task<IReadOnlyList<WorkingOrderOwner>> CoreOwnersAsync(
        BrokerWorkingOrder target, CancellationToken ct)
    {
        var stops = await _repository.GetProtectiveStopsAsync(openOnly: true, ct);

        // Placements are read only for intents on this symbol — one read per candidate, not per intent.
        var intents = new List<(PersistentOrderIntent, IReadOnlyList<PersistentOrderPlacement>)>();
        foreach (var intent in await _repository.GetPersistentOrdersAsync(openOnly: true, ct))
        {
            if (!string.Equals(intent.Symbol.Trim(), target.Symbol.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            intents.Add((intent, await _repository.GetPersistentOrderPlacementsAsync(intent.IntentId, ct)));
        }

        return WorkingOrderCancelRule.Classify(target, stops, intents);
    }

    private async Task<WorkingOrderCancelResult> CancelPlainAsync(BrokerWorkingOrder target, CancellationToken ct)
    {
        var cancelled = await _canceller.CancelOrderAsync(target.OrderNo, ct);
        return Describe(target, cancelled, afterwards: "");
    }

    /// <summary>
    /// Cancels the stop's order and CLOSES the stop — but only once the order is confirmed gone.
    ///
    /// <para>
    /// Deliberately stricter than the disarm route, which closes the row whatever the cancel said. That
    /// route's operator asked to stop MANAGING a stop; this one asked for an ORDER to be gone. Closing
    /// the row on an unconfirmed cancel would leave a live sell resting that nothing tracks any more,
    /// which is worse than both outcomes the operator could have meant.
    /// </para>
    /// </summary>
    private async Task<WorkingOrderCancelResult> CancelStopAsync(
        string stopId, BrokerWorkingOrder target, CancellationToken ct)
    {
        var cancelled = await _canceller.CancelOrderAsync(target.OrderNo, ct);
        if (!cancelled.Gone)
            return Describe(target, cancelled,
                afterwards: " The protective stop is still armed, because its order may still be resting.");

        var stop = (await _repository.GetProtectiveStopsAsync(openOnly: true, ct))
            .FirstOrDefault(s => s.StopId == stopId);
        if (stop is not null)
        {
            await _repository.TrySetProtectiveStopStateAsync(
                stopId, stop.State, "closed",
                "Its broker order was cancelled by the operator from the portfolio's working orders.", ct);
            if (stop.LocalBackstopArmedId is { } backstopId)
                await _repository.TrySetArmedOrderStateAsync(
                    backstopId, "armed", "cancelled",
                    "The protective stop it backed was cancelled by the operator.", ct: ct);
        }

        return Describe(target, cancelled,
            afterwards: " The protective stop is disarmed; place a new one if the position still needs it.");
    }

    /// <summary>
    /// Hands the cancel to the keep-working intent that owns the order. That route cancels every
    /// placement the intent has and, when an order has vanished rather than been cancelled, asks the
    /// broker whether it FILLED before writing anything terminal — which is the one question a cancel
    /// from here must never answer by assumption (MWMP, 2026-09-21).
    /// </summary>
    private async Task<WorkingOrderCancelResult> CancelIntentAsync(
        string intentId, BrokerWorkingOrder target, CancellationToken ct)
    {
        var (completed, state, message) = await _persistentOrders.CancelAsync(intentId, ct);
        var outcome = state switch
        {
            "cancelled" => "cancelled",
            "fulfilled" => "fulfilled",
            _ when completed => state,
            _ => state is "attention" ? "attention" : "not_confirmed"
        };
        return new(outcome, message, target.OrderNo, target.Symbol);
    }

    /// <summary>
    /// What a broker's cancel result means, without claiming more than it proves. A number the broker
    /// says it has no order for is GONE, but that is also what a fill looks like — so it is never
    /// reported as "cancelled".
    /// </summary>
    internal static WorkingOrderCancelResult Describe(
        BrokerWorkingOrder target, BrokerCancellationResult cancelled, string afterwards)
    {
        var what = $"{target.Side} {target.RemainingQuantity?.ToString("N0") ?? "?"} {target.Symbol}";
        if (cancelled.Gone && cancelled.RequestAccepted)
            return new("cancelled",
                $"Order #{target.OrderNo} ({what}) was cancelled and the broker confirmed it.{afterwards}",
                target.OrderNo, target.Symbol);

        if (cancelled.GoneUncancelled)
            return new("gone_possibly_filled",
                $"Order #{target.OrderNo} ({what}) is no longer resting, but the broker did not confirm "
                + "a cancellation — it may have FILLED moments ago. Check your holdings before placing "
                + $"a replacement.{afterwards}", target.OrderNo, target.Symbol);

        return new("not_confirmed",
            $"The cancel of order #{target.OrderNo} ({what}) was not confirmed: {cancelled.Message} "
            + $"Treat it as still resting and refresh before trying again.{afterwards}",
            target.OrderNo, target.Symbol);
    }
}

/// <summary>
/// The one HTTP shape for a working-order cancel, shared by core's route and any edition's, so the
/// browser reads one contract whichever route answered.
/// </summary>
public static class WorkingOrderCancelHttp
{
    /// <summary>
    /// 409 for a managed order awaiting acknowledgement, 400 for a malformed request, 200 for every
    /// broker outcome — "not confirmed" is an answer about the order, not a failure of the request, and
    /// the browser must show its sentence rather than a status code.
    /// </summary>
    public static IResult Respond(WorkingOrderCancelResult result, BrokerReconciliationWorker? reconciliation)
    {
        // Something may have left the book. The venue's own push usually refreshes the account too, but
        // only while a socket is held — this makes the portfolio's next read current either way.
        if (result.Gone) reconciliation?.RefreshSoon($"operator cancelled order #{result.OrderNo}");

        var body = new
        {
            error = result.Outcome is "needs_acknowledgement" or "invalid" ? result.Outcome : null,
            outcome = result.Outcome,
            gone = result.Gone,
            message = result.Message,
            orderNo = result.OrderNo,
            symbol = result.Symbol,
            owners = (result.Owners ?? []).Select(o => new
            {
                kind = o.Kind.ToString(),
                id = o.Id,
                consequence = o.Consequence
            })
        };

        return result.Outcome switch
        {
            "invalid" => Results.BadRequest(body),
            "needs_acknowledgement" => Results.Conflict(body),
            _ => Results.Ok(body)
        };
    }
}
