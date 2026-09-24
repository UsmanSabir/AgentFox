using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Persistence;
using TradingAgent.Trading;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// The pull-back rule for a take-profit fired ahead of its limit, and the storage that makes it safe.
///
/// <para>
/// The two properties worth the most here are both refusals. A persistent order that a PERSON
/// cancelled must never re-arm — re-arming would put back an order the operator just took away — so the
/// re-arm is gated on a marker only the pull-back writes, and that gate lives in the UPDATE statement.
/// And the overnight hand-back makes no broker call, so it must never run while an order of today's may
/// still be resting: Friday's midday break is the case pinned below.
/// </para>
/// </summary>
[TestClass]
public sealed class ArmedOrderPullbackTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    // ── The rule ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void An_order_without_a_pullback_level_is_not_watched()
    {
        var decision = Decide(Fired() with { PullbackPrice = null }, Intent("resting"), price: 100m);
        Assert.AreEqual(PullbackAction.None, decision.Action);
    }

    [TestMethod]
    public void An_order_that_has_not_fired_is_not_watched()
    {
        var decision = Decide(Fired() with { State = "armed" }, Intent("resting"), price: 100m);
        Assert.AreEqual(PullbackAction.None, decision.Action);
    }

    [TestMethod]
    public void A_resting_sell_above_the_pullback_level_is_left_alone()
    {
        var decision = Decide(Fired(), Intent("resting"), price: 240.50m);
        Assert.AreEqual(PullbackAction.None, decision.Action);
    }

    [TestMethod]
    public void A_price_at_the_pullback_level_pulls_the_sell_back()
    {
        var decision = Decide(Fired(), Intent("resting"), price: 240.49m);
        Assert.AreEqual(PullbackAction.PullBack, decision.Action);
    }

    [TestMethod]
    public void A_partly_filled_resting_sell_is_pulled_back_too()
    {
        var decision = Decide(Fired(), Intent("partial", filled: 30), price: 239m);
        Assert.AreEqual(PullbackAction.PullBack, decision.Action);
    }

    [TestMethod]
    public void No_live_price_means_no_pullback()
    {
        var decision = Decide(Fired(), Intent("resting"), price: null);
        Assert.AreEqual(PullbackAction.None, decision.Action);
    }

    [DataTestMethod]
    [DataRow("placing")]
    [DataRow("expiring")]
    [DataRow("cancelling")]
    [DataRow("attention")]
    public void A_state_the_persistent_worker_is_resolving_is_left_to_it(string state)
    {
        var decision = Decide(Fired(), Intent(state), price: 230m);
        Assert.AreEqual(PullbackAction.None, decision.Action);
    }

    [TestMethod]
    public void A_sell_cancelled_by_a_person_is_not_rearmed()
    {
        // No marker: this cancel was not the pull-back's.
        var decision = Decide(Fired(), Intent("cancelled"), price: 230m);
        Assert.AreEqual(PullbackAction.Finish, decision.Action);
    }

    [TestMethod]
    public void A_filled_sell_stops_being_watched()
    {
        var decision = Decide(Fired(), Intent("fulfilled", filled: 78), price: 230m);
        Assert.AreEqual(PullbackAction.Finish, decision.Action);
    }

    [TestMethod]
    public void A_missing_persistent_order_stops_being_watched()
    {
        var decision = Decide(Fired(), intent: null, price: 230m);
        Assert.AreEqual(PullbackAction.Finish, decision.Action);
    }

    [DataTestMethod]
    [DataRow("cancelled", PullbackAction.Rearm)]
    [DataRow("expired", PullbackAction.Rearm)]
    [DataRow("fulfilled", PullbackAction.Finish)]
    [DataRow("attention", PullbackAction.Finish)]
    [DataRow("cancelling", PullbackAction.Wait)]
    [DataRow("resting", PullbackAction.Wait)]
    public void A_requested_pullback_settles_on_the_persistent_orders_outcome(string state, PullbackAction expected)
    {
        var order = Fired() with { PullbackRequestedUtc = DateTime.UtcNow };
        Assert.AreEqual(expected, Decide(order, Intent(state), price: 230m).Action);
    }

    [TestMethod]
    public void After_the_close_an_idle_order_from_an_earlier_day_is_handed_back()
    {
        var decision = Decide(Fired(), Intent("active", lastAttempt: Today.AddDays(-1)),
            price: null, marketIsOpen: false, sessionEnded: false);
        Assert.AreEqual(PullbackAction.HandBack, decision.Action);
    }

    [TestMethod]
    public void Once_todays_session_has_ended_todays_idle_order_is_handed_back()
    {
        var decision = Decide(Fired(), Intent("partial", filled: 10, lastAttempt: Today),
            price: null, marketIsOpen: false, sessionEnded: true);
        Assert.AreEqual(PullbackAction.HandBack, decision.Action);
    }

    [TestMethod]
    public void The_Friday_midday_break_never_hands_back_an_order_placed_that_morning()
    {
        // Shut, but the next open is TODAY: the morning's order may still be resting, and the
        // hand-back makes no broker call to find out.
        var decision = Decide(Fired(), Intent("partial", filled: 10, lastAttempt: Today),
            price: null, marketIsOpen: false, sessionEnded: false);
        Assert.AreEqual(PullbackAction.None, decision.Action);
    }

    [TestMethod]
    public void A_resting_order_is_never_handed_back_without_a_cancel()
    {
        var decision = Decide(Fired(), Intent("resting", lastAttempt: Today.AddDays(-1)),
            price: null, marketIsOpen: false, sessionEnded: true);
        Assert.AreEqual(PullbackAction.None, decision.Action);
    }

    // ── The re-arm ────────────────────────────────────────────────────────────

    [TestMethod]
    public void A_pullback_rearms_at_the_early_trigger_and_counts_itself()
    {
        var rearm = ArmedOrderPullback.Rearm(Fired(), Intent("cancelled"), bounce: true)!;

        Assert.AreEqual(78, rearm.Quantity);
        Assert.AreEqual(242.92m, rearm.TriggerPrice);
        Assert.AreEqual(240.49m, rearm.PullbackPrice);
        Assert.AreEqual(1, rearm.PullbackCount);
        Assert.AreEqual(1, rearm.RearmCount);
    }

    [TestMethod]
    public void At_the_cap_it_rearms_at_its_limit_with_no_pullback()
    {
        var order = Fired() with { PullbackCount = ArmedOrderPullback.MaxPullbacks - 1, RearmCount = 3 };
        var rearm = ArmedOrderPullback.Rearm(order, Intent("cancelled"), bounce: true)!;

        // Exactly how a take-profit behaved before pull-backs existed: fires at its limit, works
        // until filled.
        Assert.AreEqual(244.14m, rearm.TriggerPrice);
        Assert.IsNull(rearm.PullbackPrice);
        Assert.AreEqual(ArmedOrderPullback.MaxPullbacks, rearm.PullbackCount);
        Assert.AreEqual(4, rearm.RearmCount);
    }

    [TestMethod]
    public void A_hand_back_at_the_close_does_not_count_toward_the_cap()
    {
        var order = Fired() with { PullbackCount = ArmedOrderPullback.MaxPullbacks - 1 };
        var rearm = ArmedOrderPullback.Rearm(order, Intent("cancelled"), bounce: false)!;

        Assert.AreEqual(ArmedOrderPullback.MaxPullbacks - 1, rearm.PullbackCount);
        Assert.AreEqual(242.92m, rearm.TriggerPrice);
        Assert.AreEqual(240.49m, rearm.PullbackPrice);
    }

    [TestMethod]
    public void A_part_filled_sell_rearms_for_what_is_left()
    {
        var rearm = ArmedOrderPullback.Rearm(Fired(), Intent("cancelled", filled: 30), bounce: true)!;
        Assert.AreEqual(48, rearm.Quantity);
        StringAssert.Contains(rearm.Reason, "30 share(s) sold");
    }

    [TestMethod]
    public void A_sell_that_filled_in_full_is_not_rearmed()
    {
        Assert.IsNull(ArmedOrderPullback.Rearm(Fired(), Intent("cancelled", filled: 78), bounce: true));
    }

    [TestMethod]
    public void Each_firing_gets_its_own_persistent_order_id()
    {
        Assert.AreEqual("armed-tp1", ArmedOrderPullback.IntentIdFor(Fired()));
        Assert.AreEqual("armed-tp1-r2", ArmedOrderPullback.IntentIdFor(Fired() with { RearmCount = 2 }));
    }

    // ── Storage ───────────────────────────────────────────────────────────────

    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"agentfox-pullback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    [TestMethod]
    public async Task The_pullback_fields_round_trip()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(Armed() with { PullbackCount = 1, RearmCount = 2 });

        var stored = (await repository.GetArmedOrdersAsync()).Single();
        Assert.AreEqual(240.49m, stored.PullbackPrice);
        Assert.AreEqual(1, stored.PullbackCount);
        Assert.AreEqual(2, stored.RearmCount);
        Assert.IsNull(stored.PullbackRequestedUtc);
    }

    [TestMethod]
    public async Task Only_fired_orders_with_a_pullback_level_are_watched()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(Armed());
        await repository.SaveArmedOrderAsync(Armed() with { ArmedId = "plain", PullbackPrice = null });
        Assert.AreEqual(0, (await repository.GetArmedOrdersWatchedForPullbackAsync()).Count,
            "an armed order has not fired, so there is nothing to pull back");

        await FireAsync(repository, "tp1");
        await FireAsync(repository, "plain");

        var watched = await repository.GetArmedOrdersWatchedForPullbackAsync();
        Assert.AreEqual("tp1", watched.Single().ArmedId);
    }

    [TestMethod]
    public async Task A_pullback_request_is_claimed_once()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(Armed());
        await FireAsync(repository, "tp1");

        Assert.IsTrue(await repository.TryRequestArmedOrderPullbackAsync("tp1", "first"));
        Assert.IsFalse(await repository.TryRequestArmedOrderPullbackAsync("tp1", "second"),
            "two overlapping passes must not both start a pull-back");
    }

    [TestMethod]
    public async Task Without_the_marker_the_order_cannot_be_rearmed()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(Armed());
        await FireAsync(repository, "tp1");

        var rearm = new PullbackRearm(78, 242.92m, 240.49m, 1, 1, "re-armed");
        Assert.IsFalse(await repository.TryRearmAfterPullbackAsync("tp1", rearm),
            "a persistent order a person cancelled must not bring its trigger back");
        Assert.AreEqual("fired", (await repository.GetArmedOrdersAsync(armedOnly: false)).Single().State);
    }

    [TestMethod]
    public async Task A_requested_pullback_rearms_with_the_new_row()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(Armed());
        await FireAsync(repository, "tp1");
        await repository.TryRequestArmedOrderPullbackAsync("tp1", "pulling back");

        var rearm = new PullbackRearm(48, 244.14m, null, 2, 3, "cap reached");
        Assert.IsTrue(await repository.TryRearmAfterPullbackAsync("tp1", rearm));

        var stored = (await repository.GetArmedOrdersAsync()).Single();
        Assert.AreEqual("armed", stored.State);
        Assert.AreEqual(48, stored.Quantity);
        Assert.AreEqual(244.14m, stored.TriggerPrice);
        Assert.IsNull(stored.PullbackPrice);
        Assert.AreEqual(2, stored.PullbackCount);
        Assert.AreEqual(3, stored.RearmCount);
        Assert.IsNull(stored.PullbackRequestedUtc);
        Assert.AreEqual("cap reached", stored.StateReason);
    }

    [TestMethod]
    public async Task A_withdrawn_request_leaves_the_order_watched_and_unable_to_rearm()
    {
        // The hand-back marks first and cancels second; when the cancel loses a race with the
        // persistent worker, the marker comes back off so nothing re-arms over a live sell.
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(Armed());
        await FireAsync(repository, "tp1");
        await repository.TryRequestArmedOrderPullbackAsync("tp1", "handing back");

        Assert.IsTrue(await repository.TryWithdrawArmedOrderPullbackRequestAsync("tp1"));

        var watched = (await repository.GetArmedOrdersWatchedForPullbackAsync()).Single();
        Assert.IsNull(watched.PullbackRequestedUtc);
        Assert.AreEqual(240.49m, watched.PullbackPrice, "still watched for a pull-back");
        Assert.IsFalse(await repository.TryRearmAfterPullbackAsync(
            "tp1", new PullbackRearm(78, 242.92m, 240.49m, 0, 1, "re-armed")));
        Assert.IsTrue(await repository.TryRequestArmedOrderPullbackAsync("tp1", "again"),
            "a later poll may request it afresh");
    }

    [TestMethod]
    public async Task Ending_a_pullback_stops_it_being_watched()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(Armed());
        await FireAsync(repository, "tp1");

        Assert.IsTrue(await repository.TryEndArmedOrderPullbackAsync("tp1", "filled"));
        Assert.AreEqual(0, (await repository.GetArmedOrdersWatchedForPullbackAsync()).Count);
        Assert.IsFalse(await repository.TryRequestArmedOrderPullbackAsync("tp1", "late"),
            "an ended pull-back cannot be restarted");
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>PPL's rung 1 as the screenshot showed it, fired 0.5% ahead with a 1% pull-back.</summary>
    private static ArmedOrder Armed() => new()
    {
        ArmedId       = "tp1",
        Symbol        = "PPL",
        TriggerKind   = ArmedTriggerKind.PriceAbove,
        TriggerPrice  = 242.92m,
        Action        = "SELL",
        Quantity      = 78,
        OrderType     = "LIMIT",
        Price         = 244.14m,
        PersistentUntilFilled = true,
        PullbackPrice = 240.49m
    };

    private static ArmedOrder Fired() => Armed() with { State = "fired" };

    private static PersistentOrderIntent Intent(
        string state, int filled = 0, DateOnly? lastAttempt = null) => new()
    {
        IntentId = "armed-tp1",
        Symbol = "PPL",
        Action = "SELL",
        Quantity = 78,
        OrderType = "LIMIT",
        Price = 244.14m,
        State = state,
        FilledQuantity = filled,
        LastAttemptSessionDate = lastAttempt ?? Today,
        SourceArmedId = "tp1",
        ExpiresUtc = DateTime.UtcNow.AddDays(20),
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };

    private static PullbackDecision Decide(
        ArmedOrder order, PersistentOrderIntent? intent, decimal? price,
        bool marketIsOpen = true, bool sessionEnded = false) =>
        ArmedOrderPullback.Decide(order, intent, price, marketIsOpen, sessionEnded, Today);

    private static async Task FireAsync(SqliteTradingRepository repository, string id)
    {
        Assert.IsTrue(await repository.TrySetArmedOrderStateAsync(id, "armed", "firing"));
        Assert.IsTrue(await repository.TrySetArmedOrderStateAsync(id, "firing", "fired"));
    }

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
}
