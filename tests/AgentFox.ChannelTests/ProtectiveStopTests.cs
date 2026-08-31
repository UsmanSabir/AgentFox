using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// Fill confirmation and native-stop placement for protective stops.
///
/// <para>
/// Every rule here can put a real SELL order into the market, so the tests lean on the readings where
/// acting would be WRONG: an unreadable holdings grid, a baseline that was never captured, a stop
/// already resting, a session that has not rolled. The asymmetry is the whole point — a stop that
/// failed to go in is visible and fixable by hand, whereas a duplicate stop sells the position twice.
/// </para>
/// </summary>
[TestClass]
public sealed class ProtectiveStopTests
{
    private static readonly DateOnly Today = new(2026, 8, 17);
    private static readonly DateOnly Yesterday = new(2026, 8, 16);

    // ── Fill confirmation ────────────────────────────────────────────────────

    [TestMethod]
    public void Fill_IsConfirmedByTheHoldingsDelta()
    {
        var stop = Stop(baseline: 100);

        var verdict = ProtectiveStopDecisions.EvaluateFill(
            stop, heldNow: 145m, entryStillResting: false, deadlinePassed: false);

        Assert.AreEqual(FillOutcome.Filled, verdict.Outcome, verdict.Reason);
        Assert.AreEqual(45, verdict.Quantity, "only the shares this entry added are protected");
    }

    [TestMethod]
    public void Fill_UnreadableHoldingsAreUnknown_NotZero()
    {
        // The dangerous inversion: reading "cannot see the grid" as "you hold nothing" would close a
        // stop that is protecting a real position.
        var stop = Stop(baseline: 100);

        var verdict = ProtectiveStopDecisions.EvaluateFill(
            stop, heldNow: null, entryStillResting: false, deadlinePassed: true);

        Assert.AreEqual(FillOutcome.Unknown, verdict.Outcome, verdict.Reason);
    }

    [TestMethod]
    public void Fill_WithoutABaseline_RefusesToDecide()
    {
        // A null baseline is not zero. Treating it as zero would read an existing 100-share holding
        // as a 100-share fill and place a stop for stock this entry never bought.
        var stop = Stop(baseline: null);

        var verdict = ProtectiveStopDecisions.EvaluateFill(
            stop, heldNow: 100m, entryStillResting: false, deadlinePassed: false);

        Assert.AreEqual(FillOutcome.NoBaseline, verdict.Outcome, verdict.Reason);
        Assert.AreEqual(0, verdict.Quantity);
    }

    [TestMethod]
    public void Fill_EntryGoneWithNoHoldingsChange_NeverFilled()
    {
        var stop = Stop(baseline: 100);

        var verdict = ProtectiveStopDecisions.EvaluateFill(
            stop, heldNow: 100m, entryStillResting: false, deadlinePassed: false);

        Assert.AreEqual(FillOutcome.NeverFilled, verdict.Outcome, verdict.Reason);
    }

    [TestMethod]
    public void Fill_StillRestingIsNotAVerdict()
    {
        var stop = Stop(baseline: 100);

        var verdict = ProtectiveStopDecisions.EvaluateFill(
            stop, heldNow: 100m, entryStillResting: true, deadlinePassed: false);

        Assert.AreEqual(FillOutcome.StillWaiting, verdict.Outcome, verdict.Reason);
    }

    [TestMethod]
    public void Fill_ShrinkingPositionIsNotAFill()
    {
        // Something outside this system sold. The size this stop was sized against is gone.
        var stop = Stop(baseline: 100);

        var verdict = ProtectiveStopDecisions.EvaluateFill(
            stop, heldNow: 60m, entryStillResting: true, deadlinePassed: false);

        Assert.AreEqual(FillOutcome.NeverFilled, verdict.Outcome, verdict.Reason);
    }

    // ── Placement ────────────────────────────────────────────────────────────

    [TestMethod]
    public void Placement_PlacesWhenNothingIsResting()
    {
        var stop = Active(desired: 45);

        var decision = ProtectiveStopDecisions.DecidePlacement(stop, heldQuantity: 45m, Today, []);

        Assert.AreEqual(PlacementAction.Place, decision.Action, decision.Reason);
        Assert.AreEqual(45, decision.Quantity);
    }

