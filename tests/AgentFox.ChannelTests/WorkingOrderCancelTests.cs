using TradingAgent.Broker;
using TradingAgent.Reconciliation;
using TradingAgent.Trading;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// Pins the portfolio's per-row Cancel: which orders are MANAGED (and would be quietly re-placed by a
/// plain broker cancel), that ownership is matched on symbol plus either of the order's ids, and that
/// a broker reply is never over-read as "cancelled".
/// </summary>
[TestClass]
public sealed class WorkingOrderCancelTests
{
    [TestMethod]
    public void A_triggered_stop_is_still_recognised_as_ours_by_its_short_number()
    {
        // Measured 2026-09-22: once a stop triggers, the book leads with the long exchange id and the
        // short number the stop recorded at placement moves to the alternate column.
        var triggered = Row("PAEL", "0010TKNSH300GM0R", alternate: "0411XK65");
        var stop = Stop("PAEL", lastOrderNo: "0411XK65");

        var owners = WorkingOrderCancelRule.Classify(triggered, [stop], []);

        Assert.AreEqual(1, owners.Count,
            "Missing it here means a plain cancel, and the stop pass re-places the stop minutes later.");
        Assert.AreEqual(WorkingOrderOwnerKind.ProtectiveStop, owners[0].Kind);
        Assert.AreEqual(stop.StopId, owners[0].Id);
        StringAssert.Contains(owners[0].Consequence, "DISARMS");
    }

    [TestMethod]
    public void The_same_number_on_another_symbol_is_not_ours()
    {
        // 0411XK1 named a MARI buy, a QTECH stop and a SELECT stop on one day (2026-08-28).
        var owners = WorkingOrderCancelRule.Classify(
            Row("MARI", "0411XK1"), [Stop("QTECH", lastOrderNo: "0411XK1")], []);

        Assert.AreEqual(0, owners.Count, "A number alone never names an order.");
    }

    [TestMethod]
    public void A_keep_working_order_is_claimed_by_its_placement_or_its_last_number()
    {
        var intent = Intent("MUGHAL", lastOrderNo: null);
        var placement = Placement(intent.IntentId, "0010TLU76K00SGEC");

        var byPlacement = WorkingOrderCancelRule.Classify(
            Row("MUGHAL", "0010TLU76K00SGEC"), [], [(intent, [placement])]);
        var byLastNumber = WorkingOrderCancelRule.Classify(
            Row("MUGHAL", "0010TLU76K00SGEC"), [], [(intent with { LastOrderNo = "0010TLU76K00SGEC" }, [])]);

        Assert.AreEqual(WorkingOrderOwnerKind.PersistentOrder, byPlacement.Single().Kind);
        Assert.AreEqual(WorkingOrderOwnerKind.PersistentOrder, byLastNumber.Single().Kind);
        StringAssert.Contains(byPlacement[0].Consequence, "will NOT be placed again");
    }

    [TestMethod]
    public void A_terminal_intent_does_not_claim_anything()
    {
        var done = Intent("DGKC", lastOrderNo: "0010TLU76K005YFQ") with { State = "cancelled" };

        Assert.AreEqual(0, WorkingOrderCancelRule.Classify(Row("DGKC", "0010TLU76K005YFQ"), [], [(done, [])]).Count);
    }

    [TestMethod]
    public void Only_a_managed_order_asks_first()
    {
        var managed = WorkingOrderCancelRule.Classify(Row("PPL", "0911XK1"), [Stop("PPL", "0911XK1")], []);

        Assert.IsFalse(WorkingOrderCancelRule.NeedsAcknowledgement([], acknowledged: false),
            "A plain order's confirmation is the button's own; asking twice trains click-through.");
        Assert.IsTrue(WorkingOrderCancelRule.NeedsAcknowledgement(managed, acknowledged: false));
        Assert.IsFalse(WorkingOrderCancelRule.NeedsAcknowledgement(managed, acknowledged: true));
    }

    [TestMethod]
    public void A_stop_and_an_intent_naming_one_order_is_refused_rather_than_guessed()
    {
        var intent = Intent("PPL", lastOrderNo: "0911XK1");
        var owners = WorkingOrderCancelRule.Classify(
            Row("PPL", "0911XK1"), [Stop("PPL", "0911XK1")], [(intent, [])]);

        Assert.IsTrue(WorkingOrderCancelRule.IsAmbiguous(owners));
        Assert.IsFalse(WorkingOrderCancelRule.IsAmbiguous(
            [owners[0], new WorkingOrderOwner(WorkingOrderOwnerKind.Edition, null, "strategy note")]),
            "An edition's note adds a warning; it is not a second route.");
    }

    [TestMethod]
    public void A_refused_cancel_of_a_vanished_order_is_never_reported_as_cancelled()
    {
        // MWMP, 2026-09-21: "Invalid Order[...] to cancel" 27 seconds after the order FILLED.
        var result = WorkingOrderCancellationService.Describe(
            Row("MWMP", "0010TLON5W00P8CB"),
            new BrokerCancellationResult(Gone: true, RequestAccepted: false, Verified: true, "Invalid Order"),
            afterwards: "");

        Assert.AreEqual("gone_possibly_filled", result.Outcome);
        Assert.IsTrue(result.Gone);
        StringAssert.Contains(result.Message, "FILLED");
    }

    [TestMethod]
    public void Confirmed_and_unconfirmed_cancels_say_so()
    {
        var row = Row("DGKC", "0010TLU76K005YFQ");

        var confirmed = WorkingOrderCancellationService.Describe(
            row, new BrokerCancellationResult(true, true, true, "CXL"), "");
        var unconfirmed = WorkingOrderCancellationService.Describe(
            row, new BrokerCancellationResult(false, false, false, "Board is shut"), "");

        Assert.AreEqual("cancelled", confirmed.Outcome);
        Assert.AreEqual("not_confirmed", unconfirmed.Outcome);
        Assert.IsFalse(unconfirmed.Gone);
        StringAssert.Contains(unconfirmed.Message, "Board is shut");
    }

    private static BrokerWorkingOrder Row(string symbol, string orderNo, string? alternate = null) =>
        new(orderNo, symbol, "SELL", 10, 38.55m, "LIMIT", alternate);

    private static ProtectiveStop Stop(string symbol, string lastOrderNo) => new()
    {
        StopId = Guid.NewGuid().ToString("N"),
        Symbol = symbol,
        StopTrigger = 38.60m,
        StopLimit = 38.55m,
        State = "active",
        LastOrderNo = lastOrderNo
    };

    private static PersistentOrderIntent Intent(string symbol, string? lastOrderNo) => new()
    {
        IntentId = Guid.NewGuid().ToString("N"),
        Symbol = symbol,
        Action = "BUY",
        Quantity = 142,
        OrderType = "LIMIT",
        Price = 70m,
        ExpiresUtc = DateTime.UtcNow.AddDays(5),
        LastOrderNo = lastOrderNo
    };

    private static PersistentOrderPlacement Placement(string intentId, string orderNo) => new()
    {
        PlacementId = Guid.NewGuid().ToString("N"),
        IntentId = intentId,
        SessionDate = DateOnly.FromDateTime(DateTime.UtcNow),
        Attempt = 1,
        Quantity = 142,
        BrokerOrderNo = orderNo
    };
}
