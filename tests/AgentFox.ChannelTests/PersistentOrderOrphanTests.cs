using TradingAgent.Reconciliation;
using TradingAgent.Trading;

namespace AgentFox.ChannelTests;

/// <summary>
/// Finding an order the ledger cannot name.
///
/// <para>
/// <b>The failure, measured 2026-09-01.</b> A persistent SELL of 50 SYS was submitted and the process
/// stopped before the broker's answer was recorded. The order was accepted and rested as
/// <c>0411XK63</c> from 10:30:58. The only number on record, from an earlier attempt, named an order
/// that no longer existed — so the operator's cancel was refused (<c>Invalid Order[...] to cancel</c>),
/// and the next maintenance pass reported the intent "cancelled; no native order remains outstanding"
/// while the order went on resting and committing the entire holding. Every further sell was refused
/// for want of free shares.
/// </para>
///
/// <para>
/// Two claims were wrong at once: that cancel had nothing to cancel, and that nothing remained. Both
/// came from treating an order the ledger failed to write down as an order that does not exist.
/// </para>
/// </summary>
[TestClass]
public sealed class PersistentOrderOrphanTests
{
    private static PersistentOrderIntent Intent(string state = "placing", int quantity = 50) => new()
    {
        IntentId = "intent-1",
        Symbol = "SYS",
        Action = "SELL",
        Quantity = quantity,
        OrderType = "LIMIT",
        Price = 132m,
        State = state,
        AttemptCount = 4,
        LastAttemptSessionDate = new DateOnly(2026, 9, 1),
        ExpiresUtc = DateTime.UtcNow.AddDays(29),
        CreatedUtc = DateTime.UtcNow.AddDays(-1),
        UpdatedUtc = DateTime.UtcNow
    };

    private static PersistentOrderPlacement Placement(
        string state, string? orderNo, int attempt = 3) => new()
    {
        PlacementId = $"p{attempt}",
        IntentId = "intent-1",
        SessionDate = new DateOnly(2026, 9, 1),
        Attempt = attempt,
        Quantity = 50,
        BrokerOrderNo = orderNo,
        State = state,
        RequestedPrice = 132m,
        CreatedUtc = DateTime.UtcNow.AddMinutes(-30)
    };

    private static BrokerReconciliationSnapshot Snapshot(params BrokerWorkingOrder[] open) =>
        new(true, true, "ok", DateTime.UtcNow) { OpenOrders = open };

    /// <summary>The real resting order, from the capture.</summary>
    private static BrokerWorkingOrder RealSys(string orderNo = "0010TKNSH300CCSD") =>
        new(orderNo, "SYS", "SEL", 50, 132m);

    // ── the orphan ────────────────────────────────────────────────────────────

    [TestMethod]
    public void TheRestingOrderIsFoundEvenThoughNoPlacementNamesIt()
    {
        // The stale number is on record; the order it named is gone from the book.
        var placements = new[] { Placement("accepted", "0010TKLU3H008UJX") };

        var unclaimed = PersistentOrderDecisions.FindUnclaimedBrokerOrders(
            Intent(), placements, Snapshot(RealSys()));

        Assert.AreEqual(1, unclaimed.Count, "the order that is actually resting must be found");
        Assert.AreEqual("0010TKNSH300CCSD", unclaimed[0].OrderNo);
    }

    [TestMethod]
    public void AnOrderTheLedgerAlreadyNamesIsNotAnOrphan()
    {
        var placements = new[] { Placement("accepted", "0010TKNSH300CCSD") };

        var unclaimed = PersistentOrderDecisions.FindUnclaimedBrokerOrders(
            Intent("cancelling"), placements, Snapshot(RealSys()));

        Assert.AreEqual(0, unclaimed.Count,
            "an order the normal cancel path can already aim at must not be offered as a candidate");
    }

    [TestMethod]
    public void AnIntentWithNoPlacementsAtAllStillFindsItsOrder()
    {
        // The purest form: the crash happened on the first attempt, so there is no placement row to take
        // a price reference from. Having no record must not exclude the search — that is the bug.
        var unclaimed = PersistentOrderDecisions.FindUnclaimedBrokerOrders(
            Intent(), [], Snapshot(RealSys()));

        Assert.AreEqual(1, unclaimed.Count);
    }

    // ── what it refuses ───────────────────────────────────────────────────────

    [TestMethod]
    public void ADifferentSymbolOrSideIsNeverACandidate()
    {
        var snapshot = Snapshot(
            new BrokerWorkingOrder("0411XK1", "QTECH", "SEL", 500, 35.64m),
            new BrokerWorkingOrder("0411XK62", "SYS", "BUY", 50, 132m));

        var unclaimed = PersistentOrderDecisions.FindUnclaimedBrokerOrders(Intent(), [], snapshot);

        Assert.AreEqual(0, unclaimed.Count,
            "cancelling another symbol's order, or the wrong side of this one, is not recoverable");
    }

    [TestMethod]
    public void AQuantityLargerThanTheIntentIsNotACandidate()
    {
        // 200 resting against an intent for 50 cannot be this intent's order, and cancelling it would
        // retire a position the operator never asked this order to touch.
        var unclaimed = PersistentOrderDecisions.FindUnclaimedBrokerOrders(
            Intent(), [], Snapshot(new BrokerWorkingOrder("0411XK99", "SYS", "SEL", 200, 132m)));

        Assert.AreEqual(0, unclaimed.Count);
    }

