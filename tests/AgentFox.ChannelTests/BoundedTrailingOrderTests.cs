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
/// Trailing buy and trailing sell with an optional price bound: what they track, when they submit,
/// the move they skip, and the limit they send.
///
/// <para>
/// Driven through the REAL SQLite repository, one price at a time, along the same three branches
/// <c>WatchlistMonitorWorker.EvaluateArmedOrdersAsync</c> takes — skip, fire (claim first), or
/// ratchet. The rule that matters most lives in two places at once: the evaluator says a move is
/// skipped, and the ratchet's guarded UPDATE is the only thing that releases it. Testing either half
/// alone would not catch them disagreeing, and a fresh repository on the same file is how a restart
/// is reproduced rather than assumed.
/// </para>
/// </summary>
[TestClass]
public sealed class BoundedTrailingOrderTests
{
    private const string Id = "bounded1";
    private static readonly DateTime Now = new(2026, 11, 9, 6, 0, 0, DateTimeKind.Utc);

    private string _root = "";
    private readonly List<decimal> _submittedLimits = [];

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"agentfox-bounded-trail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _submittedLimits.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    // ── The two acceptance walks ──────────────────────────────────────────────

    [TestMethod]
    public async Task A_trailing_buy_with_a_maximum_skips_the_move_above_it_and_buys_the_next_rebound()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingBuy(reference: 90m, max: 92m));

        await AssertLevel(repository, 94.50m, "low 90 x 1.05");

        Assert.AreEqual("wait", await StepAsync(repository, 93m),
            "93 is above the max but below the 94.50 trigger: not a move, so nothing is skipped");
        Assert.IsFalse((await Read(repository)).AwaitingNewExtreme);

        Assert.AreEqual("wait", await StepAsync(repository, 87m));
        await AssertLevel(repository, 91.35m, "the new low 87 moves the trigger down");

        Assert.AreEqual("skip", await StepAsync(repository, 93m),
            "93 meets 91.35 but is above the 92 maximum");
        Assert.AreEqual(87m, (await Read(repository)).SkippedReference);

        Assert.AreEqual("wait", await StepAsync(repository, 91.50m),
            "back inside the range does not unlock the skipped move");
        Assert.AreEqual("wait", await StepAsync(repository, 87m),
            "matching the old low is not a strictly lower one");
        Assert.AreEqual(87m, (await Read(repository)).SkippedReference, "still locked");

        Assert.AreEqual("wait", await StepAsync(repository, 86m));
        var unlocked = await Read(repository);
        Assert.IsFalse(unlocked.AwaitingNewExtreme, "a strictly lower low releases the skip");
        await AssertLevel(repository, 90.30m, "low 86 x 1.05");

        Assert.AreEqual("fire", await StepAsync(repository, 90.50m), "a fresh rebound from the new low");
        Assert.AreEqual("wait", await StepAsync(repository, 90.60m), "it fired once and is no longer armed");

        CollectionAssert.AreEqual(new[] { 90.50m }, _submittedLimits,
            "one submission, priced at the price that met the trigger");
    }

    [TestMethod]
    public async Task A_trailing_sell_with_a_minimum_skips_the_gap_below_it_and_sells_the_next_drop()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingSell(reference: 110m, min: 100m));

        await AssertLevel(repository, 104.50m, "high 110 x 0.95");

        Assert.AreEqual("skip", await StepAsync(repository, 99m), "a gap through the trigger below the minimum");
        Assert.AreEqual(110m, (await Read(repository)).SkippedReference);

        Assert.AreEqual("wait", await StepAsync(repository, 103m), "back inside the range does not unlock it");
        Assert.AreEqual("wait", await StepAsync(repository, 110m), "matching the old high is not a new one");
        Assert.IsTrue((await Read(repository)).AwaitingNewExtreme);

        Assert.AreEqual("wait", await StepAsync(repository, 112m));
        Assert.IsFalse((await Read(repository)).AwaitingNewExtreme, "a strictly higher high releases it");
        await AssertLevel(repository, 106.40m, "high 112 x 0.95");

        Assert.AreEqual("fire", await StepAsync(repository, 106m));
        CollectionAssert.AreEqual(new[] { 106m }, _submittedLimits);
    }

    // ── Boundaries ────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(92.00, "fire", "the maximum itself is allowed")]
    [DataRow(92.01, "skip", "one paisa over the maximum")]
    [DataRow(91.35, "fire", "exactly at the trigger counts as reached")]
    [DataRow(91.34, "wait", "one paisa short of the trigger")]
    public async Task A_buy_bound_and_trigger_are_both_inclusive(double price, string expected, string because)
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingBuy(reference: 87m, max: 92m));

        Assert.AreEqual(expected, await StepAsync(repository, (decimal)price), because);
    }

    [TestMethod]
    [DataRow(100.00, "fire", "the minimum itself is allowed")]
    [DataRow(99.99, "skip", "one paisa under the minimum")]
    [DataRow(100.70, "fire", "exactly at the trigger counts as reached")]
    [DataRow(100.71, "wait", "one paisa short of the trigger")]
    public async Task A_sell_bound_and_trigger_are_both_inclusive(double price, string expected, string because)
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingSell(reference: 106m, min: 100m)); // trigger 100.70

        Assert.AreEqual(expected, await StepAsync(repository, (decimal)price), because);
    }

    [TestMethod]
    public async Task Without_a_bound_it_is_the_ordinary_one_way_trail()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingBuy(reference: 90m, max: null));

        Assert.AreEqual("wait", await StepAsync(repository, 87m));
        Assert.AreEqual("fire", await StepAsync(repository, 93m), "no range, so no move is ever skipped");
        CollectionAssert.AreEqual(new[] { 93m }, _submittedLimits, "priced at the price that triggered it");
    }

    // ── The limit that is sent ────────────────────────────────────────────────

    [TestMethod]
    [DataRow("BUY", 90.50, 92.0, 90.50, "inside the bound: the observed price")]
    [DataRow("BUY", 93.00, 92.0, 92.00, "clamped to the maximum")]
    [DataRow("BUY", 93.00, 91.999, 91.99, "a bound that is not a whole paisa rounds DOWN, never past it")]
    [DataRow("BUY", 90.505, null, 90.50, "a buy rounds down")]
    [DataRow("SELL", 106.00, 100.0, 106.00, "inside the bound: the observed price")]
    [DataRow("SELL", 99.00, 100.0, 100.00, "clamped to the minimum")]
    [DataRow("SELL", 99.00, 100.001, 100.01, "a bound that is not a whole paisa rounds UP, never past it")]
    [DataRow("SELL", 106.005, null, 106.01, "a sell rounds up")]
    public void The_limit_never_crosses_the_bound(
        string action, double observed, double? bound, double expected, string because)
    {
        var limit = TrailingPriceBound.LimitAt(action, (decimal)observed, (decimal?)bound);

        Assert.AreEqual((decimal)expected, limit, because);
        if (bound is { } b)
            Assert.IsTrue(action == "BUY" ? limit <= (decimal)b : limit >= (decimal)b, because);
    }

    [TestMethod]
    public void A_trailing_limit_with_no_observed_price_keeps_the_stored_price() =>
        Assert.AreEqual(94.50m, TrailingBuy(reference: 90m, max: 92m).PriceAtFire(null, null),
            "an unreadable price never sends an order with no price");

    [TestMethod]
    public void A_market_trailing_stop_is_still_sent_at_market() =>
        Assert.IsNull(
            (TrailingSell(reference: 110m, min: null) with { OrderType = "MARKET", Price = null, PersistentUntilFilled = false })
                .PriceAtFire(null, 104m),
            "orders saved from the old Trailing stop card keep their one-shot market behaviour");

    // ── Stale prices, restarts, closed markets ────────────────────────────────

    [TestMethod]
    public async Task A_missing_price_neither_fires_nor_moves_the_trail_nor_skips()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingBuy(reference: 87m, max: 92m));

        // A lapsed feed, a quote past the feed's age bound, and a closed market all reach the evaluator
        // the same way: as no price. The age bound itself is enforced where quotes are served.
        Assert.AreEqual("wait", await StepAsync(repository, null));

        var after = await Read(repository);
        Assert.AreEqual(87m, after.ReferencePrice);
        Assert.IsFalse(after.AwaitingNewExtreme);
        Assert.AreEqual(0, _submittedLimits.Count);
    }

    [TestMethod]
    public async Task A_restart_while_waiting_for_a_new_low_keeps_the_skip_and_the_reference()
    {
        var before = NewRepository();
        await before.SaveArmedOrderAsync(TrailingBuy(reference: 87m, max: 92m));
        Assert.AreEqual("skip", await StepAsync(before, 93m));

        SqliteConnection.ClearAllPools();
        var after = NewRepository(); // the same file, read by a new process

        var restored = await Read(after);
        Assert.AreEqual(87m, restored.SkippedReference);
        Assert.AreEqual(92m, restored.PriceBound);
        Assert.AreEqual(87m, restored.ReferencePrice);

        Assert.AreEqual("wait", await StepAsync(after, 91.50m), "still locked after the restart");
        Assert.AreEqual("wait", await StepAsync(after, 86m));
        Assert.AreEqual("fire", await StepAsync(after, 90.30m));
    }

    // ── Expiry, cancellation, duplicates ──────────────────────────────────────

    [TestMethod]
    public void An_expired_order_does_not_fire_even_on_a_qualifying_price()
    {
        var order = TrailingBuy(reference: 87m, max: 92m) with { ExpiresUtc = Now.AddMinutes(-1) };

        var outcome = ArmedOrderEvaluator.Evaluate(order, 91m, [], Now, out var reason, marketIsOpen: true);

        Assert.AreEqual(ArmedOrderOutcome.Wait, outcome);
        StringAssert.Contains(reason, "Expired");
    }

    [TestMethod]
    public void An_expired_order_does_not_record_a_skip_either() =>
        Assert.AreEqual(ArmedOrderOutcome.Wait,
            ArmedOrderEvaluator.Evaluate(
                TrailingBuy(reference: 87m, max: 92m) with { ExpiresUtc = Now.AddMinutes(-1) },
                93m, [], Now, out _, marketIsOpen: true));

    [TestMethod]
    public async Task A_cancelled_order_cannot_be_skipped_ratcheted_or_fired()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingBuy(reference: 87m, max: 92m));
        Assert.IsTrue(await repository.TrySetArmedOrderStateAsync(Id, "armed", "cancelled"));

        Assert.IsFalse(await repository.TrySetArmedOrderTrailSkipAsync(Id, 87m, "late pass"));
        Assert.IsFalse(await repository.TrySetArmedOrderTrailAsync(Id, 80m, 84m, ratchetUp: false));
        Assert.AreEqual("wait", await StepAsync(repository, 90m));
        Assert.AreEqual(0, _submittedLimits.Count);
    }

    [TestMethod]
    public async Task Two_overlapping_passes_submit_once()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingBuy(reference: 87m, max: 92m));

        var first = repository.TrySetArmedOrderStateAsync(Id, "armed", "firing", "pass 1");
        var second = repository.TrySetArmedOrderStateAsync(Id, "armed", "firing", "pass 2");
        var claims = await Task.WhenAll(first, second);

        Assert.AreEqual(1, claims.Count(c => c), "the claim is a compare-and-set before any broker call");
    }

    [TestMethod]
    public async Task A_skip_measured_from_a_low_another_pass_has_left_behind_does_not_lock_the_order()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingBuy(reference: 87m, max: 92m));

        // Pass B ratchets to a new low while pass A, which read 87, is about to record its skip.
        Assert.IsTrue(await repository.TrySetArmedOrderTrailAsync(Id, 86m, 90.30m, ratchetUp: false));
        Assert.IsFalse(await repository.TrySetArmedOrderTrailSkipAsync(Id, 87m, "stale pass"),
            "the skip was a move from 87, which is no longer the tracked low");
        Assert.IsFalse((await Read(repository)).AwaitingNewExtreme);
    }

    [TestMethod]
    public async Task A_second_skip_of_the_same_move_writes_nothing()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingBuy(reference: 87m, max: 92m));

        Assert.IsTrue(await repository.TrySetArmedOrderTrailSkipAsync(Id, 87m, "first"));
        Assert.IsFalse(await repository.TrySetArmedOrderTrailSkipAsync(Id, 87m, "second"));
    }

    [TestMethod]
    public async Task An_order_saved_without_a_bound_reads_back_unbounded_and_unskipped()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingSell(reference: 110m, min: null) with
        {
            OrderType = "MARKET", Price = null, PersistentUntilFilled = false
        });

        var stored = await Read(repository);
        Assert.IsNull(stored.PriceBound);
        Assert.IsNull(stored.SkippedReference);
    }

    // ── The ticket's choices ──────────────────────────────────────────────────

    [TestMethod]
    public void The_ticket_offers_one_trailing_choice_per_side()
    {
        var trailing = OrderIntentRegistry.All.Where(i => i.Trailing).ToList();

        Assert.AreEqual(2, trailing.Count);
        Assert.IsNull(OrderIntentRegistry.Find("trailing-stop"),
            "the one-shot market card was replaced; saved orders do not reference it");

        var buy = trailing.Single(i => i.Action == "BUY");
        var sell = trailing.Single(i => i.Action == "SELL");
        Assert.AreEqual(("trailing-buy", "PercentRise"), (buy.Id, buy.TriggerKind));
        Assert.AreEqual(("trailing-sell", "PercentDrop"), (sell.Id, sell.TriggerKind));

        foreach (var intent in trailing)
        {
            Assert.AreEqual("LIMIT", intent.OrderType, intent.Id);
            Assert.AreEqual("trail-bound", intent.PriceField, intent.Id);
            Assert.IsTrue(intent.KeepWorking, intent.Id);
            Assert.IsNull(intent.DefaultWindowMinutes, $"{intent.Id} follows the extreme since arming");
        }

        Assert.IsNotNull(OrderIntentRegistry.Find("buy-after-rise")?.DefaultWindowMinutes,
            "'Buy if it rises by a %' stays the rolling-window choice");
    }

    // ── One pass, as the worker takes it ──────────────────────────────────────

    /// <summary>
    /// The worker's branch for one armed order and one price: skip, fire (claim first), or ratchet.
    /// Mirrors <c>WatchlistMonitorWorker.EvaluateArmedOrdersAsync</c> without the broker side.
    /// </summary>
    private async Task<string> StepAsync(SqliteTradingRepository repository, decimal? price)
    {
        var order = await Read(repository);
        var outcome = ArmedOrderEvaluator.Evaluate(order, price, [], Now, out var why, marketIsOpen: true);

        switch (outcome)
        {
            case ArmedOrderOutcome.SkipMove:
                await repository.TrySetArmedOrderTrailSkipAsync(order.ArmedId, order.ReferencePrice!.Value, why);
                return "skip";

            case ArmedOrderOutcome.Fire:
                if (!await repository.TrySetArmedOrderStateAsync(order.ArmedId, "armed", "firing", why))
                    return "lost-claim";
                _submittedLimits.Add(order.PriceAtFire(null, price)!.Value);
                await repository.TrySetArmedOrderStateAsync(order.ArmedId, "firing", "fired");
                return "fire";

            default:
                if (ArmedOrderEvaluator.NextTrailReference(order, price) is { } reference)
                    await repository.TrySetArmedOrderTrailAsync(
                        order.ArmedId, reference,
                        PercentTrigger.Level(order.TriggerKind, reference, order.TriggerPercent)!.Value,
                        ratchetUp: order.TriggerKind == ArmedTriggerKind.PercentDrop);
                return "wait";
        }
    }

    private static async Task<ArmedOrder> Read(SqliteTradingRepository repository) =>
        (await repository.GetArmedOrdersAsync(armedOnly: false)).Single(o => o.ArmedId == Id);

    private static async Task AssertLevel(SqliteTradingRepository repository, decimal expected, string because) =>
        Assert.AreEqual(expected, (await Read(repository)).EffectiveTriggerPrice, because);

    private static ArmedOrder TrailingBuy(decimal reference, decimal? max) => Trailing(
        "BUY", ArmedTriggerKind.PercentRise, 5m, reference, max);

    private static ArmedOrder TrailingSell(decimal reference, decimal? min) => Trailing(
        "SELL", ArmedTriggerKind.PercentDrop, 5m, reference, min);

    private static ArmedOrder Trailing(
        string action, ArmedTriggerKind kind, decimal percent, decimal reference, decimal? bound) => new()
    {
        ArmedId        = Id,
        Symbol         = "OGDC",
        TriggerKind    = kind,
        TriggerPercent = percent,
        ReferencePrice = reference,
        Trailing       = true,
        TriggerPrice   = PercentTrigger.Level(kind, reference, percent),
        Action         = action,
        Quantity       = 100,
        OrderType      = "LIMIT",
        Price          = PercentTrigger.Level(kind, reference, percent),
        PriceBound     = bound,
        PersistentUntilFilled = true,
        OperatorOriginated    = true,
        ArmedUtc       = Now.AddHours(-1),
        ExpiresUtc     = Now.AddDays(30)
    };

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
