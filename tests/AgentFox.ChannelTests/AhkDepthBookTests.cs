using System.Text.Json;
using TradingAgent.Feed;

namespace AgentFox.ChannelTests;

/// <summary>
/// The market-depth store behind <c>get_market_depth</c>.
///
/// <para>
/// The portal's depth payload had never been captured when this was written — nothing had ever
/// subscribed to MBP-FEED or MBO-FEED — so the store deliberately keeps rows raw and records the field
/// names it sees. These tests pin the two properties that make that approach safe: an unknown shape is
/// carried through rather than dropped, and MBP is never merged with MBO, since a price level and an
/// individual order are different quantities and conflating them would misstate liquidity.
/// </para>
/// </summary>
[TestClass]
public sealed class AhkDepthBookTests
{
    private static List<JsonElement> Rows(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();

    [TestMethod]
    public void UnknownRowShape_IsCarriedThroughRatherThanDropped()
    {
        // Field names here are invented precisely because the real ones are unknown: the store must not
        // depend on them. A store that only kept recognised fields would silently return an empty book
        // the first time the portal's spelling differed from a guess.
        var book = new AhkDepthBook();
        book.Ingest(
            Rows("""[{"someUnknownPrice":101.5,"someUnknownQty":400}]"""),
            null, "REG", "PPL");

        var entry = book.Get("REG", "PPL");
        Assert.IsNotNull(entry);
        Assert.AreEqual(1, entry.ByPrice.Count);
        StringAssert.Contains(entry.ByPrice[0].ToString(), "101.5");
    }

    [TestMethod]
    public void ObservedFieldNames_AreRecordedSoAShapeCanBeLearned()
    {
        // This is the artefact that lets a typed model be written from production data instead of
        // guessed, so it has to survive.
        var book = new AhkDepthBook();
        book.Ingest(
            Rows("""[{"price":10,"qty":5}]"""),
            Rows("""[{"orderId":"X1","price":10}]"""),
            "REG", "PPL");

        CollectionAssert.AreEquivalent(new[] { "price", "qty" }, book.ObservedMbpKeys.ToList());
        CollectionAssert.AreEquivalent(new[] { "orderId", "price" }, book.ObservedMboKeys.ToList());
    }

    [TestMethod]
    public void ByPriceAndByOrder_AreKeptSeparate()
    {
        var book = new AhkDepthBook();
        book.Ingest(
            Rows("""[{"price":10,"qty":100},{"price":9,"qty":50}]"""),
            Rows("""[{"orderId":"A"},{"orderId":"B"},{"orderId":"C"}]"""),
            "REG", "PPL");

        var entry = book.Get("REG", "PPL")!;
        Assert.AreEqual(2, entry.ByPrice.Count, "two price levels");
        Assert.AreEqual(3, entry.ByOrder.Count, "three individual orders");
        Assert.AreEqual(5, book.RowsSeen);
    }

    [TestMethod]
    public void EachFeedUpdatesIndependently_WithoutClearingTheOther()
    {
        // MBP and MBO arrive in the same response but not necessarily in the same poll, so an update to
        // one must not erase the other — otherwise the ladder blinks out whenever only orders changed.
        var book = new AhkDepthBook();
        book.Ingest(Rows("""[{"price":10}]"""), Rows("""[{"orderId":"A"}]"""), "REG", "PPL");
        book.Ingest(Rows("""[{"price":11}]"""), null, "REG", "PPL");

        var entry = book.Get("REG", "PPL")!;
        StringAssert.Contains(entry.ByPrice[0].ToString(), "11");
        Assert.AreEqual(1, entry.ByOrder.Count, "the order side must survive an MBP-only update");
    }

    [TestMethod]
    public void RowsCarryingTheirOwnSymbol_AreGroupedByIt()
    {
        // The fallback exists because the portal follows one symbol at a time, but if rows do identify
        // themselves they must be trusted over the fallback.
        var book = new AhkDepthBook();
        book.Ingest(
            Rows("""[{"symbol":"OGDC","price":1},{"symbol":"PPL","price":2}]"""),
            null, "REG", "PPL");

        Assert.IsNotNull(book.Get("REG", "OGDC"));
        Assert.IsNotNull(book.Get("REG", "PPL"));
    }

    [TestMethod]
    public void EmptyAndNullPayloads_AreHarmless()
    {
        var book = new AhkDepthBook();
        book.Ingest(null, null, "REG", "PPL");
        book.Ingest([], [], "REG", "PPL");

        Assert.IsNull(book.Get("REG", "PPL"));
        Assert.AreEqual(0, book.RowsSeen);
        Assert.AreEqual(0, book.All().Count);
    }

    [TestMethod]
    public void Clear_DropsBothTheBookAndTheSubscription()
    {
        // Depth is session-scoped: a new broker session starts with no subscription, so a stale
        // "following PPL" would claim a subscription that no longer exists.
        var book = new AhkDepthBook { SubscribedSymbol = "PPL" };
        book.Ingest(Rows("""[{"price":10}]"""), null, "REG", "PPL");

        book.Clear();

        Assert.IsNull(book.Get("REG", "PPL"));
        Assert.IsNull(book.SubscribedSymbol);
    }
}
