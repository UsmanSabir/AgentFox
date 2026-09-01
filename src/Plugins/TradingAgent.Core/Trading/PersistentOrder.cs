namespace TradingAgent.Trading;

using TradingAgent.Reconciliation;

/// <summary>
/// A local good-until-expiry intent. The venue only accepts DAY orders, so one native order is
/// materialised per trading date until the requested quantity has filled or the intent expires.
/// </summary>
public sealed record PersistentOrderIntent
{
    public required string IntentId { get; init; }
    public required string Symbol { get; init; }
    public required string Action { get; init; }       // BUY | SELL
    public required int Quantity { get; init; }
    public required string OrderType { get; init; }    // LIMIT | STOPLOSS
    public decimal? Price { get; init; }
    public decimal? LimitPrice { get; init; }

    /// <summary>
    /// The inclusive lifetime of the local intent. Once reached, no new order may be submitted and
    /// any still-resting native order must be cancelled and verified before the intent is terminal.
    /// </summary>
    public required DateTime ExpiresUtc { get; init; }

    /// <summary>
    /// active | placing | resting | partial | expiring | cancelling | attention | fulfilled | expired | cancelled
    /// </summary>
    public string State { get; init; } = "active";

    /// <summary>Cumulative quantity filled by this intent's exact broker order numbers.</summary>
    public int FilledQuantity { get; init; }

    /// <summary>Trading date on which a placement was last claimed.</summary>
    public DateOnly? LastAttemptSessionDate { get; init; }

    public int AttemptCount { get; init; }
    public string? LastOrderNo { get; init; }
    public string? SourceArmedId { get; init; }
    public string? StateReason { get; init; }
    public string? Note { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? TerminalUtc { get; init; }

    /// <summary>
    /// A person created this standing instruction — a dashboard order kept working, or an armed order
    /// they armed themselves handing over at fire time — rather than a strategy creating it.
    ///
    /// <para>
    /// Only a manual-only symbol reads it: that flag stops a strategy or plan originating an order,
    /// and leaves the operator's own instructions working. Defaults to FALSE, so an intent that has
    /// not claimed origination is treated as automation. See
    /// <see cref="TradingAgent.Watchlist.ArmedOrder.OperatorOriginated"/>.
    /// </para>
    /// </summary>
    public bool OperatorOriginated { get; init; }

    public int RemainingQuantity => Math.Max(0, Quantity - FilledQuantity);
    public bool IsTerminal => State is "fulfilled" or "expired" or "cancelled";

    public TradingAgent.Models.TradingSignal ToSignal(int quantity) => new()
    {
        IsSignal = true,
        Action = Action,
        Symbol = Symbol,
        Quantity = quantity,
        OrderType = OrderType,
        EntryPrice = Price,
        LimitPrice = LimitPrice,
        Confidence = "HIGH",
        PreservePriceIntent = true,
        RawMessage = $"persistent-order:{IntentId}"
    };
}

/// <summary>One native DAY-order attempt made for a persistent intent.</summary>
public sealed record PersistentOrderPlacement
{
    public required string PlacementId { get; init; }
    public required string IntentId { get; init; }
    public required DateOnly SessionDate { get; init; }
    public required int Attempt { get; init; }
    public required int Quantity { get; init; }
    public string? BrokerOrderNo { get; init; }
    public string? ExecutionId { get; init; }
    public string State { get; init; } = "accepted"; // accepted | lapsed | failed | unknown
    public decimal? RequestedPrice { get; init; }
    public decimal? SubmittedPrice { get; init; }
    public string? Message { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}

public readonly record struct PersistentOrderAttemptClaim(bool Acquired, int Attempt);

/// <summary>Pure decisions shared by the worker and table tests.</summary>
/// <summary>
/// A broker order identified as an intent's own, and the row that would record it. See
/// <see cref="PersistentOrderDecisions.PlanAdoption"/>.
/// </summary>
public sealed record PersistentOrderAdoption(PersistentOrderPlacement Placement, string Reason);

public static class PersistentOrderDecisions
{
    public static string? ValidateEligibility(string? orderType) =>
        (orderType ?? "").Trim().ToUpperInvariant() switch
        {
            "LIMIT" or "STOPLOSS" => null,
            "MARKET" => "Market orders are one-shot and cannot be re-placed automatically.",
            _ => "Only LIMIT and STOPLOSS orders can be kept working across trading days."
        };

