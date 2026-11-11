using TradingAgent.Manager;
using TradingAgent.Models;

namespace AgentFox.ChannelTests;

/// <summary>
/// The order dialog shows the execution's summary and nothing else, so a refused order's own reason has to be in it.
/// </summary>
[TestClass]
public sealed class BrokerFailureReasonTests
{
    private static OrderResult Order(bool success, string message) => new() { Success = success, Message = message };

    [TestMethod]
    public void A_refusal_carries_the_brokers_own_words()
    {
        var reason = TradingManager.BrokerFailureReason(
            [[Order(false, "Shares bought today can only be sold from the next trading day. ")]]);
        Assert.AreEqual("One or more broker orders failed: Shares bought today can only be sold from the next trading day.", reason);
    }

    [TestMethod]
    public void Only_refused_orders_speak_and_each_once()
    {
        var reason = TradingManager.BrokerFailureReason(
            [[Order(true, "Working at the broker."), Order(false, "Over_Price_Limit")], [Order(false, "Over_Price_Limit")]]);
        Assert.AreEqual("One or more broker orders failed: Over_Price_Limit", reason);
    }

    [TestMethod]
    public void A_refusal_with_no_words_keeps_the_plain_summary()
    {
        Assert.AreEqual("One or more broker orders failed.", TradingManager.BrokerFailureReason([[Order(false, "  ")]]));
        Assert.AreEqual("One or more broker orders failed.", TradingManager.BrokerFailureReason([]));
    }
}
