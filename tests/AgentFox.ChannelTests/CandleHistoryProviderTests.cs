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
    // The progressive path renders what the archive already holds. Holding nothing, it renders an
    // empty chart, so an empty archive takes the portal top-up even when the caller asked to stay
    // local — one slow first paint beats a blank pane until a backfill arrives.
    [DataRow(0, 60, false, true)]
    [DataRow(1, 60, false, false)]
    public void Portal_top_up_policy_keeps_progressive_chart_reads_local(
        int archivedBars, int requestedSessions, bool allowPortalFallback, bool expected)
    {
        Assert.AreEqual(expected, CandleHistoryProvider.ShouldTopUpArchiveFromPortal(
            archivedBars, requestedSessions, allowPortalFallback));
    }
}