    public static int QuantityToPlace(PersistentOrderIntent intent, int filled, int? availableToSell = null)
    {
        var remaining = Math.Max(0, intent.Quantity - Math.Max(0, filled));
        if (intent.Action.Equals("SELL", StringComparison.OrdinalIgnoreCase)
            && availableToSell is { } available)
            return Math.Min(remaining, Math.Max(0, available));
        return remaining;
    }

    public static bool MayAttempt(
        PersistentOrderIntent intent,
        DateTime nowUtc,
        DateOnly sessionDate,
        bool ownOrderIsResting,
        out string reason)
    {
        if (intent.IsTerminal)
        {
            reason = $"Intent is terminal ({intent.State}).";
            return false;
        }

        if (intent.State is "attention" or "expiring")
        {
            reason = $"Intent needs reconciliation ({intent.State}).";
            return false;
        }

        if (nowUtc >= intent.ExpiresUtc)
        {
            reason = $"Intent expired at {intent.ExpiresUtc:u}.";
            return false;
        }

        if (intent.RemainingQuantity <= 0)
        {
            reason = "The requested quantity is fully filled.";
            return false;
        }

        if (ownOrderIsResting)
        {
            reason = "A native order for this intent is still resting at the broker.";
            return false;
        }

        if (intent.LastAttemptSessionDate == sessionDate)
        {
            reason = $"A placement was already attempted for {sessionDate:yyyy-MM-dd}.";
            return false;
        }

        reason = $"{intent.RemainingQuantity} share(s) remain and no order is resting.";
        return true;
    }

    /// <summary>
    /// An accepted order from a prior date is not proof of non-fill merely because DAY orders are no
    /// longer visible. If the process missed that close, automatic replacement could duplicate a fill.
    /// </summary>
    public static bool PriorOutcomeWasNotObserved(
        PersistentOrderIntent intent,
        PersistentOrderPlacement? latestPlacement,
        DateOnly today) =>
        intent.LastAttemptSessionDate is { } priorDate
        && priorDate < today
        && latestPlacement?.State == "accepted";

