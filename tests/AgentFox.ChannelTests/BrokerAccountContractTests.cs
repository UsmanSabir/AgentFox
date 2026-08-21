using TradingAgent.Broker;
using TradingAgent.Feed;
using TradingAgent.Models;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class BrokerAccountContractTests
{
    [TestMethod]
    public void AhkHolding_MapsToBrokerNeutralFinancialFields()
    {
        var mapped = AhkBrokerAccountReader.MapHolding(new HoldingPosition
        {
            Symbol = "OGDC",
            Quantity = 25,
            AverageBuyPrice = 210.5m,
            CurrentPrice = 218m,
            InvestmentValue = 5262.5m,
            CurrentValue = 5450m,
            ProfitLoss = 187.5m,
            ProfitLossPercent = 3.56m
        });

        Assert.AreEqual("OGDC", mapped.InstrumentId);
        Assert.AreEqual("PSX", mapped.Exchange);
        Assert.AreEqual("equity", mapped.AssetType);
        Assert.AreEqual(25m, mapped.Quantity);
        Assert.AreEqual(5450m, mapped.MarketValue);
        Assert.AreEqual("PKR", mapped.Currency);
    }

    [TestMethod]
    public void AhkOrder_NormalizesSideWithoutLeakingPortalVocabularyIntoDashboard()
    {
        var mapped = AhkBrokerAccountReader.MapOrder(new AhkOutstandingOrder
        {
            OrderNo = " 12345 ",
            HOrderNo = "H-9",
            Scrip = " ogdc ",
            Market = "REG",
            Type = "SEL",
            Remaining = 12,
            Price = 220m,
            Action = "QUE",
            Flag = "N"
        });

        Assert.AreEqual("12345", mapped.OrderId);
        Assert.AreEqual("OGDC", mapped.Symbol);
        Assert.AreEqual("SELL", mapped.Side);
        Assert.AreEqual(12m, mapped.RemainingQuantity);
        Assert.AreEqual("QUE", mapped.Status);
        Assert.AreEqual("N", mapped.Attributes["flag"]);
    }
}
