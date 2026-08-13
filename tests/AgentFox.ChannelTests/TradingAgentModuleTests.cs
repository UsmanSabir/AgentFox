using System.Reflection;
using System.Text.Json;
using TradingAgent;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

[TestClass]
public class TradingAgentModuleTests
{
    [TestMethod]
    public void BuildSpecialistToolNames_ExposesExecutionTools()
    {
        var method = typeof(TradingAgentModule).GetMethod(
            "BuildSpecialistToolNames",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method);

        var names = (IReadOnlyList<string>)method!.Invoke(null, [null, false])!;

        CollectionAssert.Contains(names.ToList(), "place_order");
        CollectionAssert.Contains(names.ToList(), "place_orders");
    }

    [TestMethod]
    public void SerializeAlertForSse_UsesSameCamelCaseContractAsRestApi()
    {
        var method = typeof(TradingAgentModule).GetMethod(
            "SerializeAlertForSse",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method);

        var alert = new AlertRecord
        {
            AlertId = "alert-1",
            Symbol = "ATRL",
            Kind = "ResistanceBreakout",
            Severity = "High",
            Price = 1_049m,
            Interval = "1D",
            Summary = "Test alert",
            State = "new",
            RaisedUtc = DateTime.UtcNow,
            SessionDate = "2026-08-13"
        };

        var json = (string)method!.Invoke(null, [alert])!;
        using var document = JsonDocument.Parse(json);

        Assert.IsTrue(document.RootElement.TryGetProperty("alertId", out _));
        Assert.IsTrue(document.RootElement.TryGetProperty("severity", out _));
        Assert.IsFalse(document.RootElement.TryGetProperty("AlertId", out _));
    }
}
