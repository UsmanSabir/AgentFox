using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// Fill confirmation and native-stop placement for protective stops.
///
/// <para>
/// Every rule here can put a real SELL order into the market, so the tests lean on the readings where
/// acting would be WRONG: an unreadable holdings grid, a baseline that was never captured, a stop
/// already resting, a session that has not rolled. The asymmetry is the whole point — a stop that
/// failed to go in is visible and fixable by hand, whereas a duplicate stop sells the position twice
/// and this broker offers no way to cancel it.
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
}
