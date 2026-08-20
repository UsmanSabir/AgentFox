using System.Text.Json;
using TradingAgent.AhlAnalytics;

namespace AgentFox.ChannelTests;

/// <summary>
/// Deserialisation of the AHL whole-market snapshot.
///
/// <para>
/// This exists because a snapshot that fails to parse is indistinguishable, from the outside, from a
/// portal that could not be reached: <c>AhlAnalyticsClient</c> catches the <see cref="JsonException"/>,
/// logs it, and returns null, and the dashboard then reports "unavailable". So a model regression looks
/// exactly like an outage, and only a test over a real payload separates them.
/// </para>
///
/// <para>
/// The inline fixture is a trimmed copy of a live 2026-08-20 response (market OPEN), keeping one
/// equity, one index, one future and one odd-lot row with every field the real payload carries for
/// them — including the awkward ones: a string market state beside an integer per-symbol state, a
/// fraction for <c>pch</c> against percent-scaled <c>pm</c>/<c>di</c>, nested <c>pp</c>/<c>bt</c>
/// objects, and a populated L1 book.
/// </para>
/// </summary>
[TestClass]
public sealed class AhlSnapshotDeserializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    [TestMethod]
    [DataRow("false")]
    [DataRow("[]")]
    [DataRow("\"-\"")]
    public void OptionalBeta_NonObjectShape_DoesNotDiscardSnapshot(string betaJson)
    {
        var json = "{\"data\":{\"eq\":{\"ADOS\":{\"s\":\"ADOS\",\"bt\":" + betaJson + "}}}}";

        var snapshot = JsonSerializer.Deserialize<AhlMarketSnapshot>(json, Options);

        Assert.IsNotNull(snapshot?.Data?.Equities);
        Assert.IsTrue(snapshot.Data.Equities.TryGetValue("ADOS", out var equity));
        Assert.IsNull(equity.Beta);
    }

    // Trimmed from the live payload. Values are verbatim.
    private const string Fixture = """
    {"status":"ok","message":"","count":0,"data":{
      "st":"OPN","lu":"2026-08-20 11:29:18",
      "eq":{"LUCK":{"d":"2026-08-20 11:29:09","sc":"0804","nm":"Lucky Cement Limited","ds":"",
        "ty":1,"st":1,"o":440.9,"h":441.5,"l":437.5,"c":439.61,"v":152863,"ch":0.31,"pch":0.0007,
        "h52":529.5,"l52":339,"ldcp":439.3,"ldcv":981329,
        "bidp":439.6,"bidv":26,"askp":439.61,"askv":239,
        "avg":439.2,"tr":1204,"var":12.5,"hc":17.5,"sh":1465000000,"ff":439500000,
        "sa":136527017,"sg3y":0.18385,"scagr5y":0.1675,"pat":46629367,"pcagr5y":0.27078,
        "as":300925002,"pm":34.153948445237035,"di":1.0649173624126769,"dps":5,"eps":31.83,
        "eg3y":0.40903,"pr":15.708451146716934,
        "p1w":443.37,"p1m":445.63,"p3m":407.89,"p6m":435.72,"p1y":425.98,"pytd":474.96,
        "pfy":469.52,"p5y":171.98,
        "vw":1250095,"vm":36388820,"v3m":126833855,"v6m":281770044,"vy":506729787,
        "vytd":358426187,"vfy":63198896,"vaw":1250095,"va10d":1661320.6,"vam":1732800.95,
        "va3m":2149726.36,"va6m":2367815.5,"vay":2035059.39,"vaytd":2327442.77,"v30a":1882725.17,
        "sw":0,"sm":0,"s3m":0,"s6m":0,"sy":0,"sytd":0,"sfy":0,
        "xb":false,"xd":false,"xr":false,"sd":false,
        "li":["ACI","ALLSHR","KMI30","KSE100","KSE30"],
        "rsi":39.0359,"std":7.3918,
        "pp":{"pp":440.43,"r1":442.86,"r2":446.43,"r3":448.86,"s1":436.86,"s2":434.43,"s3":430.86},
        "bt":{"1m":1.3447,"1y":1.2719,"3m":1.3743,"6m":1.32},
        "fdd":"2000-01-03 16:00:00","fid":"2018-01-01 14:33:12","etf":null,
        "uc":483.23,"lc":395.37}},
      "in":{"KSE100":{"type":1,"d":"2026-08-20 11:29:00","o":178072.42,"h":178942.31,
        "l":176638.11,"c":176846.36,"v":409765640,"val":27162663152.94,"ldci":177955.51,
        "ldcv":511871806,"ch":-1109.15,"pch":-0.0062,"h52":191032.73,"l52":144119.44,
        "cw":179846.68,"cm":175927.74,"c3m":162896.68,"c6m":172170.29,"cy":149770.75,
        "c5y":47599.82,"cytd":174054.32,"cfy":0,
        "pp":{"pp":178808,"r1":179749.96,"r2":181544.4,"r3":182486.36,"s1":177013.56,
        "s2":176071.6,"s3":174277.15},
        "fdd":"2008-01-01 16:00:00","fid":"2018-01-01 14:30:07"}},
      "fut":{"AGHA-AUG":{"eq":"AGHA","m":"FUT","n":"Agha Steel Ind.Ltd","s":"SUS","st":1,
        "d":"2026-08-20 11:20:11","ld":"2026-06-01","o":7.44,"h":7.55,"l":7.36,"c":7.41,
        "v":309500,"val":2309625,"ch":0.03,"pch":0.0041,"ldcp":7.38,"ldcv":0,
        "bp":0,"bv":0,"ap":0,"av":0,"tr":173,"uc":8.38,"lc":6.38,
        "fut":{"p":"Q","dd":"2026-08-31","dm":"2026-08-01","ltd":"2026-08-28","sm":"S","cm":500}}},
      "odl":{"ACIETF":{"eq":"ACIETF","m":"ODL","n":"","s":"SUS","st":1,
        "d":"2026-08-19 15:08:49","ld":"","o":0,"h":0,"l":0,"c":17.56,"v":0,"val":386.32,
        "ch":0,"pch":0,"ldcp":17.53,"ldcv":22,"bp":0,"bv":0,"ap":0,"av":0,"tr":1,
        "uc":18.96,"lc":15.69,"fut":null,"bnb":null}}
    }}
    """;

    private static AhlSnapshotData Parse()
    {
        var response = JsonSerializer.Deserialize<AhlMarketSnapshot>(Fixture, Options);
        Assert.IsNotNull(response, "the snapshot envelope must deserialise");
        Assert.IsNotNull(response.Data, "the data section must deserialise");
        return response.Data;
    }

    [TestMethod]
    public void LivePayload_Deserialises_WithAllFourInstrumentGroups()
    {
        var data = Parse();

        Assert.AreEqual("OPN", data.MarketState);
        Assert.AreEqual("2026-08-20 11:29:18", data.LastUpdate);
        Assert.AreEqual(1, data.Equities!.Count);
        Assert.AreEqual(1, data.Indices!.Count);
        Assert.AreEqual(1, data.Futures!.Count);
        Assert.AreEqual(1, data.OddLot!.Count);
    }

    [TestMethod]
    public void StringMarketState_AndIntegerSymbolState_BothParse()
    {
        // Both are spelled "st" on the wire at different nesting levels, one a string and one an
        // integer. A model that reused one type for both throws and the whole snapshot is lost.
        var data = Parse();
        Assert.AreEqual("OPN", data.MarketState);
        Assert.AreEqual(1, data.Equities!["LUCK"].State);
    }

    [TestMethod]
    public void L1Book_IsPopulatedWhileTheMarketIsOpen()
    {
        // Captured with the market OPEN, which is what makes this meaningful — every earlier capture
        // was closed and reported zeros, so this is the test that proves the fields carry real data.
        var luck = Parse().Equities!["LUCK"];
        Assert.AreEqual(439.6m, luck.BidPrice);
        Assert.AreEqual(26L, luck.BidVolume);
        Assert.AreEqual(439.61m, luck.AskPrice);
        Assert.AreEqual(239L, luck.AskVolume);
    }

    [TestMethod]
    public void ScaleSensitiveFields_KeepTheirWireScale()
    {
        // pch is a FRACTION while pm and di are already percentages. Mixing them is a silent 100x
        // error, so the model must not normalise either one on the way in.
        var luck = Parse().Equities!["LUCK"];
        Assert.AreEqual(0.0007m, luck.ChangeFraction);
        Assert.AreEqual(34.153948445237035m, luck.NetMarginPercent);
        Assert.AreEqual(1.0649173624126769m, luck.DividendYieldPercent);
    }

    [TestMethod]
    public void NestedPivotAndBetaObjects_Parse()
    {
        var luck = Parse().Equities!["LUCK"];
        Assert.AreEqual(440.43m, luck.PivotPoints!.Pivot);
        Assert.AreEqual(430.86m, luck.PivotPoints.S3);
        // Beta keys are "1m"/"3m"/"6m"/"1y" — not valid C# identifiers, so they only bind through
        // explicit JsonPropertyName attributes.
        Assert.AreEqual(1.3447m, luck.Beta!.OneMonth);
        Assert.AreEqual(1.2719m, luck.Beta.OneYear);
    }

    [TestMethod]
    public void CircuitCapsFloatAndIndexMembership_Parse()
    {
        var luck = Parse().Equities!["LUCK"];
        Assert.AreEqual(483.23m, luck.UpperCap);
        Assert.AreEqual(395.37m, luck.LowerLock);
        Assert.AreEqual(439500000m, luck.FreeFloat);
        CollectionAssert.Contains(luck.ListedIn!.ToList(), "KSE100");
    }

    [TestMethod]
    public void FuturesRow_MapsItsUnderlyingAndTerms()
    {
        // The underlying is what makes a futures row usable at all — without it a contract cannot be
        // related to the spot symbol a decision is about.
        var future = Parse().Futures!["AGHA-AUG"];
        Assert.AreEqual("AGHA", future.Underlying);
        Assert.AreEqual("2026-08-28", future.Terms!.LastTradeDate);
    }

    [TestMethod]
    public void OddLotRow_WithNullNestedObjects_DoesNotThrow()
    {
        // "fut": null and "bnb": null appear on every odd-lot row.
        var odl = Parse().OddLot!["ACIETF"];
        Assert.AreEqual("ODL", odl.Market);
        Assert.IsNull(odl.Terms);
    }

    [TestMethod]
    public void MoversRunOverTheRealPayload_RanksTheOpenSession()
    {
        // End to end: the fixture's single equity traded in the session the snapshot reports, so it
        // must survive the freshness filter and appear.
        var data = Parse();
        var rows = AhlMovers.Run(data, AhlMovers.Screen.MostActive, limit: 5);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("LUCK", rows[0].Symbol);
        Assert.AreEqual("Cement", rows[0].Sector);
        Assert.AreEqual(0.07m, rows[0].ChangePercent, "pch 0.0007 becomes 0.07%");
    }
}
