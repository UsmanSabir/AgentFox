namespace TradingAgent.Watchlist;

/// <summary>
/// A standing intent to keep a position protected at a level — not a queued order.
///
/// <para>
/// The distinction is forced by the venue. PSX clears outstanding orders at the close, so a native
/// stop placed today does not exist tomorrow while the risk plainly does. A one-shot child order
/// would therefore protect the position for exactly one session and then quietly stop, which is the
/// worst kind of failure: protection that reads as present and is not. So the durable thing is the
/// <i>intent</i>, and a native day order is re-materialised from it every session until the position
/// is gone.
/// </para>
///
/// <para>
/// <b>It never exists before the shares do.</b> A stop is created in <c>pending_fill</c> and only
/// becomes <c>active</c> once an increase in holdings proves the entry actually executed. Selling
/// stock you do not own is a rejection at best and a short at worst, and neither is protection.
/// </para>
/// </summary>
public sealed record ProtectiveStop
{
    public required string StopId { get; init; }
    public required string Symbol { get; init; }

    /// <summary>The armed entry this protects, when it came from one. Null for a bare holding.</summary>
    public string? ParentArmedId { get; init; }

    /// <summary>
    /// The execution id of an IMMEDIATE entry this protects - the "Protect this BUY with a stop"
    /// option on an ordinary dashboard buy, which has no armed order to be a parent.
    ///
    /// <para>
    /// <b>Why a third parent kind rather than reusing the baseline.</b> An armed entry's fill is proved
    /// by holdings RISING above a baseline captured while the entry still sat unfired. An immediate
    /// order has no such window: by the time anything could read a baseline the order may already have
    /// filled, and a baseline captured too late reads equal to the holding for ever - the fill is never
    /// detected and the stop never activates. That failure is silent and runs in the UNPROTECTED
    /// direction, so it is not an acceptable trade for one fewer column.
    /// </para>
    ///
    /// <para>
    /// An immediate order needs no baseline, because it has something better: the fills recorded against
    /// its own <c>broker_orders</c> rows. That is the same mechanism
    /// <see cref="TradingAgent.Trading.PersistentOrderIntent"/> already trusts for its filled quantity.
    /// No fills recorded means no activation AND a visible "the entry never filled" close, rather than a
    /// stop that waits silently for ever.
    /// </para>
    ///
    /// <para>
    /// <b>The EXECUTION id, deliberately, not the broker order number.</b> A broker order number is not
    /// an identity here - AHL reuses one across connections, and a colliding number is stored qualified
    /// as <c>{orderNo}#{clientOrderId}</c> (see <c>SqliteTradingRepository.RecordBrokerOrdersAsync</c>).
    /// Summing fills by order number would therefore either MISS this order's fills (an exact match
    /// against a qualified row) or ADD another order's (a prefix match), and the second sizes a real
    /// sell order against shares belonging to a different position. The execution id is ours, unique by
    /// construction, and known at the moment the stop is created.
    /// </para>
    ///
    /// <para>
    /// Mutually exclusive with <see cref="ParentArmedId"/> and
    /// <see cref="ParentPersistentIntentId"/>; all three null means a bare holding, which is created
    /// <c>active</c> and never watched for a fill at all.
    /// </para>
    /// </summary>
    public string? ParentExecutionId { get; init; }

    /// <summary>
    /// The keep-working intent this protects, when the entry was an immediate order with "keep the
    /// unfilled remainder working" on. A persistent intent re-places a fresh DAY order every session
    /// under a NEW number, so no single execution can account for its fills - the intent's own
    /// cumulative <c>FilledQuantity</c> can, and does.
    /// </summary>
    public string? ParentPersistentIntentId { get; init; }

    /// <summary>Price at which the stop triggers.</summary>
    public required decimal StopTrigger { get; init; }

    /// <summary>Limit the triggered stop goes in at — at or below the trigger, or it cannot fill.</summary>
    public required decimal StopLimit { get; init; }

    /// <summary>
    /// Shares to protect. Zero until a fill confirms, then the confirmed quantity — raised as further
    /// fills land, so a partially-filled entry is protected for what is actually owned rather than
    /// left bare until the rest arrives.
    /// </summary>
    public int DesiredQuantity { get; init; }

    /// <summary>
    /// Re-place the native stop every session. On by default: a stop that survives one day and then
    /// silently lapses is the failure this whole type exists to prevent. Off makes it a single-session
    /// stop, which is a deliberate choice rather than an accident.
    /// </summary>
    public bool Recurring { get; init; } = true;

    /// <summary>
    /// <c>pending_fill</c> | <c>active</c> | <c>superseded_pending_cancel</c> | <c>closed</c>.
    ///
    /// <para>
    /// <c>superseded_pending_cancel</c> sits between <c>active</c> and <c>closed</c>: a newer stop
    /// (see <see cref="SupersedesStopId"/> on that newer row) has been confirmed resting at the
    /// broker in this one's place, but this row's own native order has not yet been verified cancelled.
    /// It stays in this state — retried every pass — until the cancel is confirmed, so a network
    /// failure or crash between "new stop placed" and "old stop cancelled" never gets silently
    /// dropped. See <c>ProtectiveStopWorker.RetireSupersededAsync</c>.
    /// </para>
    /// </summary>
    public string State { get; init; } = "pending_fill";

    /// <summary>
    /// The stop this one replaces, when it was raised (break-even, ATR trail, ...) rather than newly
    /// armed. Set once, at creation, by whoever raised it — never by the worker.
    ///
    /// <para>
    /// This is deliberately a hand-off, not an action: the writer must never cancel or close the
    /// predecessor itself, because it has no way to confirm the new stop actually reached the broker
    /// first. <c>ProtectiveStopWorker</c> owns the whole supersede lifecycle — it moves the
    /// predecessor to <c>superseded_pending_cancel</c> only once THIS row's native order is confirmed
    /// resting, and only then attempts to cancel the predecessor's own order. See
    /// <c>ProtectiveStopWorker.PlaceNativeStopAsync</c>.
    /// </para>
    /// </summary>
    public string? SupersedesStopId { get; init; }

