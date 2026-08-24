using TradingAgent.Models;
using TradingAgent.Reconciliation;
using TradingAgent.Risk;
using TradingAgent.Trading;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class PersistentOrderTests
{
    private static readonly DateOnly Today = new(2026, 8, 21);
    private static readonly DateOnly Tomorrow = new(2026, 8, 22);
    private static readonly DateTime Now = new(2026, 8, 21, 5, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Eligibility_AllowsRestingOrders_ButNeverMarket()
    {
        Assert.IsNull(PersistentOrderDecisions.ValidateEligibility("LIMIT"));
        Assert.IsNull(PersistentOrderDecisions.ValidateEligibility("STOPLOSS"));
        StringAssert.Contains(PersistentOrderDecisions.ValidateEligibility("MARKET"), "one-shot");
    }

    [TestMethod]
    public void Quantity_UsesOnlyTheUnfilledRemainder()
    {
        var intent = Intent(quantity: 100);
        Assert.AreEqual(35, PersistentOrderDecisions.QuantityToPlace(intent, filled: 65));
        Assert.AreEqual(0, PersistentOrderDecisions.QuantityToPlace(intent, filled: 100));
    }

    [TestMethod]
    public void SellQuantity_IsClampedToUncommittedHoldings()
    {
        var intent = Intent(quantity: 100) with { Action = "SELL" };
        Assert.AreEqual(25,
            PersistentOrderDecisions.QuantityToPlace(intent, filled: 40, availableToSell: 25));
    }

    [TestMethod]
    public void SellAvailability_SubtractsOutstandingSellsFromHoldings()
    {
        var snapshot = Snapshot(
            positions: [new("LUCK", 50m)],
            openOrders: [new("S1", "LUCK", "SEL", 20, 900m)]);

        var decision = SellQuantityRule.Available(
            snapshot, "LUCK", Now, TimeSpan.FromMinutes(2));

        Assert.IsTrue(decision.Known);
        Assert.AreEqual(30, decision.AvailableQuantity);
    }

    [TestMethod]
    public void SellAvailability_FailsClosedWhenAnOpenSellQuantityIsUnknown()
    {
        var snapshot = Snapshot(
            positions: [new("LUCK", 50m)],
            openOrders: [new("S1", "LUCK", "SELL", null, 900m)]);

        var decision = SellQuantityRule.Available(
            snapshot, "LUCK", Now, TimeSpan.FromMinutes(2));

        Assert.IsFalse(decision.Known);
        StringAssert.Contains(decision.Reason, "no remaining quantity");
    }

    [TestMethod]
    public void SellSizing_ReducesOneHundredRequestedToFiftyHeld()
    {
        IReadOnlyList<IReadOnlyList<TradingSignal>> groups =
        [
            [new() { Action = "SELL", Symbol = "LUCK", Quantity = 100,
                     OrderType = "LIMIT", EntryPrice = 900m }]
        ];

        var plan = SellQuantityRule.SizeIndependentSells(
            groups,
            Snapshot(positions: [new("LUCK", 50m)]),
            Now,
            TimeSpan.FromMinutes(2));

        Assert.IsNull(plan.Problem);
        Assert.AreEqual(50, plan.Groups[0][0].Quantity);
        Assert.AreEqual(100, plan.Adjustments.Single().RequestedQuantity);
        Assert.AreEqual(100, groups[0][0].Quantity, "The approved request must remain immutable.");
    }

    [TestMethod]
    public void Attempt_IsOncePerTradingDate_AndNeverWhileResting()
    {
        var intent = Intent(quantity: 100) with { LastAttemptSessionDate = Today };
        Assert.IsFalse(PersistentOrderDecisions.MayAttempt(
            intent, Now, Today, ownOrderIsResting: false, out _));
        Assert.IsTrue(PersistentOrderDecisions.MayAttempt(
            intent, Now, Tomorrow, ownOrderIsResting: false, out _));
        Assert.IsFalse(PersistentOrderDecisions.MayAttempt(
            intent, Now, Tomorrow, ownOrderIsResting: true, out _));
    }

    [TestMethod]
    public void Attempt_StopsAtExpiryOrUnknownAttention()
    {
        var expired = Intent(quantity: 10) with { ExpiresUtc = Now };
        Assert.IsFalse(PersistentOrderDecisions.MayAttempt(
            expired, Now, Today, false, out _));

        var attention = Intent(quantity: 10) with { State = "attention" };
        Assert.IsFalse(PersistentOrderDecisions.MayAttempt(
            attention, Now, Today, false, out _));
    }

    [TestMethod]
    public void PriorAcceptedOrderWithoutObservedClose_StopsReplacement()
    {
        var intent = Intent(10) with { LastAttemptSessionDate = Today };
        var accepted = new PersistentOrderPlacement
        {
            PlacementId = "p1",
            IntentId = intent.IntentId,
            SessionDate = Today,
            Attempt = 1,
            Quantity = 10,
            State = "accepted"
        };
        Assert.IsTrue(PersistentOrderDecisions.PriorOutcomeWasNotObserved(
            intent, accepted, Tomorrow));
        Assert.IsFalse(PersistentOrderDecisions.PriorOutcomeWasNotObserved(
            intent, accepted with { State = "lapsed" }, Tomorrow));
    }

    [TestMethod]
    public void Retry_IsOfferedOnlyForLatestKnownFailureToday()
    {
        var intent = Intent(10) with { LastAttemptSessionDate = Today };
        var failed = Placement("failed");

        Assert.IsTrue(PersistentOrderDecisions.CanRetryFailedToday(
            intent, failed, Now, Today, out _));
        Assert.IsFalse(PersistentOrderDecisions.CanRetryFailedToday(
            intent, failed with { State = "unknown" }, Now, Today, out _));
        Assert.IsFalse(PersistentOrderDecisions.CanRetryFailedToday(
            intent, failed, Now, Tomorrow, out _));
    }

    [TestMethod]
    public void Retry_BrokerCheckStopsForMatchingRestingOrderOrRecentFill()
    {
        var intent = Intent(10) with { LastAttemptSessionDate = Today };
        var failed = Placement("failed");
        var resting = Snapshot(openOrders: [new("O-1", "FFC", "BUY", 10, 550m)]);
        var fill = Snapshot() with
        {
            Fills = [new("O-2", "FFC", "BUY", 10, 550m, failed.CreatedUtc)]
        };
        var queued = Snapshot() with
        {
            OrderEvents = [new("O-4", "FFC", "BUY", "QUE", 10, 550m, failed.CreatedUtc)]
        };

        StringAssert.Contains(PersistentOrderDecisions.FindPossibleBrokerMatch(
            intent, failed, 10, resting), "O-1");
        StringAssert.Contains(PersistentOrderDecisions.FindPossibleBrokerMatch(
            intent, failed, 10, fill), "O-2");
        StringAssert.Contains(PersistentOrderDecisions.FindPossibleBrokerMatch(
            intent, failed, 10, queued), "O-4");
        Assert.IsNull(PersistentOrderDecisions.FindPossibleBrokerMatch(
            intent, failed, 10, queued with
            {
                OrderEvents =
                [
                    new("O-4", "FFC", "BUY", "QUE", 10, 550m, failed.CreatedUtc),
                    new("O-4", "FFC", "BUY", "REJ", 10, 550m, failed.CreatedUtc.AddSeconds(1))
                ]
            }));
        Assert.IsNull(PersistentOrderDecisions.FindPossibleBrokerMatch(
            intent, failed, 10, Snapshot(openOrders: [new("O-3", "FFC", "SEL", 10, 550m)])));
    }

    [TestMethod]
    public void PriceIntent_NeverMakesALimitWorse()
    {
        var buy = Signal("BUY", "LIMIT", 100m);
        Assert.IsNotNull(PriceIntentRule.Validate(buy, 101m, null));
        Assert.IsNull(PriceIntentRule.Validate(buy, 99m, null));

        var sell = Signal("SELL", "LIMIT", 100m);
        Assert.IsNotNull(PriceIntentRule.Validate(sell, 99m, null));
        Assert.IsNull(PriceIntentRule.Validate(sell, 101m, null));
    }

    [TestMethod]
    public void PriceIntent_StopCannotDriftAtAll()
    {
        var stop = Signal("SELL", "STOPLOSS", 100m);
        stop.LimitPrice = 99m;
        Assert.IsNull(PriceIntentRule.Validate(stop, 100m, 99m));
        Assert.IsNotNull(PriceIntentRule.Validate(stop, 100.01m, 99m));
        Assert.IsNotNull(PriceIntentRule.Validate(stop, 100m, 98.99m));
    }

    private static PersistentOrderIntent Intent(int quantity) => new()
    {
        IntentId = "intent-1",
        Symbol = "FFC",
        Action = "BUY",
        Quantity = quantity,
        OrderType = "LIMIT",
        Price = 550m,
        ExpiresUtc = Now.AddDays(30)
    };

    private static PersistentOrderPlacement Placement(string state) => new()
    {
        PlacementId = "placement-1",
        IntentId = "intent-1",
        SessionDate = Today,
        Attempt = 1,
        Quantity = 10,
        State = state,
        RequestedPrice = 550m,
        CreatedUtc = Now
    };

    private static TradingSignal Signal(string action, string type, decimal price) => new()
    {
        Action = action,
        Symbol = "FFC",
        Quantity = 10,
        OrderType = type,
        EntryPrice = price,
        PreservePriceIntent = true
    };

    private static BrokerReconciliationSnapshot Snapshot(
        IReadOnlyList<BrokerPosition>? positions = null,
        IReadOnlyList<BrokerWorkingOrder>? openOrders = null) =>
        new(true, true, "ok", Now)
        {
            Positions = positions ?? [],
            OpenOrders = openOrders ?? []
        };
}
