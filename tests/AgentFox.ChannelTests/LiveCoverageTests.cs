using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Persistence;
using TradingAgent.Reconciliation;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// <see cref="ProtectiveStopDecisions.LiveCoverage"/>: a stop's coverage judged order by order against the book, so a
/// stop whose order left the book mid-session is re-placed — and one whose order may still be live is not.
/// </summary>
[TestClass]
public sealed class LiveCoverageTests
{
    private static readonly DateOnly Today = new(2026, 11, 30);
    private static readonly DateTime Now = new(2026, 11, 30, 6, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Propagation = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Confirm = TimeSpan.FromSeconds(60);

    private static readonly ProtectiveStop Stop = new()
    {
        StopId = "s1", Symbol = "PPL", StopTrigger = 220m, StopLimit = 217.80m,
        DesiredQuantity = 100, State = "active", LastPlacedSessionDate = Today, PlacedQuantity = 100,
        LastOrderNo = "0411XK1"
    };

    private static ProtectiveStopOrder Order(string? no = "0411XK1", int qty = 100, TimeSpan? age = null,
        string state = "resting", DateTime? firstAbsent = null, string stopId = "s1", DateOnly? day = null) =>
        new(stopId, day ?? Today, no, qty, Now - (age ?? TimeSpan.FromMinutes(30)), state, firstAbsent);

    private static RestingOrder Book(string no, int? qty = 100, string? alternate = null) =>
        new("PPL", "SELL", "STOPLOSS", qty, 220m, no, "", alternate);

    private static BrokerOrderEvent Event(string no, string status, TimeSpan? ago = null) =>
        new(no, "PPL", "SELL", status, 100, 220m, Now - (ago ?? TimeSpan.FromMinutes(1)));

    private static CoverageReading Read(
        IReadOnlyList<ProtectiveStopOrder> orders, IReadOnlyList<RestingOrder>? book = null,
        IReadOnlyList<BrokerOrderEvent>? events = null, IReadOnlyDictionary<string, string>? renumbered = null) =>
        ProtectiveStopDecisions.LiveCoverage(Stop, orders, book ?? [], events ?? [], Now, Today,
            Propagation, Confirm, renumbered);

    [TestMethod]
    public void AnOrderInTheBookCoversWhatTheBookSaysRemains()
    {
        var reading = Read([Order()], [Book("0411XK1", qty: 40)]);

        Assert.AreEqual(40, reading.Covered, "a part-filled stop covers its remainder");
        Assert.AreEqual(OrderPresence.Resting, reading.Sightings.Single().Presence);
    }

    [TestMethod]
    public void ATriggeredStopIsStillFoundByItsShortNumberInTheAlternateColumn()
    {
        Assert.AreEqual(100, Read([Order()], [Book("EX9988776655", alternate: "0411XK1")]).Covered);
    }

    [TestMethod]
    public void AnOrderTheBrokerRenumberedIsFoundUnderItsNewNumber()
    {
        Assert.AreEqual(100, Read([Order()], [Book("0411XK9")],
            renumbered: new Dictionary<string, string> { ["0411XK1"] = "0411XK9" }).Covered);
    }

    [TestMethod]
    public void ACancelOnRecordCoversNothing_EvenForAnOrderPlacedSecondsAgo()
    {
        var reading = Read([Order(age: TimeSpan.FromSeconds(5))], events: [Event("0411XK1", "CLX", TimeSpan.FromSeconds(1))]);

        Assert.AreEqual(0, reading.Covered);
        Assert.AreEqual(OrderPresence.Cancelled, reading.Sightings.Single().Presence);
        Assert.AreEqual(1, reading.Departed.Count());
    }

    [TestMethod]
    public void RejectedAndExpiredCountAsGoneToo()
    {
        Assert.AreEqual(0, Read([Order()], events: [Event("0411XK1", "REJ")]).Covered);
        Assert.AreEqual(0, Read([Order()], events: [Event("0411XK1", "Expired")]).Covered);
    }

    /// <summary>Numbers restart per connection: an older order's cancel under the same number is not this one's.</summary>
    [TestMethod]
    public void ACancelFromBeforeThisOrderWasPlacedIsSomeoneElsesUnderAReusedNumber()
    {
        var reading = Read([Order(age: TimeSpan.FromSeconds(10))],
            events: [Event("0411XK1", "CLX", ago: TimeSpan.FromHours(2))]);

        Assert.AreEqual(100, reading.Covered);
        Assert.AreEqual(OrderPresence.Propagating, reading.Sightings.Single().Presence);
    }

    [TestMethod]
    public void AnOrderTooRecentForTheBookStillCovers()
    {
        Assert.AreEqual(100, Read([Order(age: TimeSpan.FromSeconds(20))]).Covered);
    }

    [TestMethod]
    public void ATradedOrderCoversUntilCustodyShowsTheSale_SoNoStopIsPlacedOverSharesJustSold()
    {
        var reading = Read([Order()], events: [Event("0411XK1", "TRD")]);

        Assert.AreEqual(100, reading.Covered);
        Assert.AreEqual(OrderPresence.Filled, reading.Sightings.Single().Presence);
    }

    [TestMethod]
    public void TradedThenCancelledIsGone_TheRemainderNoLongerRests()
    {
        Assert.AreEqual(0, Read([Order()], events: [Event("0411XK1", "TRD"), Event("0411XK1", "CLX")]).Covered);
    }

    [TestMethod]
    public void AnOrderWithNoNumberIsNeverAssumedGone()
    {
        var reading = Read([Order(no: null)]);

        Assert.AreEqual(100, reading.Covered);
        Assert.AreEqual(OrderPresence.Unidentifiable, reading.Sightings.Single().Presence);
    }

    [TestMethod]
    public void MissingWithNoRecordCoversOnTheFirstReadAndIsGoneOnlyAfterTheConfirmationTime()
    {
        Assert.AreEqual(OrderPresence.AbsentOnce, Read([Order()]).Sightings.Single().Presence);
        Assert.AreEqual(100, Read([Order(firstAbsent: Now.AddSeconds(-30))]).Covered, "not yet confirmed");

        var confirmed = Read([Order(firstAbsent: Now.AddSeconds(-61))]);
        Assert.AreEqual(0, confirmed.Covered);
        Assert.AreEqual(OrderPresence.Gone, confirmed.Sightings.Single().Presence);
    }

    [TestMethod]
    public void AnOrderSeenAgainAfterBeingMissedIsResting()
    {
        Assert.AreEqual(OrderPresence.Resting,
            Read([Order(firstAbsent: Now.AddSeconds(-90))], [Book("0411XK1")]).Sightings.Single().Presence);
    }

    [TestMethod]
    public void AnOrderReleasedForASaleCoversNothingAndIsNotReportedAsDeparted()
    {
        var reading = Read([Order(state: "released")], events: [Event("0411XK1", "CLX")]);

        Assert.AreEqual(0, reading.Covered);
        Assert.AreEqual(0, reading.Departed.Count(), "this system cancelled it on purpose");
    }

    [TestMethod]
    public void AStrandedCancelKeepsCountingForTheSession_SoItIsNotRePlacedBelowTheMarket()
    {
        var reading = Read([Order(state: "stranded")], events: [Event("0411XK1", "CLX")]);

        Assert.AreEqual(100, reading.Covered);
        Assert.AreEqual(0, reading.Departed.Count());
    }

    [TestMethod]
    public void OtherStopsAndOtherDaysAreIgnored_AndCoverageSumsThisStopsOrders()
    {
        var reading = Read(
            [Order("0411XK1", 60), Order("0411XK2", 40), Order("0411XK3", 999, stopId: "other"),
             Order("0411XK4", 999, day: Today.AddDays(-1))],
            [Book("0411XK1", 60), Book("0411XK2", 40)]);

        Assert.AreEqual(100, reading.Covered);
        Assert.AreEqual(2, reading.Sightings.Count);
    }

    // ── The decision it feeds ─────────────────────────────────────────────────

    /// <summary>The inverse of <c>Placement_DoesNotPlaceTwiceInOneSession</c>: placed today, but the book holds nothing.</summary>
    [TestMethod]
    public void Placement_PlacesAgainInTheSameSessionWhenTodaysOrderLeftTheBook()
    {
        var decision = ProtectiveStopDecisions.DecidePlacement(Stop, heldQuantity: 100m, Today, [], liveCovered: 0);

        Assert.AreEqual(PlacementAction.Place, decision.Action, decision.Reason);
        Assert.AreEqual(100, decision.Quantity);
        StringAssert.Contains(decision.Reason, "left the book");
    }

    [TestMethod]
    public void Placement_WithoutAnOrderHistoryKeepsTheLedgerTotal()
    {
        var decision = ProtectiveStopDecisions.DecidePlacement(Stop, heldQuantity: 100m, Today, [], liveCovered: null);

        Assert.AreEqual(PlacementAction.Skip, decision.Action,
            "a stop placed before the ledger existed must not be re-placed on an empty reading");
    }

    // ── The ledger itself ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task TheLedgerRecordsEachOrderAndSetsTheStopsCoverageRatherThanAddingToIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentfox-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = new SqliteTradingRepository(
                Options.Create(new TradingAgentOptions { DatabasePath = Path.Combine(root, "t.db") }),
                new ConfigurationBuilder().Build(), NullLogger<SqliteTradingRepository>.Instance);
            await repo.SaveProtectiveStopAsync(Stop with { PlacedQuantity = 0, LastPlacedSessionDate = null, LastOrderNo = null });
            IProtectiveStopOrderLedger ledger = repo;

            await ledger.RecordProtectiveStopOrderAsync(Order("A1", 100, TimeSpan.FromMinutes(10)), 100);
            await ledger.RecordProtectiveStopOrderAsync(Order("A2", 100, TimeSpan.FromMinutes(1)), placedTotal: 100);

            var stop = (await repo.GetProtectiveStopsAsync()).Single();
            Assert.AreEqual(100, stop.PlacedQuantity, "a re-place replaces the departed order's coverage");
            Assert.AreEqual("A2", stop.LastOrderNo);

            var orders = await ledger.GetProtectiveStopOrdersAsync(Today);
            Assert.AreEqual(2, orders.Count);
            await ledger.UpdateProtectiveStopOrdersAsync([orders[0] with { State = "cancelled" }]);
            Assert.AreEqual("cancelled", (await ledger.GetProtectiveStopOrdersAsync(Today))[0].State);

            Assert.AreEqual(2, await ledger.PruneProtectiveStopOrdersAsync(Today.AddDays(1)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