    /// <summary>
    /// Holding quantity before the entry went in — the datum the fill is measured against.
    ///
    /// <para>
    /// Null means it was never captured, and that is <b>not</b> the same as zero. Defaulting it to
    /// zero would read an existing 100-share holding as a 100-share fill and place a stop for stock
    /// this entry never bought. A stop with no baseline therefore refuses to activate and says so,
    /// which is a visible gap rather than a wrong order.
    /// </para>
    /// </summary>
    public int? BaselineQuantity { get; init; }

    /// <summary>
    /// Shares covered by native placements made <i>during</i>
    /// <see cref="LastPlacedSessionDate"/> — a running total for that session, not the size of the
    /// last order. A top-up adds to it: raising coverage rests a second order for the shortfall
    /// alongside the first, which is legitimate because the broker's limit is on the QUANTITY
    /// committed to resting SELLs, not on how many orders carry it — confirmed live 2026-08-27, two
    /// stop orders resting simultaneously on one symbol.
    /// </summary>
    public int PlacedQuantity { get; init; }

    /// <summary>Session the native stop was last placed for. The primary guard against a double-place.</summary>
    public DateOnly? LastPlacedSessionDate { get; init; }

    /// <summary>Exchange order number of the last placement, when the portal gave one.</summary>
    public string? LastOrderNo { get; init; }

    /// <summary>
    /// The locally-armed SELL that covers the gaps the native stop cannot — before the first
    /// placement, and between sessions. Conditional, never parallel: see
    /// <see cref="ProtectiveStopDecisions.BackstopShouldStandDown"/>.
    /// </summary>
    public string? LocalBackstopArmedId { get; init; }

    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? FillConfirmedUtc { get; init; }
    public DateTime? ClosedUtc { get; init; }

    /// <summary>Why it is in its current state. Every terminal state must be explicable.</summary>
    public string? StateReason { get; init; }

    public string? Note { get; init; }

    /// <summary>
    /// A person set this stop up by hand — attached to an entry they armed themselves, say — rather
    /// than a strategy attaching it as part of a plan.
    ///
    /// <para>
    /// Carried for one reason: a manual-only symbol. That flag stops strategies and plans originating
    /// orders, not the operator's own standing instructions, and a stop the operator placed is one of
    /// those. Defaults to FALSE so a stop written by anything that has not claimed origination — a
    /// strategy, a row from before this column existed — stays refused on a manual-only symbol.
    /// See <see cref="ArmedOrder.OperatorOriginated"/>.
    /// </para>
    /// </summary>
    public bool OperatorOriginated { get; init; }
}

/// <summary>One row read from the broker's outstanding (resting) order book.</summary>
/// <param name="Quantity">Remaining quantity, when the grid exposed it.</param>
/// <param name="Price">Order price, when the grid exposed it. Null means the column was not found —
/// which is <i>unknown</i>, never zero.</param>
public sealed record RestingOrder(
    string Symbol,
    string? Side,
    string? OrderType,
    int? Quantity,
    decimal? Price,
    string? OrderNo,
    string Row);

/// <summary>What the holdings say about an entry that was submitted.</summary>
public enum FillOutcome
{
    /// <summary>Holdings could not be read. Not evidence of anything — explicitly not "did not fill".</summary>
    Unknown,

    /// <summary>
    /// No baseline holding was ever captured, so a delta cannot be computed at all. Needs a person:
    /// the stop stays dormant rather than guessing a size.
    /// </summary>
    NoBaseline,

    /// <summary>The entry is still resting and holdings have not moved.</summary>
    StillWaiting,

    /// <summary>Holdings rose. The shares are real.</summary>
    Filled,

    /// <summary>The entry left the book without holdings moving — it never filled.</summary>
    NeverFilled,

    /// <summary>The watch ran out of time without resolving either way.</summary>
    TimedOut
}

/// <param name="Quantity">Shares confirmed by the holdings delta; meaningful only for <see cref="FillOutcome.Filled"/>.</param>
public sealed record FillVerdict(FillOutcome Outcome, int Quantity, string Reason);

/// <summary>What the recurring pass should do with one stop this session.</summary>
public enum PlacementAction { Skip, Place, Close }

/// <summary>
/// What a raised stop must settle about the predecessor it replaces before it can be placed at all.
/// </summary>
public enum SupersedeAction
{
    /// <summary>Nothing is in the way — place normally.</summary>
    Proceed,

    /// <summary>
    /// The predecessor's native order is already gone from the book (the venue cleared it overnight,
    /// it was cancelled elsewhere, or it never existed), so the predecessor row can be retired now and
    /// the replacement placed against a clean book.
    /// </summary>
    RetirePredecessorFirst,

    /// <summary>
    /// The predecessor's order still holds the shares and there is room to act now: cancel it (verified
    /// against the book), then place the replacement. Break-before-make, deliberately, because
    /// make-before-break is impossible at a broker that sizes SELLs against free quantity.
    ///
    /// <para>
    /// The caller must have the position covered by something else before it cancels — see
    /// <c>ProtectiveStopWorker</c>, which arms the local backstop first. The gap between the two orders
    /// is the one moment no native stop rests, and it is why this action exists as a named decision
    /// rather than as a side effect of a placement failure.
    /// </para>
    /// </summary>
    CancelPredecessorThenPlace,

    /// <summary>
    /// The predecessor's order is still resting and still holds the shares, and now is not the moment to
    /// cancel it. The replacement waits; the position stays covered at the old level meanwhile.
    /// </summary>
    Wait
}

