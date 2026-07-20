using TradingAgent.Research;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class PsxNewsFeedTests
{
    private const string SampleRss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0"><channel>
          <item>
            <title>OGDC hits new high</title>
            <link>https://news.example.com/ogdc-high</link>
            <pubDate>Mon, 14 Jul 2025 09:00:00 GMT</pubDate>
            <source url="https://biz.example.com">Business Times</source>
          </item>
          <item>
            <title>Market roundup</title>
            <link>https://news.example.com/roundup</link>
            <pubDate>Tue, 15 Jul 2025 09:00:00 GMT</pubDate>
          </item>
        </channel></rss>
        """;

    [TestMethod]
    public void ParseNewsFeed_ExtractsLinkIntoUrl()
    {
        var headlines = PsxDataClient.ParseNewsFeed(SampleRss, 10);

        Assert.AreEqual(2, headlines.Count);
        Assert.AreEqual("OGDC hits new high", headlines[0].Title);
        Assert.AreEqual("https://news.example.com/ogdc-high", headlines[0].Url);
        Assert.AreEqual("Business Times", headlines[0].Source);
    }

    [TestMethod]
    public void ParseNewsFeed_RespectsMaxAndSkipsEmptyTitles()
    {
        var headlines = PsxDataClient.ParseNewsFeed(SampleRss, 1);
        Assert.AreEqual(1, headlines.Count);
    }

    [TestMethod]
    public void ParseNewsFeed_MalformedXml_ReturnsEmpty()
    {
        var headlines = PsxDataClient.ParseNewsFeed("<not-xml", 10);
        Assert.AreEqual(0, headlines.Count);
    }
}
