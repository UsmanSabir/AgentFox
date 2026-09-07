using TradingAgent.Reconciliation;
using TradingAgent.Risk;

namespace AgentFox.ChannelTests;

/// <summary>
/// Pins the pre-flight SELL refusal on the dashboard order endpoint — specifically that it may only
/// refuse on a CONFIRMED zero, and that when it does refuse it names whatever is holding the shares.
///
/// <para>
/// The incident these were written from is in <see cref="SellRefusalRule"/>'s own doc comment: a resting
/// SELL cancelled from the broker's mobile app stayed in the cached reconciliation snapshot, and this
/// gate refused a sell of 50 genuinely free shares while the dialog's own live panel showed all 50
/// available. The refusal short-circuited the live check at the execution boundary that would have
/// allowed it.
/// </para>
/// </summary>
[TestClass]
public sealed class SellRefusalTests
{
    private static readonly DateTime Now = new(2026, 9, 7, 7, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void CachedZero_IsOnlyEverAReasonToAskTheBroker()
    {
        // Fully committed: 50 held, 50 resting. The state the FCEPL incident's stale snapshot was in.
        var committed = SellQuantityRule.Available(
            Snapshot(positions: [new("FCEPL", 50m)],
                     openOrders: [new("0911XK5", "FCEPL", "SEL", 50, 152m)]),
            "FCEPL", Now, TimeSpan.FromMinutes(3));

        Assert.IsTrue(SellRefusalRule.NeedsBrokerConfirmation(committed),
            "A provable zero is worth one broker read, because it is about to refuse an order.");
        Assert.IsTrue(SellRefusalRule.MayRefuse(committed));
    }

    [TestMethod]
    public void ConfirmedFreeShares_AreNeverRefused()
    {
        // The same account AFTER the out-of-band cancellation — which is what the confirming read sees.
        var free = SellQuantityRule.Available(
            Snapshot(positions: [new("FCEPL", 50m)]), "FCEPL", Now, TimeSpan.FromMinutes(3));

        Assert.IsFalse(SellRefusalRule.NeedsBrokerConfirmation(free));
        Assert.IsFalse(SellRefusalRule.MayRefuse(free),
            "50 free shares must reach the execution boundary, which is the authoritative check.");
    }

    [TestMethod]
    public void UnknownAvailability_FallsThrough_RatherThanRefusing()
    {
        // Unhealthy reconciliation, and an open SELL with no remaining quantity: the two ways this
        // answer becomes unknown. Neither may refuse — invariant 4 does not license a hurdle here,
        // because the live check downstream is both authoritative and better placed to explain it.
        var unhealthy = SellQuantityRule.Available(
            new BrokerReconciliationSnapshot(true, false, "broker unreachable", Now),
            "FCEPL", Now, TimeSpan.FromMinutes(3));
        var unsizable = SellQuantityRule.Available(
            Snapshot(positions: [new("FCEPL", 50m)],
                     openOrders: [new("0911XK5", "FCEPL", "SELL", null, 152m)]),
            "FCEPL", Now, TimeSpan.FromMinutes(3));

        foreach (var decision in new[] { unhealthy, unsizable })
        {
            Assert.IsFalse(decision.Known);
            Assert.IsFalse(SellRefusalRule.MayRefuse(decision));
            Assert.IsFalse(SellRefusalRule.NeedsBrokerConfirmation(decision),
                "There is nothing to confirm about an answer that is already unknown.");
        }
    }

    [TestMethod]
    public void RestingSells_NameForeignOrders_AndExcludeOurOwnStops()
    {
        var snapshot = Snapshot(
            positions: [new("FCEPL", 50m)],
            openOrders:
            [
                new("0911XK5", "FCEPL", "SEL", 30, 152m),           // ours: a protective stop
                new("0010TKG61200J52S", "FCEPL", "SELL", 20, 155m), // placed in the mobile app
                new("0911XK9", "PAEL", "SEL", 10, 38m),             // another symbol
                new("0911XK7", "FCEPL", "BUY", 10, 150m)            // not a sell at all
            ]);

        var foreign = SellRefusalRule.RestingSellsNotPlacedHere(
            snapshot, "FCEPL", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0911XK5" });

        Assert.AreEqual(1, foreign.Count, "Our own stop, another symbol and a BUY all belong out of it.");
        Assert.AreEqual("0010TKG61200J52S", foreign[0].OrderNo);
        Assert.AreEqual(20, foreign[0].RemainingQuantity);
    }

    [TestMethod]
    public void RestingSells_MatchTheSameSideVocabularyTheRuleCounted()
    {
        // Both spellings commit shares, so both must be nameable. A message that cannot explain a
        // commitment the rule counted is the dead end this listing exists to remove.
        Assert.IsTrue(SellQuantityRule.IsSell("SELL"));
        Assert.IsTrue(SellQuantityRule.IsSell("SEL"));
        Assert.IsTrue(SellQuantityRule.IsSell("sel"));
        Assert.IsFalse(SellQuantityRule.IsSell("BUY"));
        Assert.IsFalse(SellQuantityRule.IsSell(null));

        var snapshot = Snapshot(
            positions: [new("FCEPL", 50m)],
            openOrders: [new("A", "FCEPL", "SEL", 25, 152m), new("B", "FCEPL", "SELL", 25, 153m)]);

        Assert.AreEqual(0,
            SellQuantityRule.Available(snapshot, "FCEPL", Now, TimeSpan.FromMinutes(3)).AvailableQuantity);
        Assert.AreEqual(2,
            SellRefusalRule.RestingSellsNotPlacedHere(snapshot, "FCEPL", new HashSet<string>()).Count);
    }

    [TestMethod]
    public void Message_OffersTheRemedy_WhenTheBlockingStopIsOurs()
    {
        var message = SellRefusalRule.Compose(
            "FCEPL",
            "50 held minus 50 already committed to outstanding SELL orders.",
            ["a protective stop at 148 (broker order 0911XK5) is holding 50 share(s)"],
            []);

        StringAssert.Contains(message, "No uncommitted FCEPL shares are available to sell:");
        StringAssert.Contains(message, "broker order 0911XK5");
        StringAssert.Contains(message, "stand down for this sell");
        StringAssert.Contains(message, "50 held minus 50 already committed");
    }

    [TestMethod]
    public void Message_NamesAForeignOrder_AndSaysItCannotBeStoodDownHere()
    {
        var message = SellRefusalRule.Compose(
            "FCEPL",
            "50 held minus 50 already committed to outstanding SELL orders.",
            [],
            ["a resting SELL order (broker order 0010TKG61200J52S) is holding 50 share(s)"]);

        StringAssert.Contains(message, "0010TKG61200J52S");
        StringAssert.Contains(message, "None of them were placed here");
        Assert.IsFalse(message.Contains("stand down for this sell"),
            "Offering a release for an order this system did not place would be a button that cannot work.");
    }

    [TestMethod]
    public void Message_AlwaysStatesItsArithmeticAndThatTheReadWasFresh()
    {
        // "But I just refreshed the panel" is the first thing the operator thinks, and the screen has
        // to answer it. The bare sentence with nothing behind it is what made the FCEPL refusal a dead
        // end even once it was correct.
        foreach (var message in new[]
        {
            SellRefusalRule.Compose("FCEPL", "0 held minus 0 already committed.", [], []),
            SellRefusalRule.Compose("FCEPL", "0 held minus 0 already committed.", ["ours"], []),
            SellRefusalRule.Compose("FCEPL", "0 held minus 0 already committed.", [], ["theirs"])
        })
        {
            StringAssert.Contains(message, "0 held minus 0 already committed.");
            StringAssert.Contains(message, "Checked with the broker just now, not from a cached snapshot.");
        }
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