public sealed record SupersedeDecision(SupersedeAction Action, string Reason);

public sealed record PlacementDecision(PlacementAction Action, int Quantity, string Reason);

/// <summary>
/// The rules that decide whether shares exist and whether a native stop should go in. Pure, because
/// every one of them is a rule you would otherwise only discover was wrong by looking at a filled
/// order you did not intend.
/// </summary>
public static class ProtectiveStopDecisions
{
    /// <summary>
    /// A placed price is compared to the requested trigger with a tolerance, because the portal
    /// re-clamps every order into that day's price band — the resting order's price is routinely a
    /// little off the one asked for, and an exact match would conclude "not mine" and place a second.
    /// </summary>
    private const decimal PriceMatchTolerance = 0.02m;   // 2%

    /// <summary>
    /// Reads the holdings delta for an entry that has been submitted.
    ///
    /// <para>
    /// <paramref name="heldNow"/> is nullable on purpose and null means <b>unknown</b>. Treating an
    /// unreadable holdings grid as zero would conclude the entry never filled and close a stop that
    /// is protecting a real position — the exact inversion that must never happen, so it gets its own
    /// outcome instead of a default.
    /// </para>
    /// </summary>
    public static FillVerdict EvaluateFill(
        ProtectiveStop stop,
        decimal? heldNow,
        bool entryStillResting,
        bool deadlinePassed)
    {
        if (stop.BaselineQuantity is not { } baseline)
            return new FillVerdict(FillOutcome.NoBaseline, 0,
                "No holding was recorded before the entry went in, so a fill cannot be measured. "
                + "Place the stop manually, or disarm it.");

        if (heldNow is not { } held)
            return new FillVerdict(FillOutcome.Unknown, 0,
                "Holdings could not be read this pass; the fill is undetermined.");

        var delta = (int)Math.Floor(held) - baseline;

        if (delta > 0)
            return new FillVerdict(FillOutcome.Filled, delta,
                $"Holding rose from {baseline} to {held} — {delta} share(s) filled.");

        if (delta < 0)
            // The position shrank while waiting on an entry. Something outside this system sold, so
            // the size this stop was sized against no longer exists and guessing a new one would be
            // inventing protection.
            return new FillVerdict(FillOutcome.NeverFilled, 0,
                $"Holding FELL from {baseline} to {held} while awaiting the entry — "
                + "the position was changed elsewhere.");

        if (!entryStillResting)
            return new FillVerdict(FillOutcome.NeverFilled, 0,
                "The entry is no longer in the outstanding book and holdings did not change — "
                + "it expired or was cancelled without filling.");

        return deadlinePassed
            ? new FillVerdict(FillOutcome.TimedOut, 0,
                "The entry is still resting, but the watch window has closed.")
            : new FillVerdict(FillOutcome.StillWaiting, 0,
                "The entry is still resting and holdings have not moved.");
    }

