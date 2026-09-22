using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Persistence;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// Storage for percent triggers, and the ratchet that moves a trailing one.
///
/// <para>
/// Two things here can only fail in production. The percent columns were added to a table that already
/// exists in every deployed database, and <c>CREATE TABLE IF NOT EXISTS</c> does not alter it — so the
/// additive migration is tested against a hand-built OLD table rather than a fresh one, which is the
/// only version of the test that would have caught its absence.
/// </para>
///
/// <para>
/// The other is the direction guard. Two monitoring passes can read different prices and write them in
/// either order, and a later write carrying a staler price would move a trailing stop's level back DOWN
/// — quietly widening the loss the operator capped. The comparison therefore lives in the UPDATE
/// statement, and these tests drive it through the repository rather than trusting the caller to check
/// first.
/// </para>
/// </summary>
[TestClass]
public sealed class ArmedOrderTrailPersistenceTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"agentfox-trail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    // ── Round trip ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task APercentTrigger_RoundTripsItsReferenceAndPercentage()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingDrop());

        var stored = (await repository.GetArmedOrdersAsync()).Single(o => o.ArmedId == "trail1");

        Assert.AreEqual(ArmedTriggerKind.PercentDrop, stored.TriggerKind);
        Assert.AreEqual(3m, stored.TriggerPercent);
        Assert.AreEqual(100m, stored.ReferencePrice);
        Assert.IsTrue(stored.Trailing);
        Assert.AreEqual(97m, stored.TriggerPrice, "The materialised level.");
        Assert.AreEqual(97m, stored.EffectiveTriggerPrice,
            "Recomputing from the stored reference must agree with the stored level, or the panel and "
            + "the evaluator would disagree about where the order fires.");
    }

    [TestMethod]
    public async Task AWindowedTrigger_RoundTripsItsWindow()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(
            TrailingDrop() with { ArmedId = "window1", Trailing = false, TriggerWindowMinutes = 15 });

        var stored = (await repository.GetArmedOrdersAsync()).Single(o => o.ArmedId == "window1");

        Assert.AreEqual(15, stored.TriggerWindowMinutes);
        Assert.IsTrue(stored.IsWindowed);
    }

    // ── Migration ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task AnOlderDatabase_GainsThePercentColumnsWithoutLosingItsOrders()
    {
        WriteLegacyArmedOrdersTable();

        var repository = NewRepository();
        var legacy = (await repository.GetArmedOrdersAsync()).SingleOrDefault(o => o.ArmedId == "legacy1");

        Assert.IsNotNull(legacy, "The migration must ALTER the existing table, never replace it.");
        Assert.AreEqual(95.00m, legacy.TriggerPrice, "Its own trigger survives untouched.");
        Assert.IsNull(legacy.TriggerPercent);
        Assert.IsNull(legacy.ReferencePrice);
        Assert.IsFalse(legacy.Trailing,
            "A row written before trailing existed is not trailing — the column defaults, it does not "
            + "opt every historical order into a ratchet.");
        Assert.IsFalse(legacy.OperatorOriginated,
            "Origination is claimed, never inferred: a migrated row must not be promoted to \"the "
            + "operator armed this\", which is what lets an order fire on a manual-only symbol.");
        Assert.IsNull(legacy.ActiveFromUtc,
            "A row written before activation dates existed is active NOW, exactly as it always was. "
            + "A migration that gave it a date would postpone a live standing instruction.");
    }

    // ── Activation date ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task AScheduledOrder_RoundTripsItsKindAndItsDate()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(Scheduled());

        var stored = (await repository.GetArmedOrdersAsync()).Single(o => o.ArmedId == "sched1");

        Assert.AreEqual(ArmedTriggerKind.Scheduled, stored.TriggerKind);
        Assert.AreEqual(
            new DateTime(2026, 9, 21, 19, 0, 0, DateTimeKind.Utc), stored.ActiveFromUtc,
            "The instant must survive exactly: it is 22-09 PKT midnight, and a round trip that "
            + "shifted it by hours would move the order to a different trading day.");
        Assert.IsNull(stored.TriggerPrice, "A scheduled order has no level.");
    }

    [TestMethod]
    public async Task APriceTriggerCanCarryAnActivationDate()
    {
        // The conditional half: the date and the trigger kind are orthogonal, and storage must not
        // quietly couple them by, say, only persisting the date for scheduled orders.
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(Scheduled() with
        {
            ArmedId      = "sched2",
            TriggerKind  = ArmedTriggerKind.PriceBelow,
            TriggerPrice = 95m
        });

        var stored = (await repository.GetArmedOrdersAsync()).Single(o => o.ArmedId == "sched2");

        Assert.AreEqual(ArmedTriggerKind.PriceBelow, stored.TriggerKind);
        Assert.AreEqual(95m, stored.TriggerPrice);
        Assert.AreEqual(
            new DateTime(2026, 9, 21, 19, 0, 0, DateTimeKind.Utc), stored.ActiveFromUtc);
    }

    [TestMethod]
    public async Task AnOrdinaryOrder_StoresNoActivationDate()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingDrop());

        var stored = (await repository.GetArmedOrdersAsync()).Single(o => o.ArmedId == "trail1");

        Assert.IsNull(stored.ActiveFromUtc,
            "Null is the ordinary case and must stay distinguishable from a date — the evaluator "
            + "reads null as \"active now\".");
    }

    // ── Retention ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task TheSweep_RemovesFinishedOrdersAndNeverArmedOnes()
    {
        // The asymmetry IS the rule. An armed row is a live standing instruction — pruning one would
        // cancel an order the operator is relying on, silently — and an order carrying an activation
        // date can legitimately sit armed for months before it is due.
        var repository = NewRepository();
        var longAgo = DateTime.UtcNow.AddDays(-400);

        await repository.SaveArmedOrderAsync(TrailingDrop() with
        {
            ArmedId = "old-armed", ArmedUtc = longAgo
        });
        await repository.SaveArmedOrderAsync(TrailingDrop() with
        {
            ArmedId = "old-fired", ArmedUtc = longAgo, State = "fired"
        });
        await repository.SaveArmedOrderAsync(TrailingDrop() with
        {
            ArmedId = "recent-fired", State = "fired"
        });

        var removed = await repository.PruneArmedOrdersAsync(DateTime.UtcNow.AddDays(-90));

        Assert.AreEqual(1, removed);
        var remaining = (await repository.GetArmedOrdersAsync(armedOnly: false))
            .Select(o => o.ArmedId).ToHashSet();
        CollectionAssert.AreEquivalent(
            new[] { "old-armed", "recent-fired" }, remaining.ToArray(),
            "Only the OLD terminal row goes. A 400-day-old armed order stays armed.");
    }

    [TestMethod]
    public async Task Origination_RoundTripsBothWays()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingDrop());
        await repository.SaveArmedOrderAsync(TrailingDrop() with
        {
            ArmedId = "byhand", OperatorOriginated = true
        });

        var orders = await repository.GetArmedOrdersAsync();
        Assert.IsTrue(orders.Single(o => o.ArmedId == "byhand").OperatorOriginated,
            "An order armed from the dashboard has to still be the operator's instruction after a "
            + "restart, or a manual-only symbol would stop firing the moment the process bounced.");
        Assert.IsFalse(orders.Single(o => o.ArmedId == "trail1").OperatorOriginated,
            "And a strategy's order must not acquire origination on the way through storage.");
    }

    // ── The one-way ratchet ───────────────────────────────────────────────────

    [TestMethod]
    public async Task AFavourableRatchet_MovesTheReferenceAndTheLevelTogether()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingDrop());

        Assert.IsTrue(await repository.TrySetArmedOrderTrailAsync(
            "trail1", reference: 120m, triggerPrice: 116.40m, ratchetUp: true));

        var stored = (await repository.GetArmedOrdersAsync()).Single(o => o.ArmedId == "trail1");
        Assert.AreEqual(120m, stored.ReferencePrice);
        Assert.AreEqual(116.40m, stored.TriggerPrice,
            "The level has to move with the reference; leaving it behind is what makes a panel lie.");
    }

    [TestMethod]
    [DataRow(110.0, "a lower reference would loosen the trail")]
    [DataRow(120.0, "an equal reference is not an improvement worth a write")]
    public async Task ATrailNeverMovesBackwards(double reference, string because)
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingDrop());
        await repository.TrySetArmedOrderTrailAsync("trail1", 120m, 116.40m, ratchetUp: true);

        Assert.IsFalse(
            await repository.TrySetArmedOrderTrailAsync(
                "trail1", (decimal)reference, (decimal)reference * 0.97m, ratchetUp: true),
            because);

        var stored = (await repository.GetArmedOrdersAsync()).Single(o => o.ArmedId == "trail1");
        Assert.AreEqual(120m, stored.ReferencePrice, "The refused write must change nothing.");
        Assert.AreEqual(116.40m, stored.TriggerPrice);
    }

    [TestMethod]
    public async Task ARiseTrigger_RatchetsDownwardInstead()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingDrop() with
        {
            ArmedId = "rise1",
            TriggerKind = ArmedTriggerKind.PercentRise,
            Action = "BUY",
            TriggerPrice = 103m
        });

        Assert.IsTrue(await repository.TrySetArmedOrderTrailAsync(
            "rise1", reference: 90m, triggerPrice: 92.70m, ratchetUp: false),
            "A breakout entry follows the market DOWN.");

        Assert.IsFalse(await repository.TrySetArmedOrderTrailAsync(
            "rise1", reference: 95m, triggerPrice: 97.85m, ratchetUp: false),
            "A higher reference is not an improvement for a rise trigger.");
    }

    // ── Guards ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ANonTrailingOrder_CannotBeRatcheted()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingDrop() with
        {
            ArmedId = "fixed1", Trailing = false
        });

        Assert.IsFalse(
            await repository.TrySetArmedOrderTrailAsync("fixed1", 200m, 194m, ratchetUp: true),
            "A fixed percent trigger measures from where it was armed. Moving it would silently "
            + "convert it into a trailing one.");
    }

    [TestMethod]
    public async Task AnOrderThatIsNoLongerArmed_CannotBeRatcheted()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(TrailingDrop());
        await repository.TrySetArmedOrderStateAsync("trail1", "armed", "cancelled", "disarmed");

        Assert.IsFalse(
            await repository.TrySetArmedOrderTrailAsync("trail1", 999m, 969m, ratchetUp: true),
            "Ratcheting a cancelled or fired order rewrites the record of why it ended where it did.");
    }

    [TestMethod]
    public async Task AnUnknownId_IsReportedRatherThanCreated()
    {
        var repository = NewRepository();
        Assert.IsFalse(
            await repository.TrySetArmedOrderTrailAsync("nope", 120m, 116.40m, ratchetUp: true));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ArmedOrder TrailingDrop() => new()
    {
        ArmedId        = "trail1",
        Symbol         = "OGDC",
        TriggerKind    = ArmedTriggerKind.PercentDrop,
        TriggerPercent = 3m,
        ReferencePrice = 100m,
        Trailing       = true,
        TriggerPrice   = PercentTrigger.Level(ArmedTriggerKind.PercentDrop, 100m, 3m),
        Action         = "SELL",
        Quantity       = 500,
        OrderType      = "MARKET",
        Note           = "trailing stop"
    };

    /// <summary>
    /// A scheduled order as the arm endpoint builds one: no level, no percentage, and an activation
    /// instant that is 2026-09-22 midnight in Pakistan (UTC+5, no DST) — i.e. 2026-09-21T19:00Z.
    /// </summary>
    private static ArmedOrder Scheduled() => new()
    {
        ArmedId       = "sched1",
        Symbol        = "OGDC",
        TriggerKind   = ArmedTriggerKind.Scheduled,
        ActiveFromUtc = new DateTime(2026, 9, 21, 19, 0, 0, DateTimeKind.Utc),
        Action        = "BUY",
        Quantity      = 100,
        OrderType     = "LIMIT",
        Price         = 45m,
        Note          = "buy it on the 22nd"
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

    /// <summary>
    /// Writes <c>armed_orders</c> as it stood BEFORE the percent columns, with a row in it. This is the
    /// shape every already-deployed database has, and the only one that exercises the migration.
    /// </summary>
    private void WriteLegacyArmedOrdersTable()
    {
        var path = Path.Combine(_root, "trading", "trading.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE armed_orders (
                armed_id      TEXT PRIMARY KEY,
                symbol        TEXT NOT NULL,
                trigger_kind  TEXT NOT NULL,
                trigger_price TEXT NULL,
                trigger_alert TEXT NULL,
                action        TEXT NOT NULL,
                quantity      INTEGER NOT NULL,
                order_type    TEXT NOT NULL,
                price         TEXT NULL,
                limit_price   TEXT NULL,
                state         TEXT NOT NULL DEFAULT 'armed',
                armed_utc     TEXT NOT NULL,
                expires_utc   TEXT NULL,
                fired_utc     TEXT NULL,
                execution_id  TEXT NULL,
                state_reason  TEXT NULL,
                note          TEXT NULL,
                source_alert  TEXT NULL
            );
            INSERT INTO armed_orders
                (armed_id, symbol, trigger_kind, trigger_price, action, quantity, order_type,
                 state, armed_utc)
            VALUES ('legacy1', 'OGDC', 'PriceBelow', '95.00', 'BUY', 10, 'LIMIT',
                    'armed', '2026-08-19T09:00:00.0000000Z');
            """;
        command.ExecuteNonQuery();
    }
}