    [TestMethod]
    public void TwoMatchingOrdersAreBOTHReturnedSoTheCallerCanRefuse()
    {
        // Ambiguity is reported, never resolved by picking one — the same rule as BrokerChargeKey.
        var unclaimed = PersistentOrderDecisions.FindUnclaimedBrokerOrders(
            Intent(), [], Snapshot(RealSys(), RealSys("0010TKNSH300ZZZZ")));

        Assert.AreEqual(2, unclaimed.Count,
            "the adopt path keys on there being exactly one; hiding the second would make it guess");
    }

    // ── the precondition that turns a shape match into evidence ───────────────

    // ── the adoption decision itself ──────────────────────────────────────────
    //
    // This is the branch that claims a LIVE broker order as ours. It fires only after a fault has
    // already happened, so production is the worst place to find out it is wrong — hence a pure function
    // and these tests rather than waiting for the next crash to reveal it.

    private static readonly DateOnly Today = new(2026, 9, 1);

    [TestMethod]
    public void ACrashedSubmissionAdoptsTheOneOrderThatMatchesIt()
    {
        var adoption = PersistentOrderDecisions.PlanAdoption(
            Intent(), [Placement("accepted", "0010TKLU3H008UJX")], Snapshot(RealSys()), Today);

        Assert.IsNotNull(adoption, "one unexplained submission and one unexplained order is an identification");
        Assert.AreEqual("0010TKNSH300CCSD", adoption.Placement.BrokerOrderNo);
        Assert.AreEqual(50, adoption.Placement.Quantity);
        Assert.AreEqual("accepted", adoption.Placement.State);
        StringAssert.Contains(adoption.Reason, "NOT reported by the broker as ours",
            "the row must record that this was inferred, not measured — that distinction is the whole "
            + "basis on which someone later trusts or distrusts the number");
    }

    [TestMethod]
    public void AnAccountedForIntentAdoptsNothing()
    {
        // The guard that stops a shape match alone from claiming an order. Remove it and any resting
        // order that merely looks like the intent gets adopted — including someone else's.
        var adoption = PersistentOrderDecisions.PlanAdoption(
            Intent("resting"), [Placement("accepted", "0411XK99")], Snapshot(RealSys()), Today);

        Assert.IsNull(adoption,
            "nothing of this intent's is missing, so an unclaimed order belongs to something else");
    }

    [TestMethod]
    public void TwoCandidatesAdoptNeither()
    {
        var adoption = PersistentOrderDecisions.PlanAdoption(
            Intent(), [], Snapshot(RealSys(), RealSys("0010TKNSH300ZZZZ")), Today);

        Assert.IsNull(adoption, "picking one of two would cancel a stranger's order half the time");
    }

    [TestMethod]
    public void AnUnreadableBrokerAdoptsNothing()
    {
        // Unknown is never zero: a snapshot that could not be read reports no open orders, which must
        // not be mistaken for "nothing of ours is resting" — nor allowed to adopt on partial evidence.
        var unhealthy = new BrokerReconciliationSnapshot(
            true, false, "the account could not be read", DateTime.UtcNow) { OpenOrders = [RealSys()] };

        Assert.IsNull(PersistentOrderDecisions.PlanAdoption(Intent(), [], unhealthy, Today));
    }

    [TestMethod]
    public void ThePartFilledRemainderIsAdoptedRatherThanTheFullQuantity()
    {
        // GetOutstandingLog reports the REMAINING quantity (CLAUDE.md §6a), so an order that part-filled
        // before we lost track of it must be adopted at what is actually still working.
        var adoption = PersistentOrderDecisions.PlanAdoption(
            Intent(), [], Snapshot(new BrokerWorkingOrder("0411XK63", "SYS", "SEL", 18, 132m)), Today);

        Assert.IsNotNull(adoption);
        Assert.AreEqual(18, adoption.Placement.Quantity);
    }

    [TestMethod]
    public void AClaimedButUnrecordedSubmissionCountsAsUnaccountedFor()
    {
        Assert.IsTrue(PersistentOrderDecisions.HasUnaccountedSubmission(Intent("placing"), []),
            "'placing' means we sent something and never learned what happened to it");

        Assert.IsTrue(PersistentOrderDecisions.HasUnaccountedSubmission(
                Intent("attention"), [Placement("unknown", null)]),
            "an unknown outcome with no order number is the same situation, one state later");
    }

    [TestMethod]
    public void AnAccountedForIntentIsNotOfferedAnOrphan()
    {
        // Without this, a shape match alone could adopt an order — which is exactly the inference this
        // design refuses. An intent whose orders are all recorded has nothing missing.
        Assert.IsFalse(PersistentOrderDecisions.HasUnaccountedSubmission(
                Intent("resting"), [Placement("accepted", "0010TKNSH300CCSD")]),
            "every submission is accounted for, so any unclaimed order belongs to something else");

        Assert.IsFalse(PersistentOrderDecisions.HasUnaccountedSubmission(
                Intent("active"), [Placement("failed", null)]),
            "a definitively FAILED placement reached no broker; there is nothing of ours to find");
    }
}
