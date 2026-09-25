using TradingAgent.AhlAnalytics;
using TradingAgent.Research;

namespace AgentFox.ChannelTests;

/// <summary>
/// Two changes the data sources made on 2026-09-24/25, each of which stopped a whole feed without a
/// single error that named the cause.
/// </summary>
[TestClass]
public sealed class PortalHostAndKeyTests
{
    // Trimmed from the live dps.psx.com.pk home page, 2026-09-25.
    private const string PsxHead =
        """<link rel="stylesheet" href="/static/styles.css?v=1.75"><script>window.__ps = {"lc":"en","tz":"Asia/Karachi","_k":"uvWO-LBXjUUM03UM_B2i4Eepc-P7UTxj3F-3PYAyt2A","rv":"1"};</script><script src="/static/script.js?v=1.75"></script>""";

    [TestMethod]
    public void The_PSX_request_key_is_read_from_the_page()
    {
        Assert.AreEqual("uvWO-LBXjUUM03UM_B2i4Eepc-P7UTxj3F-3PYAyt2A", PsxDataClient.ParseRequestKey(PsxHead));
    }

    [TestMethod]
    public void A_page_without_a_PSX_request_key_yields_none_rather_than_a_guess()
    {
        Assert.IsNull(PsxDataClient.ParseRequestKey("<html><head><title>Not Found</title></head></html>"));
        Assert.IsNull(PsxDataClient.ParseRequestKey("""<script>window.__ps = {"lc":"en"};</script>"""));
        Assert.IsNull(PsxDataClient.ParseRequestKey(null));
    }

    [TestMethod]
    public void Analytics_calls_go_to_the_host_the_sign_in_landed_on()
    {
        // Measured 2026-09-25: one token, 200 on the landing host, 401 on the configured one.
        var api = AhlAnalyticsClient.ApiBaseFrom(
            new Uri("https://ahl.capitalstake.com/dashboard"), "https://data.arifhabibltd.com/");

        Assert.AreEqual("https://ahl.capitalstake.com/", api.ToString());
    }

    [TestMethod]
    public void Analytics_falls_back_to_the_configured_host_when_the_landing_is_unknown()
    {
        Assert.AreEqual("https://data.arifhabibltd.com/",
            AhlAnalyticsClient.ApiBaseFrom(null, "https://data.arifhabibltd.com").ToString());
    }
}
