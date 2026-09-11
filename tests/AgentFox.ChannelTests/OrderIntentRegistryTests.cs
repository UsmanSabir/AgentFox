using TradingAgent.Trading;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class OrderIntentRegistryTests
{
    [TestMethod]
    public void Registry_CoversEverySupportedBrokerOrderType()
    {
        var types = OrderIntentRegistry.All.Select(item => item.OrderType).Distinct().ToArray();
        CollectionAssert.AreEquivalent(new[] { "LIMIT", "MARKET", "STOPLOSS" }, types);
    }

    [TestMethod]
    public void Registry_IdsAreUniqueAndConditionalChoicesDeclareATrigger()
    {
        Assert.AreEqual(
            OrderIntentRegistry.All.Count,
            OrderIntentRegistry.All.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.IsTrue(OrderIntentRegistry.All
            .Where(item => item.Submission == "conditional")
            .All(item => item.TriggerKind is "PercentDrop" or "PercentRise" or "PriceBelow" or "PriceAbove"));
    }

    [TestMethod]
    public void Registry_ExposesTheLaymanExitAndBreakoutChoices()
    {
        Assert.IsNotNull(OrderIntentRegistry.Find("profit-book"));
        Assert.IsNotNull(OrderIntentRegistry.Find("stop-loss"));
        Assert.IsNotNull(OrderIntentRegistry.Find("buy-on-rise"));
        Assert.IsNotNull(OrderIntentRegistry.Find("sell-after-drop"));
    }
    [TestMethod]
    [DataRow("wait-buy-drops", "BUY", "PriceBelow")]
    [DataRow("wait-buy-rises", "BUY", "PriceAbove")]
    [DataRow("wait-sell-drops", "SELL", "PriceBelow")]
    [DataRow("wait-sell-rises", "SELL", "PriceAbove")]
    public void ExactPriceChoices_WaitLocallyBeforeSubmittingLimit(string id, string side, string trigger)
    {
        var intent = OrderIntentRegistry.Find(id);
        Assert.IsNotNull(intent);
        Assert.AreEqual("conditional", intent.Submission);
        Assert.AreEqual(side, intent.Action);
        Assert.AreEqual(trigger, intent.TriggerKind);
        Assert.AreEqual("LIMIT", intent.OrderType);
        Assert.AreEqual("trigger-and-limit", intent.PriceField);
    }
}
