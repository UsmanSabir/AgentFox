using TradingAgent.Research;

namespace AgentFox.ChannelTests;

/// <summary>
/// Verifies delisted-security detection from the PSX company page. Delisted stocks must be flagged
/// so <c>research_stock</c> can exclude them from research and recommendations. Fixtures mirror the
/// real portal markup: a delisted company renders a <c>tag--del</c> DELISTED badge next to its name;
/// a normally-listed one renders no such badge.
/// </summary>
[TestClass]
public sealed class PsxListingStatusTests
{
    // Real markup from dps.psx.com.pk/company/ENGRO (delisted).
    private const string DelistedPageFragment =
        "<div class=\"quote__name\">Engro Corporation Limited" +
        "<div class=\"tag tag--skim tag--del\">DELISTED</div></div>" +
        "<div class=\"quote__sector\"><span>FERTILIZER</span></div>";

    // Real markup from dps.psx.com.pk/company/OGDC (actively listed — no status badge).
    private const string ListedPageFragment =
        "<div class=\"quote__name\">Oil &amp; Gas Development Company Limited</div>" +
        "<div class=\"quote__sector\"><span>OIL &amp; GAS EXPLORATION COMPANIES</span></div>";

    [TestMethod]
    public void ParseListingStatus_DelistedCompany_IsDelisted()
    {
        var status = PsxDataClient.ParseListingStatus("engro", DelistedPageFragment);

        Assert.AreEqual("ENGRO", status.Symbol);
        Assert.AreEqual(true, status.IsDelisted);
        Assert.AreEqual("DELISTED", status.StatusLabel);
    }

    [TestMethod]
    public void ParseListingStatus_ListedCompany_NotDelisted()
    {
        var status = PsxDataClient.ParseListingStatus("OGDC", ListedPageFragment);

        Assert.AreEqual(false, status.IsDelisted);
        Assert.IsNull(status.StatusLabel);
    }

    [TestMethod]
    public void ParseListingStatus_DelistedClassOnly_StillDetected()
    {
        // Class present without a plain-text DELISTED label — the modifier class alone is decisive.
        var status = PsxDataClient.ParseListingStatus("XYZ", "<div class=\"tag tag--del\"></div>");

        Assert.AreEqual(true, status.IsDelisted);
    }

    [TestMethod]
    public void ParseListingStatus_SimilarClassName_NotAFalsePositive()
    {
        // "tag--delta" must not be mistaken for the delisted modifier "tag--del".
        var status = PsxDataClient.ParseListingStatus("XYZ", "<div class=\"tag tag--delta\">DELTA</div>");

        Assert.AreEqual(false, status.IsDelisted);
    }

    [TestMethod]
    public void ParseListingStatus_EmptyPage_IsUnknown()
    {
        var status = PsxDataClient.ParseListingStatus("XYZ", "");

        Assert.IsNull(status.IsDelisted);
        Assert.IsNotNull(status.Error);
    }
}