    /// <summary>
    /// Decides whether to place the native stop for <paramref name="today"/>.
    ///
    /// <para>
    /// <b>The bias is to do nothing.</b> Every ambiguous reading returns <see cref="PlacementAction.Skip"/>,
    /// because the two mistakes are not symmetric: a stop that failed to go in is visible in the
    /// panel and can be placed by hand, whereas a duplicate stop sells the position twice.
    /// </para>
    /// </summary>
    /// <param name="excludedOrderNumbers">
    /// Broker order numbers that must NOT be read as protection this stop can rely on — in practice,
    /// the predecessor this stop is replacing. Without it, a raise smaller than
    /// <see cref="PriceMatchTolerance"/> matches the very order it is trying to supersede and is
    /// skipped forever, because "a stop is already resting near this price" is trivially true of the
    /// order being replaced. Mirrors <c>SellQuantityRule.Available</c>'s parameter of the same name,
    /// for the same reason.
    /// </param>
    /// <param name="cancelledOrderNumbers">
    /// Orders the snapshot still lists but which are CONFIRMED cancelled during this pass. Distinct
    /// from <paramref name="excludedOrderNumbers"/> and NOT interchangeable with it: that set answers
    /// "this order is not mine", which is exactly what a sibling stop's order is — and a sibling's
    /// order still commits its shares at the venue. This one answers "this order no longer exists", and
    /// only those shares are free again. Conflating the two would size against a book with a sibling's
    /// committed shares silently added back, which is how an order that cannot fit gets sent anyway.
    /// </param>
    public static PlacementDecision DecidePlacement(
        ProtectiveStop stop,
        decimal? heldQuantity,
        DateOnly today,
        IReadOnlyList<RestingOrder> resting,
        IReadOnlySet<string>? excludedOrderNumbers = null,
        IReadOnlySet<string>? cancelledOrderNumbers = null)
    {
        if (stop.State != "active")
            return new PlacementDecision(PlacementAction.Skip, 0, $"Not active (state: {stop.State}).");

        if (heldQuantity is not { } held)
            return new PlacementDecision(PlacementAction.Skip, 0,
                "Holdings could not be read; refusing to place a stop against an unknown position.");

        if (held <= 0)
            return new PlacementDecision(PlacementAction.Close, 0,
                "The position is gone — the stop executed, or it was sold elsewhere. Nothing left to protect.");

        // Never offer more shares than are actually held, whatever the intent says.
        var quantity = Math.Min(stop.DesiredQuantity, (int)Math.Floor(held));
        if (quantity <= 0)
            return new PlacementDecision(PlacementAction.Skip, 0,
                "Nothing confirmed to protect yet.");

        // Coverage only carries within a session: the venue clears outstanding orders overnight, so
        // yesterday's placement protects nothing today.
        var coveredThisSession = stop.LastPlacedSessionDate == today ? stop.PlacedQuantity : 0;
        var shortfall = quantity - coveredThisSession;

        if (shortfall <= 0)
            return new PlacementDecision(PlacementAction.Skip, 0,
                $"Already placed for {today:yyyy-MM-dd}, covering {coveredThisSession} share(s).");

        var match = FindOwnResting(stop, resting, excludedOrderNumbers);
        if (match.Ambiguous)
            return new PlacementDecision(PlacementAction.Skip, 0,
                $"An order for {stop.Symbol} is resting but could not be identified "
                + $"({match.Reason}). Not placing a second one.");

        if (match.Order is { } mine && coveredThisSession == 0)
            // Something is resting at this level that this system did not place today — a manual
            // stop, or one of ours from a placement that was never recorded. Either way the position
            // is protected at the level asked for, and adding a second order to an unknown one is how
            // a holding gets sold twice. A predecessor being deliberately superseded is NOT this case
            // and is excluded by order number — see excludedOrderNumbers.
            return new PlacementDecision(PlacementAction.Skip, 0,
                $"A stop for {stop.Symbol} is already resting"
                + (mine.OrderNo is not null ? $" (order no {mine.OrderNo})" : "")
                + ", and this system did not place it this session. Leaving it alone.");

        // ── What the venue will actually accept ─────────────────────────────────────────────────
        // Everything above sizes against CUSTODY, which is the right ceiling for "how much should be
        // protected" and the wrong one for "how much can be placed". The broker sizes every SELL
        // against custody minus shares already committed to resting SELLs, so a second stop on a
        // symbol — a sibling protecting another tranche, or this stop's own partial coverage — makes
        // the difference real. Sending the full shortfall anyway earns a rejection, and a rejection is
        // not free: the worker parks this exact order in its refusal backoff and stops retrying, so the
        // shares that COULD have been covered are left bare for the backoff's duration.
        //
        // This stop's own resting order is counted in `committed` on purpose. `shortfall` has already
        // deducted what this stop placed THIS SESSION, so the two agree in the ordinary top-up case;
        // where they disagree — a placement the ledger missed, a quantity the venue reduced — the
        // book is the side that is right, because it is the side the rejection comes from.
        // This is a CAP, never a gate. An unreadable quantity leaves the sum unknown, and unknown here
        // means "place what was asked for" rather than the Skip an unknown earns elsewhere in this
        // class — deliberately, because the two unknowns are not the same shape. Every other unknown
        // guards an irreversible act (cancelling a stop, selling a position twice); this one guards
        // only against a REJECTION, and refusing to place on it would let an unreadable grid leave a
        // position bare for as long as the grid stays unreadable. Falling through to the full shortfall
        // is exactly what this code did before the cap existed, so the cap can only ever improve on it.
        // The same reasoning is why a resting take-profit with no quantity still does not block a stop.
        var (_, committed) = CommittedSells(stop.Symbol, resting, cancelledOrderNumbers);
        var placeable = shortfall;

        if (committed is { } alreadyCommitted)
        {
            var free = (int)Math.Floor(held) - alreadyCommitted;
            if (free <= 0)
                return new PlacementDecision(PlacementAction.Skip, 0,
                    $"All {(int)Math.Floor(held)} {stop.Symbol} share(s) are already committed to "
                    + $"resting SELLs, leaving nothing free for the {shortfall} this stop still needs. "
                    + "The broker sizes every SELL against custody minus resting SELLs, so this order "
                    + "cannot be placed until some of that clears.");

            // Partial rather than nothing: covering what fits beats covering none of it, and the
            // remainder is topped up on a later pass — the same path a holding that grew already uses.
            placeable = Math.Min(shortfall, free);
        }

        return new PlacementDecision(PlacementAction.Place, placeable,
            (coveredThisSession > 0
                ? $"Holding grew to {quantity}; topping up by {placeable} share(s) on top of the "
                  + $"{coveredThisSession} already resting."
                : stop.LastPlacedSessionDate is null
                    ? $"No stop is resting for {stop.Symbol}; placing for {placeable} share(s)."
                    : $"Session rolled over (last placed {stop.LastPlacedSessionDate:yyyy-MM-dd}); "
                      + $"re-placing for {placeable} share(s).")
            + (placeable < shortfall
                ? $" Capped at the {placeable} share(s) free of resting SELLs; the remaining "
                  + $"{shortfall - placeable} is topped up once they clear."
                : ""));
    }

