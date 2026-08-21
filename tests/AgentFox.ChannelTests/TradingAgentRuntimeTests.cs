using System.Text.Json;
using TradingAgent;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// Contracts of the trading engine's composition surface. These used to reflect over private
/// statics on TradingAgentModule; the members are now <c>internal</c> on the Core types that own
/// them, so a rename or signature change fails the BUILD instead of failing a null check at run
/// time. Core grants this assembly InternalsVisibleTo for exactly this.
/// </summary>
[TestClass]
public class TradingAgentRuntimeTests
{
    [TestMethod]
    public void BuildSpecialistToolNames_ExposesTradingAndDiscoveryTools()
    {
        var names = TradingAgentRuntime.BuildSpecialistToolNames(
            webSearchProvider: null,
            researchWebEnabled: false).ToList();

        CollectionAssert.Contains(names, "place_order");
        CollectionAssert.Contains(names, "place_orders");
        CollectionAssert.Contains(names, "market_movers");
        CollectionAssert.Contains(names, "stock_dossier");
        CollectionAssert.Contains(names, "get_market_depth");
    }

    [TestMethod]
    public void SerializeAlertForSse_UsesSameCamelCaseContractAsRestApi()
    {
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

        var json = TradingCoreEndpoints.SerializeAlertForSse(alert);
        using var document = JsonDocument.Parse(json);

        Assert.IsTrue(document.RootElement.TryGetProperty("alertId", out _));
        Assert.IsTrue(document.RootElement.TryGetProperty("severity", out _));
        Assert.IsFalse(document.RootElement.TryGetProperty("AlertId", out _));
    }
}
