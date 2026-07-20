using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Research;
using TradingAgent.Tools;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class ResearchIndexToolTests
{
    [TestMethod]
    public async Task InvalidIndexSymbol_IsRejectedBeforeNetworkAccess()
    {
        var client = new PsxDataClient(
            Options.Create(new TradingAgentOptions()),
            NullLogger<PsxDataClient>.Instance);
        var tool = new ResearchIndexTool(client, NullLogger<ResearchIndexTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["index"] = "KSE30/../../etc"
        });

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Invalid PSX index symbol");
    }
}