    /// <summary>
    /// How many shares the native stop resting for TODAY should cover, when it currently covers more
    /// than is actually held — a manual sell at the broker's own client, a partial exit executed
    /// outside this system, anything that shrank custody after the stop went in. Null means leave it
    /// alone.
    ///
    /// <para>
    /// <see cref="DecidePlacement"/> already caps a NEW placement at <c>Min(DesiredQuantity, held)</c>,
    /// but it only ever ADDS coverage — a session's <c>shortfall</c> going negative just reads as
    /// "already placed" and stops there, leaving an oversized order resting with nothing said about
    /// it. This function is the other half: it says whether the resting order itself is now wrong AND
    /// what it should be, so <see cref="ProtectiveStopWorker"/> can correct it the same way a
    /// break-even or ATR raise already gets corrected — a same-price successor sized to what remains,
    /// cancelled into and placed via <see cref="DecideSupersede"/>'s make-before-break sequencing. It
    /// returns the ceiling rather than a bool specifically so the caller cannot compute a different
    /// one from the same inputs.
    /// </para>
    ///
    /// <para>
    /// <b>The case this must refuse.</b> A stop of ours that TRIGGERED and part-filled looks identical to
    /// an external sell from custody alone: hold 100 with a stop resting for 100, 60 fill, and custody
    /// reads 40 against a <see cref="ProtectiveStop.PlacedQuantity"/> of 100. Nothing is oversized — the
    /// venue already reduced the order — and "correcting" it would cancel the remainder of a stop that is
    /// actively executing, in exactly the market this stop exists for.
    /// </para>
    ///
    /// <para>
    /// <b>They are told apart by the book's own QUANTITY, not by presence.</b> An earlier version of this
    /// compared presence alone, on the documented basis that an armed stop appeared in no broker read —
    /// which was measured wrong. CONFIRMED 2026-09-01: an armed <c>SLO</c> is listed with status
    /// <c>APT</c> and commits its full quantity as <c>PENDING SELL</c>. Presence therefore proves nothing
    /// and that version would simply never have shrunk anything. What the book still gives is the
    /// quantity the venue currently holds against this order, and comparing custody to THAT is the
    /// question actually being asked: 40 held against a book showing 100 is oversized; 40 held against a
    /// book showing 40 is a part-filled stop that the venue has already resized.
    /// </para>
    ///
    /// <para>
    /// That reasoning depends on the book reporting a part-filled order's REMAINING quantity rather than
    /// its original, and CONFIRMED 2026-09-01 it does: a SELECT order for 83 with 51 filled showed 32 in
    /// the outstanding book, 32 as <c>PENDING SELL</c>, and 32 held. All three agree while nothing is
    /// oversized, which is precisely the case this has to refuse. See
    /// <c>docs/ahl-tradecast-protocol.md</c>.
    /// </para>
    ///
    /// <para>
    /// Unknown is never zero. An unreadable holding answers null, exactly as it answers Skip in
    /// <see cref="DecidePlacement"/>; so does an unreadable outstanding book, and so does a book with a
    /// row for this symbol that cannot be attributed. An oversized stop is a known, bounded risk;
    /// cancelling one on a guess is not.
    /// </para>
    /// </summary>
    /// <param name="supersedeInFlight">
    /// Whether this row is a successor whose PREDECESSOR is still open. Not simply "this row has a
    /// <see cref="ProtectiveStop.SupersedesStopId"/>": that field is set by every raise and is never
    /// cleared afterwards, so testing it would permanently exempt every stop that has ever been lifted
    /// to break-even or trailed on ATR — which is the normal state of a managed position, and would make
    /// this whole path fire almost nowhere.
    /// </param>
    /// <param name="excludedOrderNumbers">
    /// Orders belonging to OTHER stop rows on this symbol — see
    /// <see cref="OrdersOwnedBySiblings"/>. Without it, a stop whose own order has left the book falls
    /// through to the price match and sizes <c>committed</c> from a SIBLING's order, which is a
    /// different number of shares at a different trigger.
    /// </param>
    public static int? ShrinkTo(
        ProtectiveStop stop,
        decimal? heldQuantity,
        IReadOnlyList<RestingOrder>? resting,
        bool supersedeInFlight,
        DateOnly today,
        IReadOnlySet<string>? excludedOrderNumbers = null)
    {
        if (stop.State != "active" || supersedeInFlight) return null;

        // Nothing resting for today means there is nothing sized wrong to correct — the ordinary
        // DecidePlacement path handles a fresh placement or a session rollover on its own.
        if (stop.LastPlacedSessionDate != today || stop.PlacedQuantity <= 0) return null;

        if (heldQuantity is not { } held) return null;

        // A position gone to zero is DecidePlacement's Close, not a shrink — there is nothing left to
        // resize down to.
        if (held <= 0) return null;

        if (resting is null) return null;

        var mine = FindOwnResting(stop, resting, excludedOrderNumbers);
        if (mine.Ambiguous) return null;

        // What the venue currently holds against this stop. Its own row when the book lists it, and
        // otherwise what we placed — a stop the book does not show has not been reduced by anything we
        // can see, so the placed quantity is the only figure available.
        var committed = mine.Order?.Quantity ?? stop.PlacedQuantity;
        if (committed <= 0) return null;

        // ── Custody this stop can actually claim ────────────────────────────────────────────────
        // A ceiling of Min(DesiredQuantity, held) is right for the only-stop-on-the-symbol case and
        // wrong as soon as a sibling protects another tranche of the same holding: both would shrink to
        // the WHOLE remaining custody and their orders would sum to more than exists, which the venue
        // refuses. What is left for this stop is custody minus what siblings already have resting.
        var (_, bySiblings) = CommittedSells(
            stop.Symbol,
            resting.Where(r => excludedOrderNumbers?.Contains((r.OrderNo ?? "").Trim()) ?? false).ToList());
        if (bySiblings is not { } siblingShares) return null;

        var ceiling = Math.Min(stop.DesiredQuantity, (int)Math.Floor(held) - siblingShares);

        // A ceiling at or below zero says the siblings' orders already account for every share held.
        // That is a real over-commitment, but shrinking to nothing is CLOSING this stop's protection,
        // and the worker would do it by cancelling the resting order and replacing it with one for no
        // shares at all. Leaving it alone keeps the position covered and lets the sibling's own shrink
        // settle it: whichever row is evaluated with room to spare corrects first, and the next pass
        // finds the other one fitting. Both orders converge on the holding from above rather than one
        // of them being zeroed on the way.
        if (ceiling <= 0) return null;

        return committed > ceiling ? ceiling : null;
    }

