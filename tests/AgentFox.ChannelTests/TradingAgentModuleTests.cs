using System.Reflection;
using TradingAgent;

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
}
