using TradingAgent.Models;
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

    private static TradingSignal Signal(string action, string type, decimal price) => new()
    {
        Action = action,
        Symbol = "FFC",
        Quantity = 10,
        OrderType = type,
        EntryPrice = price,
        PreservePriceIntent = true
    };
}