    public static bool CanRetryFailedToday(
        PersistentOrderIntent intent,
        PersistentOrderPlacement? latestPlacement,
        DateTime nowUtc,
        DateOnly today,
        out string reason)
    {
        if (intent.IsTerminal || intent.State is not ("active" or "partial"))
        {
            reason = $"The intent is {intent.State}, so it cannot be retried.";
            return false;
        }

        if (nowUtc >= intent.ExpiresUtc || intent.RemainingQuantity <= 0)
        {
            reason = nowUtc >= intent.ExpiresUtc
                ? "The intent has expired."
                : "The requested quantity is already filled.";
            return false;
        }

        if (intent.LastAttemptSessionDate != today
            || latestPlacement?.SessionDate != today
            || !string.Equals(latestPlacement.State, "failed", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Only the latest definitively failed attempt from today can be retried.";
            return false;
        }

        reason = "The latest attempt definitively failed and is eligible for a broker check and retry.";
        return true;
    }

    /// <summary>
    /// Returns conservative evidence that the supposedly failed order may already exist at the broker.
    /// The caller must stop rather than duplicate it. Fills are restricted to the failed attempt's time
    /// window so an unrelated earlier trade in the same symbol does not block a retry all day.
    /// </summary>
    public static string? FindPossibleBrokerMatch(
        PersistentOrderIntent intent,
        PersistentOrderPlacement latestPlacement,
        int quantity,
        BrokerReconciliationSnapshot snapshot)
    {
        var open = snapshot.OpenOrders.FirstOrDefault(order =>
            SameSymbolAndSide(intent, order.Symbol, order.Side)
            && CompatiblePrice(intent, latestPlacement, order.Price)
            && CompatibleQuantity(order.RemainingQuantity, quantity));
        if (open is not null)
            return $"Today's outstanding book contains possibly matching broker order #{open.OrderNo}.";

        var notBeforeUtc = latestPlacement.CreatedUtc.AddMinutes(-1);
        var activeEvent = snapshot.OrderEvents
            .Where(row => row.ObservedUtc >= notBeforeUtc
                       && SameSymbolAndSide(intent, row.Symbol, row.Side)
                       && CompatiblePrice(intent, latestPlacement, row.Price)
                       && CompatibleQuantity(row.Quantity, quantity))
            .GroupBy(row => row.OrderNo, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(row => row.ObservedUtc).Last())
            .FirstOrDefault(row => !IsDeadAction(row.Action));
        if (activeEvent is not null)
            return $"Today's broker activity contains possibly matching order #{activeEvent.OrderNo} "
                 + $"in state {activeEvent.Action ?? "unknown"}.";

        var fill = snapshot.Fills.FirstOrDefault(row =>
            row.FilledUtc >= notBeforeUtc
            && SameSymbolAndSide(intent, row.Symbol, row.Side)
            && CompatiblePrice(intent, latestPlacement, row.Price)
            && row.Quantity > 0
            && row.Quantity <= quantity);
        return fill is null
            ? null
            : $"Today's broker activity contains a possibly matching fill for order #{fill.OrderNo}.";
    }

    private static bool SameSymbolAndSide(PersistentOrderIntent intent, string? symbol, string? side)
    {
        if (!string.Equals(intent.Symbol.Trim(), symbol?.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        var brokerSide = side?.Trim().ToUpperInvariant();
        return intent.Action.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? brokerSide == "BUY"
            : brokerSide is "SEL" or "SELL";
    }

    /// <summary>
    /// Resting broker orders that look like this intent's and that NO placement row claims.
    ///
    /// <para>
    /// <b>The failure this exists for — an orphan.</b> Cancel, expiry and reconciliation all work from
    /// the order numbers written into placement rows. If the process dies between the broker accepting an
    /// order and that row being written, the order exists at the venue and NOTHING points at it. Measured
    /// 2026-09-01: a persistent SELL of 50 SYS rested as <c>0411XK63</c> from 10:30:58 while the only
    /// number on record, from an earlier attempt, named an order that no longer existed. The operator's
    /// cancel was refused (<c>Invalid Order[...] to cancel</c>), and the live order went on committing
    /// the whole holding so every further sell was refused.
    /// </para>
    ///
    /// <para>
    /// <b>Shape is not identity, so this NEVER concludes ownership on its own.</b> It answers "what is
    /// resting that we have not accounted for", and the caller supplies the second half of the argument:
    /// that an order of ours is known to be unaccounted for (a claim whose outcome was never recorded).
    /// One candidate plus one unexplained submission is evidence; a candidate alone is not, and two
    /// candidates are never resolved by guessing — the same rule as <c>BrokerChargeKey.Resolve</c>.
    /// </para>
    /// </summary>
    public static IReadOnlyList<BrokerWorkingOrder> FindUnclaimedBrokerOrders(
        PersistentOrderIntent intent,
        IReadOnlyList<PersistentOrderPlacement> placements,
        BrokerReconciliationSnapshot snapshot)
    {
        var claimed = placements
            .Select(placement => placement.BrokerOrderNo?.Trim())
            .Where(number => !string.IsNullOrWhiteSpace(number))
            .Select(number => number!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The latest placement is the best available price/quantity reference; without one, the intent's
        // own figures are used. A missing placement is the orphan case itself, so it must not exclude.
        var reference = placements.LastOrDefault() ?? new PersistentOrderPlacement
        {
            PlacementId = "",
            IntentId = intent.IntentId,
            SessionDate = intent.LastAttemptSessionDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            Attempt = 0,
            Quantity = intent.Quantity,
            State = "unknown",
            RequestedPrice = intent.Price
        };

        return snapshot.OpenOrders
            .Where(order => !claimed.Contains(order.OrderNo.Trim())
                            && SameSymbolAndSide(intent, order.Symbol, order.Side)
                            && CompatiblePrice(intent, reference, order.Price)
                            && CompatibleQuantity(order.RemainingQuantity, intent.Quantity))
            .ToList();
    }

    /// <summary>
    /// Whether this intent has an order of its own that the ledger cannot name — the precondition that
    /// turns a shape match into evidence rather than a coincidence.
    ///
    /// <para>
    /// True when a placement was claimed and its outcome never recorded (<c>placing</c>), or the latest
    /// placement reached the broker with an unknown outcome and no order number. In both cases we know a
    /// submission left this process and do not know what became of it.
    /// </para>
    /// </summary>
    public static bool HasUnaccountedSubmission(
        PersistentOrderIntent intent, IReadOnlyList<PersistentOrderPlacement> placements)
    {
        if (string.Equals(intent.State, "placing", StringComparison.OrdinalIgnoreCase)) return true;

        var latest = placements.LastOrDefault();
        return latest is not null
               && string.IsNullOrWhiteSpace(latest.BrokerOrderNo)
               && string.Equals(latest.State, "unknown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The whole adoption decision, as a pure function: whether an unaccounted-for broker order can be
    /// identified as this intent's, and the exact placement row that would record it.
    ///
    /// <para>
    /// <b>Pure so that it is testable at all.</b> This lived inside the worker, where exercising it needs
    /// a repository, a broker snapshot reader, a calendar and an activity log — so the one branch that
    /// decides whether to claim a live broker order had no test, only its ingredients did. It fires only
    /// when a specific fault has already happened, which means production is the worst possible place to
    /// discover it is wrong. Same reasoning as CLAUDE.md invariant 3 for the strategy gates.
    /// </para>
    ///
    /// <para>
    /// Returns null unless BOTH halves of the argument hold: this intent is known to have submitted
    /// something whose outcome was never recorded, and exactly one resting order matches it that no
    /// placement claims. Ambiguity is never resolved by picking one.
    /// </para>
    /// </summary>
    public static PersistentOrderAdoption? PlanAdoption(
        PersistentOrderIntent intent,
        IReadOnlyList<PersistentOrderPlacement> placements,
        BrokerReconciliationSnapshot snapshot,
        DateOnly today)
    {
        if (!snapshot.Supported || !snapshot.Healthy) return null;
        if (!HasUnaccountedSubmission(intent, placements)) return null;

        var unclaimed = FindUnclaimedBrokerOrders(intent, placements, snapshot);
        if (unclaimed.Count != 1) return null;

        var adopted = unclaimed[0];
        var orderNo = adopted.OrderNo.Trim();
        if (orderNo.Length == 0) return null;

        var quantity = adopted.RemainingQuantity is { } remaining && remaining > 0
            ? (int)Math.Min(remaining, intent.Quantity)
            : intent.RemainingQuantity;
        var reason =
            $"Adopted broker order #{orderNo} for {intent.Symbol}: this intent submitted an order whose "
            + "outcome was never recorded, and exactly one resting order matches it that no placement "
            + "claims. Matched by symbol, side, price and quantity — NOT reported by the broker as ours "
            + "— so it is an inference, and the order can now be cancelled or reconciled by number.";

        return new PersistentOrderAdoption(
            new PersistentOrderPlacement
            {
                PlacementId = Guid.NewGuid().ToString("N"),
                IntentId = intent.IntentId,
                SessionDate = intent.LastAttemptSessionDate ?? today,
                Attempt = Math.Max(1, intent.AttemptCount),
                Quantity = quantity,
                BrokerOrderNo = orderNo,
                State = "accepted",
                RequestedPrice = intent.Price,
                SubmittedPrice = adopted.Price,
                Message = reason
            },
            reason);
    }

    private static bool CompatiblePrice(
        PersistentOrderIntent intent,
        PersistentOrderPlacement placement,
        decimal? brokerPrice)
    {
        if (brokerPrice is null) return true; // unknown cannot safely prove a mismatch

        return new[] { placement.SubmittedPrice, placement.RequestedPrice, intent.Price, intent.LimitPrice }
            .Where(price => price is > 0m)
            .Any(price => Math.Abs(price!.Value - brokerPrice.Value) <= 0.01m);
    }

    private static bool CompatibleQuantity(long? brokerRemaining, int intendedQuantity) =>
        brokerRemaining is null || brokerRemaining is > 0 && brokerRemaining <= intendedQuantity;

    private static bool CompatibleQuantity(int? brokerQuantity, int intendedQuantity) =>
        brokerQuantity is null || brokerQuantity is > 0 && brokerQuantity <= intendedQuantity;

    private static bool IsDeadAction(string? action) =>
        string.Equals(action, "REJ", StringComparison.OrdinalIgnoreCase)
        || string.Equals(action, "CLX", StringComparison.OrdinalIgnoreCase);
}
