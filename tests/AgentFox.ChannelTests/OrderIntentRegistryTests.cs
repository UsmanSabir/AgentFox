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
            .All(item => item.TriggerKind is "PercentDrop" or "PercentRise"));
    }

    [TestMethod]
    public void Registry_ExposesTheLaymanExitAndBreakoutChoices()
    {
        Assert.IsNotNull(OrderIntentRegistry.Find("profit-book"));
        Assert.IsNotNull(OrderIntentRegistry.Find("stop-loss"));
        Assert.IsNotNull(OrderIntentRegistry.Find("buy-on-rise"));
        Assert.IsNotNull(OrderIntentRegistry.Find("sell-after-drop"));
    }
}
