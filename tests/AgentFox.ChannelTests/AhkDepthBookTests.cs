using System.Text.Json;
using TradingAgent.Feed;

namespace AgentFox.ChannelTests;

/// <summary>
/// The market-depth store behind <c>get_market_depth</c>.
///
/// <para>
/// The shape under test is real: captured live on 2026-08-20 from PPL with the market open. Three
/// quirks of how the portal publishes depth are what these tests pin, because each one produces a
/// plausible-looking wrong answer if mishandled — a book that blinks out, a best ask of zero, or a
/// bid paired with the wrong quantity.
/// </para>
/// </summary>
[TestClass]
public sealed class AhkDepthBookTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>Verbatim from the live capture: two real levels, then the zero-filled tail.</summary>
    private const string MbpJson = """
    [{"orders":3,"volume":5510,"price":238.52,"sOrders":1,"sVolume":82,"sPrice":238.7},
     {"orders":1,"volume":7689,"price":238.5,"sOrders":1,"sVolume":500,"sPrice":238.88},
     {"orders":0,"volume":0,"price":0,"sOrders":0,"sVolume":0,"sPrice":0}]
    """;

    private const string MboJson = """
    [{"price":238.52,"volume":10,"flag":"dc","orderNo":null,"sPrice":238.7,"sVolume":82,"sFlag":"dc","sOrderNo":null},
     {"price":0,"volume":0,"flag":null,"orderNo":null,"sPrice":0,"sVolume":0,"sFlag":null,"sOrderNo":null}]
    """;

    private static List<AhkDepthLevelRow> Levels(string json = MbpJson) =>
        JsonSerializer.Deserialize<List<AhkDepthLevelRow>>(json, Options)!;

    private static List<AhkDepthOrderRow> Orders(string json = MboJson) =>
        JsonSerializer.Deserialize<List<AhkDepthOrderRow>>(json, Options)!;

    [TestMethod]
    public void LiveRowShape_BindsBothLaddersFromOneRow()
    {
        // Each row carries BOTH sides: unprefixed is the bid, s-prefixed the ask. Getting this wrong
        // pairs every bid price with the ask's quantity, which reads as a plausible book.
        var level = Levels()[0];

        Assert.AreEqual(238.52m, level.BidPrice);
        Assert.AreEqual(5510L, level.BidVolume);
        Assert.AreEqual(3, level.BidOrders);
        Assert.AreEqual(238.7m, level.AskPrice);
        Assert.AreEqual(82L, level.AskVolume);
        Assert.AreEqual(1, level.AskOrders);
    }

    [TestMethod]
    public void ZeroPaddedRows_AreDropped()
    {
        // The array is fixed length with a zero tail. Keeping the padding makes "best ask" resolve to
        // 0 and inflates total depth with nothing.
        var book = new AhkDepthBook();
        book.Ingest(Levels(), Orders(), "REG", "PPL");

        var entry = book.Get("REG", "PPL")!;
        Assert.AreEqual(2, entry.Levels.Count, "the zero row must not survive");
        Assert.AreEqual(1, entry.Orders.Count);
        Assert.AreEqual(3, book.RowsSeen, "padding must not be counted either");
    }

    [TestMethod]
    public void EmptyPayload_MeansUnchanged_NotAnEmptyBook()
    {
        // Most polls carry empty arrays because the portal republishes only on change. Clearing on
        // those would make the book blink out several times a second.
        var book = new AhkDepthBook();
        book.Ingest(Levels(), Orders(), "REG", "PPL");
        book.Ingest([], [], "REG", "PPL");
        book.Ingest(null, null, "REG", "PPL");

        var entry = book.Get("REG", "PPL");
        Assert.IsNotNull(entry, "the last known ladder must be retained");
        Assert.AreEqual(2, entry.Levels.Count);
    }

    [TestMethod]
    public void OneSideArriving_DoesNotClearTheOther()
    {
        var book = new AhkDepthBook();
        book.Ingest(Levels(), Orders(), "REG", "PPL");
        book.Ingest(Levels(), null, "REG", "PPL");

        Assert.AreEqual(1, book.Get("REG", "PPL")!.Orders.Count,
            "an MBP-only update must not erase the order side");
    }

    [TestMethod]
    public void DerivedScalars_ComeFromTheTouchAndIgnorePadding()
    {
        var book = new AhkDepthBook();
        book.Ingest(Levels(), Orders(), "REG", "PPL");
        var entry = book.Get("REG", "PPL")!;

        Assert.AreEqual(238.52m, entry.BestBid);
        Assert.AreEqual(238.7m, entry.BestAsk);
        Assert.AreEqual(5510L, entry.BidVolumeAtTouch);
        Assert.AreEqual(82L, entry.AskVolumeAtTouch);
        Assert.AreEqual(0.18m, entry.Spread);

        // 5510 + 7689 bid against 82 + 500 ask — a heavily bid book.
        Assert.AreEqual(13199L, entry.TotalBidVolume);
        Assert.AreEqual(582L, entry.TotalAskVolume);
        Assert.AreEqual(0.9155m, entry.Imbalance);
    }

    [TestMethod]
    public void OneSidedBook_HasNoSpreadRatherThanAFabricatedOne()
    {
        // A bid with nothing offered is normal, especially at a circuit cap. Reporting a spread
        // against a zero ask would invent a number.
        var book = new AhkDepthBook();
        book.Ingest(
            Levels("""[{"orders":1,"volume":100,"price":50,"sOrders":0,"sVolume":0,"sPrice":0}]"""),
            null, "REG", "XYZ");

        var entry = book.Get("REG", "XYZ")!;
        Assert.AreEqual(50m, entry.BestBid);
        Assert.IsNull(entry.BestAsk);
        Assert.IsNull(entry.Spread);
        Assert.AreEqual(1m, entry.Imbalance, "all bid");
    }

    [TestMethod]
    public void RowsWithoutASymbol_AreAttributedToTheSubscription()
    {
        // The payload carries no symbol field at all, which is why the subscribed symbol is
        // authoritative. Ingesting with no symbol must store nothing rather than guess.
        var book = new AhkDepthBook();
        book.Ingest(Levels(), Orders(), "REG", null);

        Assert.AreEqual(0, book.All().Count);
        Assert.AreEqual(0, book.RowsSeen);
    }

    [TestMethod]
    public void AllPaddingPayload_StoresNothing()
    {
        var book = new AhkDepthBook();
        book.Ingest(
            Levels("""[{"orders":0,"volume":0,"price":0,"sOrders":0,"sVolume":0,"sPrice":0}]"""),
            null, "REG", "PPL");

        Assert.IsNull(book.Get("REG", "PPL"));
    }

    [TestMethod]
    public void Clear_DropsBothTheBookAndTheSubscription()
    {
        // Depth is session-scoped: a new session carries no subscription, so a retained
        // "following PPL" would claim one that no longer exists.
        var book = new AhkDepthBook { SubscribedSymbol = "PPL" };
        book.Ingest(Levels(), Orders(), "REG", "PPL");

        book.Clear();

        Assert.IsNull(book.Get("REG", "PPL"));
        Assert.IsNull(book.SubscribedSymbol);
    }
}
