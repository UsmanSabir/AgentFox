using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Models;
using TradingAgent.Observability;
using TradingAgent.Persistence;
using TradingAgent.Reconciliation;
using TradingAgent.Trading;

namespace AgentFox.ChannelTests;

/// <summary>
/// A keep-working order the broker RENUMBERED, and the end-of-day backstop for one it cannot link.
///
/// <para>
/// MEASURED 2026-10-15, AHL, CNERGY: a keep-working SELL of 161 placed at 08:47, before the open, was
/// acknowledged as pending order <c>5460</c>; it filled at 10:31 as <c>0010TMX4O3000PRF</c>, the only id
/// the trade log reports. The intent joined fills on <c>5460</c>, read "no fill", and would have sold the
/// same 161 shares again at the next open. These tests replay that sequence against a real repository.
/// </para>
/// </summary>
[TestClass]
public sealed class PersistentOrderRenumberingTests
{
    private const string Pending = "5460";
    private const string Exchange = "0010TMX4O3000PRF";

    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-renumber-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FixedCalendar(bool open, bool dayEnded) : IMarketCalendar
    {
        public MarketStatus GetStatus(DateTime? utcNow = null)
        {
            var pktNow = DateTime.UtcNow.AddHours(5);
            return new MarketStatus(open, pktNow, open ? "open" : "closed",
                NextOpenPkt: dayEnded ? pktNow.Date.AddDays(1).AddHours(9.5) : null);
        }
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5));

    private SqliteTradingRepository NewRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspaces:0"] = _root })
            .Build();
        return new SqliteTradingRepository(
            Options.Create(new TradingAgentOptions { DatabasePath = "trading/trading.db" }),
            configuration,
            NullLogger<SqliteTradingRepository>.Instance);
    }

    /// <summary>
    /// Only what a lifecycle pass with nothing resting touches. The order path, the window and the
    /// canceller are null on purpose: if either branch under test starts reaching for them, it fails here.
    /// </summary>
    private static PersistentOrderWorker Worker(
        ITradingRepository repository, BrokerReconciliationSnapshot snapshot, IMarketCalendar calendar,
        TradingActivityLog? activity = null)
    {
        var reconciliation = new TradingReconciliationState();
        reconciliation.Update(snapshot);
        return new PersistentOrderWorker(
            repository, null!, null!, null!, calendar, reconciliation, null!, null!, null!, null!,
            Options.Create(new TradingAgentOptions()), NullLogger<PersistentOrderWorker>.Instance,
            activity ?? new TradingActivityLog());
    }

    private static BrokerReconciliationSnapshot Snapshot(
        IReadOnlyList<BrokerFill>? fills = null, IReadOnlyList<BrokerOrderRenumbering>? renumberings = null) =>
        new(true, true, "ok", DateTime.UtcNow)
        {
            Fills = fills ?? [],
            OrderRenumberings = renumberings ?? []
        };

    private static BrokerFill CnergyFill(string orderNo, int quantity = 161, decimal price = 12.97m) =>
        new(orderNo, "CNERGY", "SEL", quantity, price, DateTime.UtcNow.AddMinutes(-5));

    /// <summary>
    /// The CNERGY intent as it stood at 10:31: placed today pre-open, acknowledged as <see cref="Pending"/>,
    /// under a real execution whose broker-order row carries that number.
    /// </summary>
    private static async Task<(string IntentId, string PlacementId, string ExecutionId)> PlacePreOpenAsync(
        SqliteTradingRepository repository)
    {
        var intent = new PersistentOrderIntent
        {
            IntentId = Guid.NewGuid().ToString("N"),
            Symbol = "CNERGY",
            Action = "SELL",
            Quantity = 161,
            OrderType = "LIMIT",
            Price = 12.97m,
            ExpiresUtc = DateTime.UtcNow.AddDays(2),
            CreatedUtc = DateTime.UtcNow.AddDays(-1)
        };
        await repository.SavePersistentOrderAsync(intent);
        Assert.IsTrue((await repository.TryClaimPersistentOrderAttemptAsync(intent.IntentId, Today)).Acquired);

        var execution = await repository.TryBeginExecutionAsync(
            "persistent:" + intent.IntentId, "{}", "test");
        await repository.RecordBrokerOrdersAsync(execution.ExecutionId,
        [
            new OrderResult { Success = true, OrderId = Pending, Symbol = "CNERGY", Action = "SELL", Quantity = 161 }
        ]);

        var placement = new PersistentOrderPlacement
        {
            PlacementId = Guid.NewGuid().ToString("N"),
            IntentId = intent.IntentId,
            SessionDate = Today,
            Attempt = 1,
            Quantity = 161,
            BrokerOrderNo = Pending,
            ExecutionId = execution.ExecutionId,
            State = "accepted",
            RequestedPrice = 12.97m,
            SubmittedPrice = 12.97m,
            Message = "Accepted by AHL, order status PEN.",
            CreatedUtc = DateTime.UtcNow.AddHours(-2)
        };
        await repository.RecordPersistentOrderPlacementAsync(placement, "resting", "placed");
        return (intent.IntentId, placement.PlacementId, execution.ExecutionId);
    }

    // ── The pure decisions ───────────────────────────────────────────────────

    private static PersistentOrderIntent Intent(string symbol = "CNERGY", string action = "SELL") => new()
    {
        IntentId = "i1", Symbol = symbol, Action = action, Quantity = 161, OrderType = "LIMIT",
        Price = 12.97m, ExpiresUtc = DateTime.UtcNow.AddDays(2)
    };

    private static PersistentOrderPlacement Placement(string? orderNo = Pending, DateOnly? date = null) => new()
    {
        PlacementId = "p1", IntentId = "i1", SessionDate = date ?? Today, Attempt = 1, Quantity = 161,
        BrokerOrderNo = orderNo, SubmittedPrice = 12.97m, CreatedUtc = DateTime.UtcNow.AddHours(-2)
    };

    [TestMethod]
    public void A_renumbering_moves_todays_placement_onto_the_exchange_id()
    {
        var found = PersistentOrderDecisions.FindRenumberings(Intent(), [Placement()],
            Snapshot(renumberings: [new BrokerOrderRenumbering(Pending, Exchange, "CNERGY")]), Today);

        Assert.AreEqual(Exchange, found.Single().To);
    }

    [TestMethod]
    public void A_number_recorded_on_an_earlier_day_is_never_renumbered_from_todays_books()
    {
        // A pending number is a server sequence and the link is today's activity log. Equal strings from
        // two days are not one order.
        var found = PersistentOrderDecisions.FindRenumberings(Intent(), [Placement(date: Today.AddDays(-1))],
            Snapshot(renumberings: [new BrokerOrderRenumbering(Pending, Exchange, "CNERGY")]), Today);

        Assert.AreEqual(0, found.Count);
    }

    [TestMethod]
    public void A_renumbering_on_another_symbol_is_not_ours()
    {
        var found = PersistentOrderDecisions.FindRenumberings(Intent(), [Placement()],
            Snapshot(renumberings: [new BrokerOrderRenumbering(Pending, Exchange, "SNGP")]), Today);

        Assert.AreEqual(0, found.Count);
    }

    [TestMethod]
    public void Two_targets_for_one_number_are_refused_rather_than_guessed()
    {
        var found = PersistentOrderDecisions.FindRenumberings(Intent(), [Placement()],
            Snapshot(renumberings:
            [
                new BrokerOrderRenumbering(Pending, Exchange, "CNERGY"),
                new BrokerOrderRenumbering(Pending, "0010TMX4O3000ZZZ", "CNERGY")
            ]), Today);

        Assert.AreEqual(0, found.Count);
    }

    [TestMethod]
    public void An_unlinked_fill_matching_side_size_and_limit_is_a_candidate()
    {
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Pending };
        var found = PersistentOrderDecisions.FindUnlinkedMatchingFills(
            Intent(), Placement(), own, Snapshot(fills: [CnergyFill(Exchange)]));

        Assert.AreEqual(Exchange, found.Single().OrderNo);
    }

    [TestMethod]
    public void A_fill_that_is_not_this_orders_shape_is_no_candidate()
    {
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Pending };
        var snapshot = Snapshot(fills:
        [
            CnergyFill(Exchange, price: 12.90m),                                   // below a SELL limit
            CnergyFill("A", quantity: 200),                                        // larger than the order
            new BrokerFill("B", "CNERGY", "BUY", 161, 12.97m, DateTime.UtcNow),     // the other side
            new BrokerFill("C", "SNGP", "SEL", 161, 12.97m, DateTime.UtcNow),       // another stock
            CnergyFill(Pending)                                                    // already linked
        ]);

        Assert.AreEqual(0,
            PersistentOrderDecisions.FindUnlinkedMatchingFills(Intent(), Placement(), own, snapshot).Count);
    }

    [TestMethod]
    public void A_sell_filled_above_its_limit_still_matches()
    {
        // A pre-open order fills at the opening price, which can be better than the limit.
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Pending };
        var found = PersistentOrderDecisions.FindUnlinkedMatchingFills(
            Intent(), Placement(), own, Snapshot(fills: [CnergyFill(Exchange, price: 13.05m)]));

        Assert.AreEqual(1, found.Count);
    }

    // ── The worker, against a real repository ────────────────────────────────

    [TestMethod]
    public async Task The_CNERGY_fill_is_credited_once_the_pending_number_is_renumbered()
    {
        var repository = NewRepository();
        var (intentId, _, executionId) = await PlacePreOpenAsync(repository);
        var snapshot = Snapshot([CnergyFill(Exchange)], [new BrokerOrderRenumbering(Pending, Exchange, "CNERGY")]);
        // Reconciliation records the fill before any lifecycle pass sees it — as an `external:` order,
        // since no execution of ours names that number yet. That is the state the incident left.
        await repository.RecordFillsAsync(snapshot.Fills);

        await Worker(repository, snapshot, new FixedCalendar(open: true, dayEnded: false)).RunNowAsync();

        var intent = (await repository.GetPersistentOrderAsync(intentId))!;
        Assert.AreEqual("fulfilled", intent.State, intent.StateReason);
        Assert.AreEqual(161, intent.FilledQuantity);
        Assert.AreEqual(Exchange, intent.LastOrderNo, "the card must show the number the broker uses");
        Assert.AreEqual(Exchange, (await repository.GetPersistentOrderPlacementsAsync(intentId)).Single().BrokerOrderNo);

        var own = await repository.FindOwnBrokerOrderNumbersAsync([Exchange]);
        Assert.IsTrue(own.Contains(Exchange),
            $"the fill must now belong to execution {executionId}, not to an external order");
    }

    [TestMethod]
    public async Task Renumbering_before_the_fill_still_credits_the_fill_when_it_arrives()
    {
        // The order queues at the open and fills an hour later; the move happens on the first pass.
        var repository = NewRepository();
        var (intentId, _, _) = await PlacePreOpenAsync(repository);
        var renumbering = new BrokerOrderRenumbering(Pending, Exchange, "CNERGY");
        var calendar = new FixedCalendar(open: true, dayEnded: false);

        await Worker(repository, Snapshot(renumberings: [renumbering]), calendar).RunNowAsync();
        Assert.AreEqual(0, (await repository.GetPersistentOrderAsync(intentId))!.FilledQuantity);

        var filled = Snapshot([CnergyFill(Exchange)], [renumbering]);
        await repository.RecordFillsAsync(filled.Fills);
        await Worker(repository, filled, calendar).RunNowAsync();

        var intent = (await repository.GetPersistentOrderAsync(intentId))!;
        Assert.AreEqual("fulfilled", intent.State, intent.StateReason);
        Assert.IsTrue((await repository.FindOwnBrokerOrderNumbersAsync([Exchange])).Contains(Exchange),
            "a fill recorded after the move must land on our row, not create an external one");
    }

    [TestMethod]
    public async Task An_unlinked_matching_fill_holds_the_intent_instead_of_lapsing_it()
    {
        // The backstop: no renumbering reported, the market has closed for the day. Lapsing would place
        // all 161 again at the next open.
        var repository = NewRepository();
        var (intentId, _, _) = await PlacePreOpenAsync(repository);
        var snapshot = Snapshot([CnergyFill(Exchange)]);
        await repository.RecordFillsAsync(snapshot.Fills);
        var activity = new TradingActivityLog();

        await Worker(repository, snapshot, new FixedCalendar(open: false, dayEnded: true), activity).RunNowAsync();

        var intent = (await repository.GetPersistentOrderAsync(intentId))!;
        Assert.AreEqual("attention", intent.State);
        StringAssert.Contains(intent.StateReason!, Exchange);
        Assert.AreEqual(0, intent.FilledQuantity, "a shape match is held for a person, never credited");
        Assert.AreEqual("accepted", (await repository.GetPersistentOrderPlacementsAsync(intentId)).Single().State,
            "the placement stays accepted, so the next day settles it from holdings");
        Assert.IsTrue(activity.Snapshot().Any(e => e.Message.Contains("could not be linked")));
    }

    [TestMethod]
    public async Task With_no_candidate_fill_the_day_ends_as_before()
    {
        var repository = NewRepository();
        var (intentId, _, _) = await PlacePreOpenAsync(repository);

        await Worker(repository, Snapshot(), new FixedCalendar(open: false, dayEnded: true)).RunNowAsync();

        Assert.AreEqual("active", (await repository.GetPersistentOrderAsync(intentId))!.State);
        Assert.AreEqual("lapsed", (await repository.GetPersistentOrderPlacementsAsync(intentId)).Single().State);
    }

    [TestMethod]
    public async Task A_fill_on_another_order_of_ours_does_not_hold_the_intent()
    {
        // The operator's own dashboard sell of the same stock is not a candidate — the ledger knows it.
        var repository = NewRepository();
        var (intentId, _, _) = await PlacePreOpenAsync(repository);
        var dashboard = await repository.TryBeginExecutionAsync("dashboard-sell", "{}", "test");
        await repository.RecordBrokerOrdersAsync(dashboard.ExecutionId,
        [
            new OrderResult { Success = true, OrderId = "0010TMX4O300BPCW", Symbol = "CNERGY", Action = "SELL", Quantity = 160 }
        ]);
        var snapshot = Snapshot([CnergyFill("0010TMX4O300BPCW", quantity: 160, price: 13.06m)]);
        await repository.RecordFillsAsync(snapshot.Fills);

        await Worker(repository, snapshot, new FixedCalendar(open: false, dayEnded: true)).RunNowAsync();

        Assert.AreEqual("active", (await repository.GetPersistentOrderAsync(intentId))!.State);
        Assert.AreEqual("lapsed", (await repository.GetPersistentOrderPlacementsAsync(intentId)).Single().State);
    }

    [TestMethod]
    public async Task A_renumbering_is_a_compare_and_set_and_applies_once()
    {
        var repository = NewRepository();
        var (_, placementId, _) = await PlacePreOpenAsync(repository);

        Assert.IsTrue(await repository.RenumberPersistentOrderPlacementAsync(placementId, Pending, Exchange));
        Assert.IsFalse(await repository.RenumberPersistentOrderPlacementAsync(placementId, Pending, Exchange));
    }
}