    /// <summary>
    /// Decides what a raised stop must do about the predecessor it replaces, before
    /// <see cref="DecidePlacement"/> is even asked.
    ///
    /// <para>
    /// <b>Why this exists.</b> This broker sizes every SELL against custody MINUS the quantity already
    /// committed to resting SELL orders — confirmed live on 2026-08-27, both by the rejection text
    /// ("You cannot sell more than 0 shares of SYS") and by its own <c>PendingSellQuantity</c> field.
    /// So when a predecessor stop holds the whole position, the replacement cannot be placed at all,
    /// and the make-before-break ordering the supersede design depends on is simply unavailable: the
    /// "make" step can never succeed while the "break" step has not happened. Left unhandled, the raise
    /// retries every pass forever and the position silently stays on its old trigger while the panel
    /// reports it raised.
    /// </para>
    ///
    /// <para>
    /// <b>Two stops for one symbol are fine; two stops for shares that do not exist are not.</b> Also
    /// confirmed live the same day: two <c>SLO</c> SELLs rested simultaneously on one symbol once free
    /// shares covered both. So the constraint is quantity, never symbol, and a replacement that FITS in
    /// the free quantity proceeds normally — the existing make-before-break path still applies to it.
    /// </para>
    ///
    /// <para>
    /// <b>What this deliberately does not do.</b> It never cancels anything itself — it only ever says
    /// that cancelling is now the way forward. Every unknown (an unreadable book, an unreadable holding,
    /// a resting row with no quantity) resolves to <see cref="SupersedeAction.Wait"/>, which leaves the
    /// position covered at the old level. The failure this design must never reach is zero stops
    /// resting; two is survivable, and none is not.
    /// </para>
    /// </summary>
    /// <param name="replacementWindowAllowed">
    /// Whether the caller is in a position to cancel and immediately re-place — in practice, whether the
    /// order window is open and the position has other cover for the gap. False collapses the
    /// break-before-make case to <see cref="SupersedeAction.Wait"/>, which is correct rather than merely
    /// safe: PSX clears the book at the close, so a raise that waits is placed clean at the next
    /// session without anyone cancelling anything.
    /// </param>
    public static SupersedeDecision DecideSupersede(
        ProtectiveStop successor,
        ProtectiveStop? predecessor,
        decimal? heldQuantity,
        IReadOnlyList<RestingOrder>? resting,
        bool replacementWindowAllowed = false)
    {
        if (successor.SupersedesStopId is not { Length: > 0 })
            return new(SupersedeAction.Proceed, "This stop replaces nothing.");

        if (predecessor is null || predecessor.State is "closed")
            return new(SupersedeAction.Proceed,
                "The stop this one replaces is already closed; nothing is holding the shares.");

        if (resting is null)
            return new(SupersedeAction.Wait,
                "The outstanding book could not be read, so whether the previous stop still holds the "
                + "shares is unknown. The position stays covered at the old level.");

        // Order number AND symbol. CONFIRMED live 2026-08-28: this broker's order numbers are only
        // unique within a connection — the format is {connection}11XK{seq}, and a fresh connection
        // restarts the sequence — so one number names different orders on different symbols. A real
        // capture had `0411XK1` as both a MARI BUY and a PAEL stop on the same day. Matching on the
        // number alone would read someone else's live order as this stop's own.
        var predecessorOrder = predecessor.LastOrderNo is { Length: > 0 } no
            ? resting.FirstOrDefault(r =>
                r.Symbol.Equals(predecessor.Symbol, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.OrderNo?.Trim(), no.Trim(), StringComparison.OrdinalIgnoreCase))
            : null;

        if (predecessorOrder is null)
            return new(SupersedeAction.RetirePredecessorFirst,
                predecessor.LastOrderNo is { Length: > 0 } gone
                    ? $"The previous stop's order {gone} is no longer in the outstanding book — the "
                      + "venue cleared it at the close, or it was cancelled elsewhere. Retiring it "
                      + "now leaves the replacement a clean book to place against."
                    : "No native order was ever placed for the stop this one replaces, so nothing is "
                      + "holding the shares.");

        // The predecessor IS resting. Whether the replacement can go in alongside it comes down to
        // free quantity, and every unknown in that sum resolves to Wait.
        if (heldQuantity is not { } held)
            return new(SupersedeAction.Wait,
                "Holdings could not be read, so the free quantity behind the previous stop is unknown.");

        var (_, committedOrNull) = CommittedSells(successor.Symbol, resting);
        if (committedOrNull is not { } committed)
            return new(SupersedeAction.Wait,
                $"A resting {successor.Symbol} SELL has no readable quantity, so the free quantity "
                + "cannot be computed. Refusing to guess.");

        var free = (int)Math.Floor(held) - committed;
        var wanted = Math.Min(successor.DesiredQuantity, (int)Math.Floor(held));

        if (free >= wanted && wanted > 0)
            return new(SupersedeAction.Proceed,
                $"{free} {successor.Symbol} share(s) are free of resting SELLs, which covers the "
                + $"{wanted} this stop needs — it can rest alongside the one it replaces until that "
                + "one is retired.");

        var blocked = $"The previous stop (order {predecessor.LastOrderNo}) still holds {committed} of "
            + $"{(int)Math.Floor(held)} {successor.Symbol} share(s), leaving {free} free — not enough "
            + $"for the {wanted} this raise needs. The broker sizes every SELL against custody minus "
            + "resting SELLs, so the replacement cannot be placed while that order stands.";

        return replacementWindowAllowed
            ? new(SupersedeAction.CancelPredecessorThenPlace,
                blocked + " Cancelling it first is the only way through, so the position is covered by "
                + "the local backstop for the moment between the two orders.")
            : new(SupersedeAction.Wait,
                blocked + " The position stays protected at the old trigger, and the raise takes effect "
                + "at the next session, when the venue clears the book.");
    }

