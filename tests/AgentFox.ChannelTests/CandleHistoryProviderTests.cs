using TradingAgent.Research;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class CandleHistoryProviderTests
{
    [TestMethod]
    [DataRow(0, 60, true, true)]
    [DataRow(19, 60, true, true)]
    [DataRow(20, 60, true, false)]
    [DataRow(4, 5, true, true)]
    [DataRow(5, 5, true, false)]
    [DataRow(0, 60, false, false)]
    public void Portal_top_up_policy_keeps_progressive_chart_reads_local(
        int archivedBars, int requestedSessions, bool allowPortalFallback, bool expected)
    {
        Assert.AreEqual(expected, CandleHistoryProvider.ShouldTopUpArchiveFromPortal(
            archivedBars, requestedSessions, allowPortalFallback));
    }
}