    [TestMethod]
    public void Placement_RePlacesAfterTheSessionRolls()
    {
        // The reason this feature exists: yesterday's order was cleared at the close, so the position
        // is unprotected this morning even though the record says it was placed.
        var stop = Active(desired: 45) with { LastPlacedSessionDate = Yesterday, PlacedQuantity = 45 };

        var decision = ProtectiveStopDecisions.DecidePlacement(stop, heldQuantity: 45m, Today, []);

        Assert.AreEqual(PlacementAction.Place, decision.Action, decision.Reason);
        Assert.AreEqual(45, decision.Quantity);
    }

    [TestMethod]
    public void Placement_DoesNotPlaceTwiceInOneSession()
    {
        var stop = Active(desired: 45) with { LastPlacedSessionDate = Today, PlacedQuantity = 45 };

        var decision = ProtectiveStopDecisions.DecidePlacement(stop, heldQuantity: 45m, Today, []);

        Assert.AreEqual(PlacementAction.Skip, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Placement_TopsUpOnlyTheShortfall()
    {
        // With no cancel available, raising coverage means resting a SECOND order for the difference.
        // Re-placing the full size would leave 30 + 75 resting against a 75-share holding.
        var stop = Active(desired: 75) with { LastPlacedSessionDate = Today, PlacedQuantity = 30 };

        var decision = ProtectiveStopDecisions.DecidePlacement(
            stop, heldQuantity: 75m, Today, [Resting(price: 550m)]);

        Assert.AreEqual(PlacementAction.Place, decision.Action, decision.Reason);
        Assert.AreEqual(45, decision.Quantity, "only the uncovered shares");
    }

    [TestMethod]
    public void Placement_NeverOffersMoreSharesThanAreHeld()
    {
        var stop = Active(desired: 45);

        var decision = ProtectiveStopDecisions.DecidePlacement(stop, heldQuantity: 20m, Today, []);

        Assert.AreEqual(PlacementAction.Place, decision.Action, decision.Reason);
        Assert.AreEqual(20, decision.Quantity);
    }

    [TestMethod]
    public void Placement_ClosesWhenThePositionIsGone()
    {
        var stop = Active(desired: 45) with { LastPlacedSessionDate = Today, PlacedQuantity = 45 };

        var decision = ProtectiveStopDecisions.DecidePlacement(stop, heldQuantity: 0m, Today, []);

        Assert.AreEqual(PlacementAction.Close, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Placement_RefusesWhenHoldingsCannotBeRead()
    {
        var stop = Active(desired: 45);

        var decision = ProtectiveStopDecisions.DecidePlacement(stop, heldQuantity: null, Today, []);

        Assert.AreEqual(PlacementAction.Skip, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Placement_LeavesAnUnrecognisedRestingStopAlone()
    {
        // A stop is resting at this level that this system did not place today — a manual one, or a
        // placement that was never recorded. Adding a second is how a holding gets sold twice.
        var stop = Active(desired: 45);

        var decision = ProtectiveStopDecisions.DecidePlacement(
            stop, heldQuantity: 45m, Today, [Resting(price: 550m)]);

        Assert.AreEqual(PlacementAction.Skip, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Placement_IsNotBlockedByAnUnrelatedTakeProfit()
    {
        // A resting take-profit SELL sits well ABOVE the stop. Treating "any resting SELL" as
        // protection would block this stop forever and leave the position genuinely uncovered.
        var stop = Active(desired: 45);

        var decision = ProtectiveStopDecisions.DecidePlacement(
            stop, heldQuantity: 45m, Today, [Resting(price: 610m)]);

        Assert.AreEqual(PlacementAction.Place, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Placement_RefusesWhenARestingRowHasNoReadablePrice()
    {
        var stop = Active(desired: 45);

        var decision = ProtectiveStopDecisions.DecidePlacement(
            stop, heldQuantity: 45m, Today, [Resting(price: null)]);

        Assert.AreEqual(PlacementAction.Skip, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Placement_RecognisesItsOwnOrderByNumber()
    {
        // The mid-session restart case: the record survived, so the order number identifies the
        // placement exactly rather than relying on price proximity.
        var stop = Active(desired: 45) with { LastOrderNo = "  A-9931 " };

        var decision = ProtectiveStopDecisions.DecidePlacement(
            stop, heldQuantity: 45m, Today, [Resting(price: 610m, orderNo: "a-9931")]);

        Assert.AreEqual(PlacementAction.Skip, decision.Action, decision.Reason);
    }

    // ── The local backstop ───────────────────────────────────────────────────

    [TestMethod]
    public void Backstop_StandsDownWhileTheNativeStopIsResting()
    {
        // Without this, "native plus a backstop" is two orders selling the same position.
        var stop = Active(desired: 45);

        var down = ProtectiveStopDecisions.BackstopShouldStandDown(
            stop, [Resting(price: 550m)], out var reason);

        Assert.IsTrue(down, reason);
    }

    [TestMethod]
    public void Backstop_FiresWhenNothingIsResting()
    {
        var stop = Active(desired: 45);

        var down = ProtectiveStopDecisions.BackstopShouldStandDown(stop, [], out var reason);

        Assert.IsFalse(down, reason);
    }

    [TestMethod]
    public void Backstop_StandsDownWhenTheBookCannotBeRead()
    {
        // An unreadable book cannot rule out a resting stop, and firing on an unknown is how a
        // position gets sold twice.
        var stop = Active(desired: 45);

        var down = ProtectiveStopDecisions.BackstopShouldStandDown(stop, null, out var reason);

        Assert.IsTrue(down, reason);
    }

    [TestMethod]
    public void Backstop_IsNotHeldBackByARestingBuy()
    {
        var stop = Active(desired: 45);

        var down = ProtectiveStopDecisions.BackstopShouldStandDown(
            stop, [Resting(price: 550m, side: "BUY")], out var reason);

        Assert.IsFalse(down, reason);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static ProtectiveStop Stop(int? baseline) => new()
    {
        StopId      = "stop-1",
        Symbol      = "FFC",
        ParentArmedId = "entry-1",
        StopTrigger = 554m,
        StopLimit   = 548m,
        BaselineQuantity = baseline
    };

    private static ProtectiveStop Active(int desired) => Stop(baseline: 0) with
    {
        State = "active",
        DesiredQuantity = desired,
        StopTrigger = 554m
    };

    private static RestingOrder Resting(
        decimal? price, string? side = null, string? orderNo = null) =>
        new("FFC", side, null, null, price, orderNo, "row");

    private static RestingOrder Sized(decimal price, int quantity, string orderNo) =>
        new("FFC", "SEL", "SLO", quantity, price, orderNo, "row");

    // ── Supersession under a free-quantity broker ────────────────────────────
    //
    // CONFIRMED live 2026-08-27 against AHL: a SELL is sized against custody MINUS the quantity
    // already committed to resting SELL orders ("You cannot sell more than 0 shares of SYS"), and two
    // stop orders DO rest simultaneously on one symbol when free shares cover both. So the constraint
    // is quantity, never symbol — and a raise over a fully committed holding is unplaceable until the
    // order it replaces is cancelled.

    private static ProtectiveStop Predecessor(string? orderNo, int desired = 100) => Stop(baseline: 0) with
    {
        StopId = "old", State = "active", DesiredQuantity = desired,
        StopTrigger = 120m, StopLimit = 118.8m, LastOrderNo = orderNo
    };

    private static ProtectiveStop Successor(int desired = 100) => Stop(baseline: 0) with
    {
        StopId = "new", State = "active", DesiredQuantity = desired,
        StopTrigger = 125m, StopLimit = 123.75m, SupersedesStopId = "old"
    };

    [TestMethod]
    public void Supersede_WaitsWhileThePredecessorStillHoldsEveryShare()
    {
        var decision = ProtectiveStopDecisions.DecideSupersede(
            Successor(), Predecessor("OLD-120"), heldQuantity: 100m,
            resting: [Sized(120m, 100, "OLD-120")]);

        Assert.AreEqual(SupersedeAction.Wait, decision.Action, decision.Reason);
        StringAssert.Contains(decision.Reason, "still holds 100 of 100");
    }

    [TestMethod]
    public void Supersede_ProceedsWhenFreeSharesCoverTheReplacement()
    {
        // 150 held, 100 committed to the predecessor, so 50 are free — and the replacement wants 50.
        // Both may rest at once; the predecessor is retired afterwards by the existing path.
        var decision = ProtectiveStopDecisions.DecideSupersede(
            Successor(desired: 50), Predecessor("OLD-120"), heldQuantity: 150m,
            resting: [Sized(120m, 100, "OLD-120")]);

        Assert.AreEqual(SupersedeAction.Proceed, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Supersede_RetiresThePredecessorOnceItsOrderHasLeftTheBook()
    {
        // The overnight path: PSX clears the book at the close, so the next session finds nothing
        // holding the shares and the raise can go in clean.
        var decision = ProtectiveStopDecisions.DecideSupersede(
            Successor(), Predecessor("OLD-120"), heldQuantity: 100m, resting: []);

        Assert.AreEqual(SupersedeAction.RetirePredecessorFirst, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Supersede_RetiresAPredecessorThatNeverPlacedAnOrder()
    {
        var decision = ProtectiveStopDecisions.DecideSupersede(
            Successor(), Predecessor(orderNo: null), heldQuantity: 100m, resting: []);

        Assert.AreEqual(SupersedeAction.RetirePredecessorFirst, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Supersede_AnUnreadableBookWaits_RatherThanAssumingTheSharesAreFree()
    {
        var decision = ProtectiveStopDecisions.DecideSupersede(
            Successor(), Predecessor("OLD-120"), heldQuantity: 100m, resting: null);

        Assert.AreEqual(SupersedeAction.Wait, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Supersede_AnUnreadableRestingQuantityWaits_RatherThanGuessingTheFreeCount()
    {
        var decision = ProtectiveStopDecisions.DecideSupersede(
            Successor(), Predecessor("OLD-120"), heldQuantity: 100m,
            resting: [Resting(120m, "SEL", "OLD-120")]);   // quantity null — unknown, never zero

        Assert.AreEqual(SupersedeAction.Wait, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Supersede_UnreadableHoldingsWait()
    {
        var decision = ProtectiveStopDecisions.DecideSupersede(
            Successor(), Predecessor("OLD-120"), heldQuantity: null,
            resting: [Sized(120m, 100, "OLD-120")]);

        Assert.AreEqual(SupersedeAction.Wait, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Supersede_CancelsThePredecessorWhenTheWindowIsOpen()
    {
        // The intraday path. Make-before-break cannot complete against a free-quantity broker, so the
        // only route is to cancel first — and saying so as a named decision is what lets the worker
        // arm cover BEFORE it opens the gap.
        var decision = ProtectiveStopDecisions.DecideSupersede(
            Successor(), Predecessor("OLD-120"), heldQuantity: 100m,
            resting: [Sized(120m, 100, "OLD-120")], replacementWindowAllowed: true);

        Assert.AreEqual(SupersedeAction.CancelPredecessorThenPlace, decision.Action, decision.Reason);
        StringAssert.Contains(decision.Reason, "local backstop");
    }

    [TestMethod]
    public void Supersede_WaitsRatherThanCancellingWhenNoReplacementCouldBePlaced()
    {
        // Same inputs, window shut. Cancelling here would open a gap for no benefit — and waiting is
        // not a compromise: the venue clears the book at the close, so the raise goes in clean next
        // session with nothing cancelled at all.
        var decision = ProtectiveStopDecisions.DecideSupersede(
            Successor(), Predecessor("OLD-120"), heldQuantity: 100m,
            resting: [Sized(120m, 100, "OLD-120")], replacementWindowAllowed: false);

        Assert.AreEqual(SupersedeAction.Wait, decision.Action, decision.Reason);
        StringAssert.Contains(decision.Reason, "next session");
    }

    [TestMethod]
    public void Supersede_AnOpenWindowDoesNotOverrideAnUnknown()
    {
        // The window says "you could act"; the book says "you do not know what you would be acting on".
        // Unknown has to win, or an unreadable read becomes a licence to cancel real protection.
        var decision = ProtectiveStopDecisions.DecideSupersede(
            Successor(), Predecessor("OLD-120"), heldQuantity: 100m,
            resting: null, replacementWindowAllowed: true);

        Assert.AreEqual(SupersedeAction.Wait, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Supersede_AnOpenWindowStillPrefersRestingAlongsideWhenSharesAreFree()
    {
        // Free shares mean no gap is needed at all, so an open window must not turn a harmless
        // side-by-side placement into a cancellation.
        var decision = ProtectiveStopDecisions.DecideSupersede(
            Successor(desired: 50), Predecessor("OLD-120"), heldQuantity: 150m,
            resting: [Sized(120m, 100, "OLD-120")], replacementWindowAllowed: true);

        Assert.AreEqual(SupersedeAction.Proceed, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Supersede_AStopThatReplacesNothingProceeds()
    {
        var decision = ProtectiveStopDecisions.DecideSupersede(
            Active(desired: 45), predecessor: null, heldQuantity: 45m, resting: []);

        Assert.AreEqual(SupersedeAction.Proceed, decision.Action, decision.Reason);
    }

    // ── Releasing shares so a REDUCING sell can get through ──────────────────
    //
    // A target scale-out takes profit on part of a position, so it makes the position smaller — and it
    // is still refused when a protective stop covers the whole holding, because the broker sizes a
    // SELL against custody minus resting SELLs. The stop has to give up its shares first.

    private static ProtectiveStop Holder(string orderNo, int quantity = 100) => Stop(baseline: 0) with
    {
        StopId = "holder", State = "active", DesiredQuantity = quantity,
        StopTrigger = 120m, StopLimit = 118.8m, LastOrderNo = orderNo
    };

    [TestMethod]
    public void Release_StandsDownOurOwnStopWhenItIsBlockingAReduction()
    {
        var decision = ProtectiveStopDecisions.DecideRelease(
            "FFC", quantityNeeded: 40, heldQuantity: 100m,
            resting: [Sized(120m, 100, "STOP-1")], stops: [Holder("STOP-1")]);

        Assert.AreEqual(StopReleaseAction.ReleaseStop, decision.Action, decision.Reason);
        Assert.AreEqual("holder", decision.StopId);
        Assert.AreEqual("STOP-1", decision.OrderNo);
    }

    [TestMethod]
    public void Release_DoesNothingWhenEnoughSharesAreAlreadyFree()
    {
        var decision = ProtectiveStopDecisions.DecideRelease(
            "FFC", quantityNeeded: 40, heldQuantity: 150m,
            resting: [Sized(120m, 100, "STOP-1")], stops: [Holder("STOP-1")]);

        Assert.AreEqual(StopReleaseAction.NotNeeded, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Release_RefusesToTouchAnOrderThisSystemDidNotPlace()
    {
        // The shares are committed to something with no protective stop behind it — a manual order, or
        // one from another tool. Cancelling it to make room would be trading away protection nobody
        // asked about.
        var decision = ProtectiveStopDecisions.DecideRelease(
            "FFC", quantityNeeded: 40, heldQuantity: 100m,
            resting: [Sized(120m, 100, "SOMEONE-ELSE")], stops: [Holder("STOP-1")]);

        Assert.AreEqual(StopReleaseAction.CannotRelease, decision.Action, decision.Reason);
        StringAssert.Contains(decision.Reason, "not placed here");
    }

    [TestMethod]
    public void Release_MatchesTheStopByOrderNumberOnly_NeverByPrice()
    {
        // A stop whose recorded order number is absent from the book is NOT the row sitting at a
        // similar price. Matching on price here would cancel an unrelated order.
        var decision = ProtectiveStopDecisions.DecideRelease(
            "FFC", quantityNeeded: 40, heldQuantity: 100m,
            resting: [Sized(120m, 100, "UNKNOWN-ROW")], stops: [Holder("STALE-NO")]);

        Assert.AreEqual(StopReleaseAction.CannotRelease, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Release_AnUnreadableBookReleasesNothing()
    {
        var decision = ProtectiveStopDecisions.DecideRelease(
            "FFC", quantityNeeded: 40, heldQuantity: 100m,
            resting: null, stops: [Holder("STOP-1")]);

        Assert.AreEqual(StopReleaseAction.CannotRelease, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Release_AnUnreadableRestingQuantityReleasesNothing()
    {
        var decision = ProtectiveStopDecisions.DecideRelease(
            "FFC", quantityNeeded: 40, heldQuantity: 100m,
            resting: [Resting(120m, "SEL", "STOP-1")], stops: [Holder("STOP-1")]);

        Assert.AreEqual(StopReleaseAction.CannotRelease, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Release_PicksTheStopThatFreesTheMostShares()
    {
        var decision = ProtectiveStopDecisions.DecideRelease(
            "FFC", quantityNeeded: 40, heldQuantity: 100m,
            resting: [Sized(120m, 30, "SMALL"), Sized(121m, 70, "BIG")],
            stops:
            [
                Holder("SMALL") with { StopId = "small" },
                Holder("BIG") with { StopId = "big" }
            ]);

        Assert.AreEqual(StopReleaseAction.ReleaseStop, decision.Action, decision.Reason);
        Assert.AreEqual("big", decision.StopId, "one cancellation should free as much as possible");
    }

    [TestMethod]
    public void Release_IgnoresRestingBuys()
    {
        // A resting BUY commits cash, not shares, and must not count against what can be sold.
        var decision = ProtectiveStopDecisions.DecideRelease(
            "FFC", quantityNeeded: 40, heldQuantity: 100m,
            resting: [new RestingOrder("FFC", "BUY", "NOR", 100, 119m, "BUY-1", "row")],
            stops: [Holder("STOP-1")]);

        Assert.AreEqual(StopReleaseAction.NotNeeded, decision.Action, decision.Reason);
    }

    // ── Order numbers are NOT unique across symbols ──────────────────────────
    //
    // CONFIRMED live 2026-08-28. This broker numbers orders {connection}11XK{seq}, and the sequence
    // restarts on every new connection — so the same string names different orders on different
    // symbols. A real capture had `0411XK1` as both a MARI BUY (10:38) and a PAEL protective stop
    // (11:28) on one account, one day. Anything matching a stop to a resting order by number alone
    // will eventually match somebody else's live order — and the supersede path CANCELS what it
    // matches.

    [TestMethod]
    public void Supersede_DoesNotMatchAnotherSymbolsOrderWithTheSameNumber()
    {
        // The book holds no FFC order at all — only an unrelated symbol reusing the number. The
        // predecessor's order is therefore genuinely gone and the row should be retired, NOT treated
        // as still resting (which would then try to cancel the other symbol's order).
        var foreign = new RestingOrder("MARI", "BUY", "NOR", 38, 662m, "0411XK1", "row");

        var decision = ProtectiveStopDecisions.DecideSupersede(
            Successor(), Predecessor("0411XK1"), heldQuantity: 100m,
            resting: [foreign], replacementWindowAllowed: true);

        Assert.AreEqual(SupersedeAction.RetirePredecessorFirst, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Release_DoesNotMatchAnotherSymbolsOrderWithTheSameNumber()
    {
        // Same collision, on the release path: FFC has no resting SELL, so the shares are free and
        // nothing needs standing down.
        var foreign = new RestingOrder("MARI", "BUY", "NOR", 38, 662m, "STOP-1", "row");

        var decision = ProtectiveStopDecisions.DecideRelease(
            "FFC", quantityNeeded: 40, heldQuantity: 100m,
            resting: [foreign], stops: [Holder("STOP-1")]);

        Assert.AreEqual(StopReleaseAction.NotNeeded, decision.Action, decision.Reason);
    }

    [TestMethod]
    public void Supersede_StillMatchesTheRightOrderWhenAnotherSymbolSharesTheNumber()
    {
        // The discriminating case: BOTH exist. The stop's own symbol must win.
        var foreign = new RestingOrder("MARI", "BUY", "NOR", 38, 662m, "SHARED", "row");
        var mine = Sized(120m, 100, "SHARED");

        var decision = ProtectiveStopDecisions.DecideSupersede(
            Successor(), Predecessor("SHARED"), heldQuantity: 100m,
            resting: [foreign, mine], replacementWindowAllowed: true);

        Assert.AreEqual(SupersedeAction.CancelPredecessorThenPlace, decision.Action, decision.Reason);
    }

    // ── The quiet failure: a small raise matched to the order it is replacing ──

    [TestMethod]
    public void Placement_ASmallRaiseIsNotMistakenForItsOwnPredecessor()
    {
        // A lift of 1.25% falls inside the 2% price-match tolerance, so without excluding the
        // predecessor's order number the raise matches the very order it is replacing and is skipped
        // silently — forever. This is the bug the exclusion set exists for.
        var raised = Successor() with { StopTrigger = 121.5m, StopLimit = 120.3m };
        RestingOrder[] resting = [Sized(120m, 100, "OLD-120")];

        var withoutExclusion = ProtectiveStopDecisions.DecidePlacement(
            raised, heldQuantity: 100m, Today, resting);
        Assert.AreEqual(PlacementAction.Skip, withoutExclusion.Action,
            "documents the old behaviour the exclusion set corrects");

        var withExclusion = ProtectiveStopDecisions.DecidePlacement(
            raised, heldQuantity: 100m, Today, resting,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OLD-120" });

        Assert.AreEqual(PlacementAction.Place, withExclusion.Action, withExclusion.Reason);
        Assert.AreEqual(100, withExclusion.Quantity);
    }
}