    /// <summary>
    /// Picks which protective stop, if any, should stand down so a SELL that REDUCES the position can
    /// get through — a target scale-out, most often.
    ///
    /// <para>
    /// <b>Why a reduction can be blocked at all.</b> The broker sizes every SELL against custody minus
    /// resting SELLs, so a stop covering 100% of a holding leaves nothing free for a take-profit on
    /// part of that same holding. The stop has to give up its shares first; there is no arrangement in
    /// which both rest over the whole position.
    /// </para>
    ///
    /// <para>
    /// <b>It will only ever stand down a stop this system placed and can identify by order number.</b>
    /// An order matched by price alone, or a book that could not be read, returns
    /// <see cref="StopReleaseAction.CannotRelease"/> — cancelling something unidentified to make room
    /// for a sell would be trading away protection nobody asked about.
    /// </para>
    /// </summary>
    public static StopReleaseDecision DecideRelease(
        string symbol,
        int quantityNeeded,
        decimal? heldQuantity,
        IReadOnlyList<RestingOrder>? resting,
        IReadOnlyList<ProtectiveStop> stops)
    {
        if (quantityNeeded <= 0)
            return new(StopReleaseAction.NotNeeded, null, null, "Nothing was asked for.");

        if (resting is null || heldQuantity is not { } held)
            return new(StopReleaseAction.CannotRelease, null, null,
                "Holdings or the outstanding book could not be read, so what is holding these shares "
                + "is unknown. Nothing was cancelled.");

        var (sells, committedOrNull) = CommittedSells(symbol, resting);
        if (committedOrNull is not { } committed)
            return new(StopReleaseAction.CannotRelease, null, null,
                $"A resting {symbol} SELL has no readable quantity, so the free quantity cannot be "
                + "computed. Refusing to guess.");

        var free = (int)Math.Floor(held) - committed;
        if (free >= quantityNeeded)
            return new(StopReleaseAction.NotNeeded, null, null,
                $"{free} {symbol} share(s) are already free, which covers the {quantityNeeded} needed.");

        // Only our own stops, identified the exact way — by the order number we recorded when we placed
        // it. Releasing the largest frees the most shares for one cancellation.
        var ours = stops
            .Where(s => s.State == "active"
                     && s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                     && s.LastOrderNo is { Length: > 0 })
            .Select(s => (Stop: s, Row: sells.FirstOrDefault(r => string.Equals(
                r.OrderNo?.Trim(), s.LastOrderNo!.Trim(), StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Row is not null)
            .OrderByDescending(x => x.Row!.Quantity!.Value)
            .ToList();

        if (ours.Count == 0)
            return new(StopReleaseAction.CannotRelease, null, null,
                $"{free} {symbol} share(s) are free but {quantityNeeded} are needed, and no protective "
                + "stop this system placed is holding the rest. Whatever is holding them was not placed "
                + "here, so it is left alone.");

        var chosen = ours[0];
        return new(StopReleaseAction.ReleaseStop, chosen.Stop.StopId, chosen.Stop.LastOrderNo,
            $"Only {free} {symbol} share(s) are free but {quantityNeeded} are needed. Standing down "
            + $"protective stop {chosen.Stop.StopId} (order {chosen.Stop.LastOrderNo}, "
            + $"{chosen.Row!.Quantity} share(s) at {chosen.Stop.StopTrigger}) to make room; it is "
            + "re-placed over what remains once the sell is through.");
    }

    /// <summary>
    /// Whether the local backstop must stand down because the native stop is already resting.
    ///
    /// <para>
    /// This is what keeps "native plus a local backstop" from being two stops that both fire. The
    /// backstop's job is to cover the window where the native order does not exist — before the first
    /// placement, and between sessions — not to run alongside it. An unreadable book counts as
    /// "stand down", since firing on an unknown is how a position gets sold twice.
    /// </para>
    /// </summary>
    /// <param name="excludedOrderNumbers">
    /// Orders belonging to OTHER stop rows on this symbol — see
    /// <see cref="OrdersOwnedBySiblings"/>. A sibling's native stop covers a DIFFERENT tranche, so
    /// standing down against it leaves this stop's own shares with no cover at all: no native order
    /// (<see cref="DecidePlacement"/> skipped for the same reason) and now no backstop either.
    /// </param>
    public static bool BackstopShouldStandDown(
        ProtectiveStop stop,
        IReadOnlyList<RestingOrder>? resting,
        out string reason,
        IReadOnlySet<string>? excludedOrderNumbers = null)
    {
        if (resting is null)
        {
            reason = "The outstanding book could not be read, so a resting native stop cannot be "
                   + "ruled out. Standing down rather than risk selling the position twice.";
            return true;
        }

        var match = FindOwnResting(stop, resting, excludedOrderNumbers);
        if (match.Order is { } mine)
        {
            reason = $"A native stop for {stop.Symbol} is resting"
                   + (mine.OrderNo is not null ? $" (order no {mine.OrderNo})" : "")
                   + " and will fire on its own.";
            return true;
        }

        if (match.Ambiguous)
        {
            reason = $"An unidentifiable order for {stop.Symbol} is resting ({match.Reason}). "
                   + "Standing down rather than risk selling the position twice.";
            return true;
        }

        reason = $"No native stop is resting for {stop.Symbol}; the backstop is the only protection.";
        return false;
    }

    /// <summary>
    /// The resting SELL rows for a symbol, and the quantity they commit — the figure every decision
    /// about whether an order will FIT has to be made against, because this broker sizes every SELL as
    /// custody MINUS shares already committed to resting SELLs (confirmed live 2026-08-27, both by the
    /// rejection text and by its own <c>PendingSellQuantity</c> field).
    ///
    /// <para>
    /// A null <c>Committed</c> means a row had no readable quantity, so the sum is UNKNOWN. It is never
    /// zero: every caller has to refuse rather than guess, because guessing low sends an order the
    /// venue rejects and guessing high leaves a position uncovered.
    /// </para>
    /// </summary>
    /// <param name="ignoredOrderNumbers">
    /// Orders the snapshot still lists but which are CONFIRMED gone — a predecessor cancelled during
    /// this pass, in practice. Their shares are free again however the stale book reads.
    /// </param>
    private static (IReadOnlyList<RestingOrder> Sells, int? Committed) CommittedSells(
        string symbol,
        IReadOnlyList<RestingOrder> resting,
        IReadOnlySet<string>? ignoredOrderNumbers = null)
    {
        var sells = resting
            .Where(r => r.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                     // A row positively attributable to the other side of the book commits nothing.
                     && !(r.Side is { Length: > 0 } side
                          && side.Contains("BUY", StringComparison.OrdinalIgnoreCase))
                     && !(ignoredOrderNumbers?.Contains((r.OrderNo ?? "").Trim()) ?? false))
            .ToList();

        return sells.Any(r => r.Quantity is null)
            ? (sells, null)
            : (sells, sells.Sum(r => r.Quantity!.Value));
    }

    /// <summary>
    /// Order numbers resting for this symbol that belong to a DIFFERENT protective stop row, and so
    /// must never be read as protection THIS stop can rely on.
    ///
    /// <para>
    /// <b>The failure this exists to stop.</b> Two entries on one symbol produce two independent stop
    /// rows — nothing merges them, and nothing should: they protect different share counts at
    /// different triggers. But <see cref="FindOwnResting"/>'s across-session fallback is the PRICE, and
    /// two stops on the same name routinely sit within <see cref="PriceMatchTolerance"/> of each other.
    /// So the second row reads the first row's order as "a stop is already resting here", skips
    /// forever, and never places one of its own — and because the row still reads <c>active</c>, that
    /// tranche is reported protected while nothing at the broker covers it. The local backstop does not
    /// fill the gap either: <see cref="BackstopShouldStandDown"/> matches the same way and stands down
    /// against the same order.
    /// </para>
    ///
    /// <para>
    /// <b>Only today's placements, and never this stop's own number.</b> PSX clears the book at the
    /// close and this broker's order numbers are only unique within a connection — the format is
    /// <c>{connection}11XK{seq}</c>, and a fresh connection restarts the sequence — so a sibling's
    /// number from an earlier session names an order the venue already cleared AND may well have been
    /// reissued to something live today. Excluding one on that basis could hide this stop's own resting
    /// order from it and place a duplicate, which is the one outcome worse than the bug being fixed.
    /// Hence both guards: the sibling must have placed for <paramref name="today"/>, and this stop's
    /// own <see cref="ProtectiveStop.LastOrderNo"/> is removed from the result unconditionally.
    /// </para>
    ///
    /// <para>
    /// Rows in <c>pending_fill</c> have never placed anything, and a <c>closed</c> row's number is
    /// exactly the stale kind described above, so neither contributes.
    /// </para>
    /// </summary>
    public static IReadOnlySet<string> OrdersOwnedBySiblings(
        ProtectiveStop stop,
        IReadOnlyList<ProtectiveStop> allStops,
        DateOnly today)
    {
        var siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var other in allStops)
        {
            if (other.StopId == stop.StopId) continue;
            if (!other.Symbol.Equals(stop.Symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (other.State is not ("active" or "superseded_pending_cancel")) continue;
            if (other.LastPlacedSessionDate != today) continue;
            if (other.LastOrderNo is not { Length: > 0 } no) continue;
            siblings.Add(no.Trim());
        }

        // Never hide this stop's OWN order from it, whatever a sibling has recorded. A collision here
        // is not hypothetical — see the reissued-number reasoning above — and the cost of getting it
        // wrong in this direction is a second live SELL over shares already committed.
        if (stop.LastOrderNo is { Length: > 0 } mine) siblings.Remove(mine.Trim());

        return siblings;
    }

    /// <summary>
    /// Finds this stop's own resting order among the book rows.
    ///
    /// <para>
    /// Order number is the exact key and is used first — it identifies a placement made earlier in the
    /// same session, which is how a mid-day restart avoids placing a second stop. Across sessions the
    /// number is gone with the order, so the fallback is the price: a protective stop sits near its
    /// trigger, whereas a resting take-profit SELL sits well above it. That difference is what stops
    /// an unrelated take-profit limit from blocking this stop forever — which a naive
    /// "any resting SELL means protected" test would do.
    /// </para>
    /// </summary>
    private static (RestingOrder? Order, bool Ambiguous, string Reason) FindOwnResting(
        ProtectiveStop stop,
        IReadOnlyList<RestingOrder> resting,
        IReadOnlySet<string>? excludedOrderNumbers = null)
    {
        var forSymbol = resting
            .Where(r => r.Symbol.Equals(stop.Symbol, StringComparison.OrdinalIgnoreCase)
                     && !(excludedOrderNumbers?.Contains((r.OrderNo ?? "").Trim()) ?? false))
            .ToList();

        if (forSymbol.Count == 0) return (null, false, "nothing resting");

        if (stop.LastOrderNo is { Length: > 0 } known)
        {
            var byNumber = forSymbol.FirstOrDefault(r => string.Equals(
                r.OrderNo?.Trim(), known.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byNumber is not null) return (byNumber, false, "matched by order number");
        }

        foreach (var row in forSymbol)
        {
            // A row we can positively attribute to the other side of the book is not ours, and must
            // not make us stand down.
            if (row.Side is { Length: > 0 } side
                && side.Contains("BUY", StringComparison.OrdinalIgnoreCase))
                continue;

            if (row.Price is not { } price || price <= 0)
                return (null, true, "a resting row for this symbol has no readable price");

            var drift = Math.Abs(price - stop.StopTrigger) / stop.StopTrigger;
            if (drift <= PriceMatchTolerance) return (row, false, "matched by price");
        }

        // Rows exist for the symbol but none look like this stop — a take-profit limit sitting well
        // above, most likely. That is not ambiguity, it is a different order.
        return (null, false, "resting orders for this symbol are priced away from the stop");
    }
}
