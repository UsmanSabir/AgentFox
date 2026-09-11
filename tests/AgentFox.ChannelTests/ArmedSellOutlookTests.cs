using TradingAgent.Models;
using TradingAgent.Reconciliation;
using TradingAgent.Risk;

namespace AgentFox.ChannelTests;

/// <summary>
/// Pins when a triggered armed SELL may be RETIRED rather than left armed to retry.
///
/// <para>
/// From the CNERGY incident of 2026-09-11, which <see cref="ArmedSellOutlook"/> describes in full: a
/// trailing exit outlived the position a sibling order had already sold, and was triggered and refused
/// 29 times in 53 minutes on one message that could not distinguish "you own none" from "you own them
/// and they are committed". The first never clears; the second clears the moment the commitment does.
/// </para>
///
/// <para>
/// The direction that matters in every test here is that retirement is the NARROW case. An order that
/// might still fill must survive every one of these, because ending it destroys a live exit thesis.
/// </para>
/// </summary>
[TestClass]
public sealed class ArmedSellOutlookTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 4, 45, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(3);

    [TestMethod]
    public void APositionThatIsGone_RetiresTheOrder()
    {
        // Exactly the CNERGY state at 09:41:50 PKT: the sibling exit has sold all 2471 shares, so
        // custody is empty and nothing is resting over it either.
        var gone = SellQuantityRule.Available(Snapshot(), "CNERGY", Now, MaxAge);

        Assert.IsTrue(gone.Known);
        Assert.AreEqual(0, gone.HeldQuantity, "Custody, not availability, is what decides this.");

        var verdict = ArmedSellOutlook.For(gone, brokerConfirmed: true, "CNERGY");

        Assert.AreEqual(ArmedSellDisposition.Retire, verdict.Disposition);
        StringAssert.Contains(verdict.Reason, "holds no CNERGY shares");
        StringAssert.Contains(verdict.Reason, "cannot fill however long it waits");
    }

    [TestMethod]
    public void AFullyCommittedHolding_StaysArmed()
    {
        // 2471 owned, all of them under a resting protective stop. Zero AVAILABLE and the identical
        // refusal text, but the order must survive: the commitment goes when the stop is superseded,
        // and at the latest when the venue clears its book at the close.
        var committed = SellQuantityRule.Available(
            Snapshot(
                positions: [new("CNERGY", 2471m)],
                openOrders: [new("0411XK1", "CNERGY", "SEL", 2471, 9.90m)]),
            "CNERGY", Now, MaxAge);

        Assert.AreEqual(0, committed.AvailableQuantity);
        Assert.AreEqual(2471, committed.HeldQuantity);

        var verdict = ArmedSellOutlook.For(committed, brokerConfirmed: true, "CNERGY");

        Assert.AreEqual(ArmedSellDisposition.StayArmed, verdict.Disposition,
            "Zero available is not zero held. Retiring here would destroy a live exit over a stop "
            + "that is about to give its shares up.");
        StringAssert.Contains(verdict.Reason, "Still armed");
    }

    [TestMethod]
    public void AnUnconfirmedZeroHolding_NeverRetires()
    {
        // The same empty custody, but read from the cache rather than confirmed for this question.
        // A snapshot can describe the account as it was a whole poll interval ago — 21 minutes on the
        // deployed configuration — which is how a position bought moments ago reads as absent.
        var cached = SellQuantityRule.Available(Snapshot(), "CNERGY", Now, MaxAge);

        var verdict = ArmedSellOutlook.For(cached, brokerConfirmed: false, "CNERGY");

        Assert.AreEqual(ArmedSellDisposition.StayArmed, verdict.Disposition,
            "Only a read taken FOR this question may end an order. This is the same asymmetry "
            + "SellRefusalRule.MayRefuse has, and for the same reason.");
    }

    [TestMethod]
    public void UnknownAvailability_NeverRetires_AndNeverSubmits()
    {
        var unhealthy = SellQuantityRule.Available(
            new BrokerReconciliationSnapshot(true, false, "broker unreachable", Now),
            "CNERGY", Now, MaxAge);

        Assert.IsFalse(unhealthy.Known);
        Assert.IsNull(unhealthy.HeldQuantity, "Unknown is never zero — invariant 4.");

        var verdict = ArmedSellOutlook.For(unhealthy, brokerConfirmed: true, "CNERGY");

        Assert.AreEqual(ArmedSellDisposition.StayArmed, verdict.Disposition);
        StringAssert.Contains(verdict.Reason, "unknown");
    }

    [TestMethod]
    public void APartialHolding_Submits_RatherThanRetiring()
    {
        // 57 of the original 2471 left after a part fill elsewhere. Selling some of an exit is right
        // where selling none of it is not, and the execution boundary sizes it.
        var partial = SellQuantityRule.Available(
            Snapshot(positions: [new("CNERGY", 57m)]), "CNERGY", Now, MaxAge);

        var verdict = ArmedSellOutlook.For(partial, brokerConfirmed: true, "CNERGY");

        Assert.AreEqual(ArmedSellDisposition.Submit, verdict.Disposition);
    }

    [TestMethod]
    public void ForeignOrdersCommittingEveryShare_StayArmed_NotRetired()
    {
        // The shares are held but committed to an order placed in the broker's own mobile app. Nothing
        // here can stand that down, but it is still not a reason to end this order: it clears when the
        // operator cancels it there, or at the close.
        var foreign = SellQuantityRule.Available(
            Snapshot(
                positions: [new("CNERGY", 2471m)],
                openOrders: [new("0010TKG61200J52S", "CNERGY", "SELL", 2471, 12m)]),
            "CNERGY", Now, MaxAge);

        Assert.AreEqual(ArmedSellDisposition.StayArmed,
            ArmedSellOutlook.For(foreign, brokerConfirmed: true, "CNERGY").Disposition);
    }

    private static BrokerReconciliationSnapshot Snapshot(
        IReadOnlyList<BrokerPosition>? positions = null,
        IReadOnlyList<BrokerWorkingOrder>? openOrders = null) =>
        new(true, true, "ok", Now)
        {
            Positions = positions ?? [],
            OpenOrders = openOrders ?? []
        };
}
