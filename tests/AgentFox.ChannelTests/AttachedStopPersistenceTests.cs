using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Models;
using TradingAgent.Persistence;
using TradingAgent.Reconciliation;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// A protective stop attached to an IMMEDIATE dashboard buy: that it survives the round trip, and that
/// its fill is measured from the entry's own execution rather than from a holdings baseline.
///
/// <para>
/// The baseline is the thing an immediate order cannot have. An armed entry sits unfired long enough
/// for holdings-before to be read; an immediate order may already have filled by the time anything
/// looks, and a baseline captured then equals the holding for ever — so the fill is never detected and
/// the stop never activates. Silent, and in the unprotected direction. Fills attributed to the
/// execution answer the same question and cannot be too late.
/// </para>
/// </summary>
[TestClass]
public sealed class AttachedStopPersistenceTests
{
    private string _root = "";

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-attachstop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
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

    [TestMethod]
    public async Task BothNewParentLinkagesSurviveTheRoundTrip()
    {
        // A column added by migration that nothing reads back is the classic silent half-change: the
        // stop would be created linked and re-read orphaned, then closed as "the entry no longer
        // exists" on the very next pass.
        var repository = NewRepository();

        await repository.SaveProtectiveStopAsync(Stop("s-immediate") with
        {
            ParentExecutionId = "exec-1"
        });
        await repository.SaveProtectiveStopAsync(Stop("s-keepworking") with
        {
            ParentPersistentIntentId = "intent-1"
        });
        await repository.SaveProtectiveStopAsync(Stop("s-bare"));

        var stops = await repository.GetProtectiveStopsAsync(openOnly: false);

        var immediate = stops.Single(s => s.StopId == "s-immediate");
        Assert.AreEqual("exec-1", immediate.ParentExecutionId);
        Assert.IsNull(immediate.ParentPersistentIntentId);
        Assert.IsNull(immediate.ParentArmedId);

        var keepWorking = stops.Single(s => s.StopId == "s-keepworking");
        Assert.AreEqual("intent-1", keepWorking.ParentPersistentIntentId);
        Assert.IsNull(keepWorking.ParentExecutionId);

        var bare = stops.Single(s => s.StopId == "s-bare");
        Assert.IsNull(bare.ParentExecutionId);
        Assert.IsNull(bare.ParentPersistentIntentId);
    }

    [TestMethod]
    public async Task AnEntrysOwnFillsAreAttributedToItsExecution()
    {
        var repository = NewRepository();
        var execution = await BeginAsync(repository, "k-buy");

        await repository.RecordBrokerOrdersAsync(execution, [Buy("LUCK", "0411XK9", 35)]);
        await repository.RecordFillsAsync(
        [
            new BrokerFill("0411XK9", "LUCK", "BUY", 20, 424m, DateTime.UtcNow),
            new BrokerFill("0411XK9", "LUCK", "BUY", 15, 424m, DateTime.UtcNow)
        ]);

        // A part fill then the rest: the stop must cover 35, not the 20 the first pass saw.
        Assert.AreEqual(35,
            await repository.GetFilledQuantityForExecutionAsync(execution, "LUCK"));

        // Case-insensitive on the symbol, because the dialog uppercases and the broker may not.
        Assert.AreEqual(35,
            await repository.GetFilledQuantityForExecutionAsync(execution, "luck"));
    }

    [TestMethod]
    public async Task NothingFilledIsZeroAndAnotherExecutionsFillsAreNeverCounted()
    {
        // Zero here is a MEASUREMENT — the fills table is written from the broker's own snapshot — and
        // it must stay zero rather than borrowing a sibling order's fills.
        var repository = NewRepository();
        var mine = await BeginAsync(repository, "k-mine");
        var theirs = await BeginAsync(repository, "k-theirs");

        await repository.RecordBrokerOrdersAsync(mine, [Buy("LUCK", "0411XK9", 35)]);
        await repository.RecordBrokerOrdersAsync(theirs, [Buy("LUCK", "0411XK10", 10)]);
        await repository.RecordFillsAsync(
            [new BrokerFill("0411XK10", "LUCK", "BUY", 10, 424m, DateTime.UtcNow)]);

        Assert.AreEqual(0, await repository.GetFilledQuantityForExecutionAsync(mine, "LUCK"));
        Assert.AreEqual(10, await repository.GetFilledQuantityForExecutionAsync(theirs, "LUCK"));
        Assert.AreEqual(0, await repository.GetFilledQuantityForExecutionAsync("", "LUCK"));
    }

    [TestMethod]
    public async Task AReusedBrokerOrderNumberDoesNotLeakFillsBetweenExecutions()
    {
        // The reason this keys on the execution and not the order number. AHL restarts its sequence
        // per connection, so 0411XK1 really does name unrelated orders on one account on one day —
        // the ledger stores the collider as {orderNo}#{clientOrderId}. Summing by number would either
        // miss this order's fills or add the other's, and the second sizes a real sell against shares
        // belonging to a different position.
        var repository = NewRepository();
        var first = await BeginAsync(repository, "k-first");
        var second = await BeginAsync(repository, "k-second");

        await repository.RecordBrokerOrdersAsync(first, [Buy("LUCK", "0411XK1", 35)]);
        await repository.RecordBrokerOrdersAsync(second, [Buy("MARI", "0411XK1", 10)]);

        var rows = await BrokerOrderIdsAsync();
        var firstId = rows.Single(r => r.Symbol == "LUCK").BrokerId;
        var secondId = rows.Single(r => r.Symbol == "MARI").BrokerId;
        Assert.AreNotEqual(firstId, secondId, "the collider is stored qualified, not overwritten");

        await repository.RecordFillsAsync(
            [new BrokerFill(secondId, "MARI", "BUY", 10, 657m, DateTime.UtcNow)]);

        Assert.AreEqual(0, await repository.GetFilledQuantityForExecutionAsync(first, "LUCK"),
            "the MARI fill belongs to the other execution and must not size the LUCK stop");
        Assert.AreEqual(10, await repository.GetFilledQuantityForExecutionAsync(second, "MARI"));
    }

    [TestMethod]
    public async Task AGroupedExecutionAttributesEachSymbolSeparately()
    {
        // The symbol filter's reason for existing: one execution can carry a group, and a stop
        // protects one symbol.
        var repository = NewRepository();
        var execution = await BeginAsync(repository, "k-group");

        await repository.RecordBrokerOrdersAsync(execution,
            [Buy("LUCK", "0411XK9", 35), Buy("MARI", "0411XK10", 10)]);
        await repository.RecordFillsAsync(
        [
            new BrokerFill("0411XK9", "LUCK", "BUY", 35, 424m, DateTime.UtcNow),
            new BrokerFill("0411XK10", "MARI", "BUY", 10, 657m, DateTime.UtcNow)
        ]);

        Assert.AreEqual(35, await repository.GetFilledQuantityForExecutionAsync(execution, "LUCK"));
        Assert.AreEqual(10, await repository.GetFilledQuantityForExecutionAsync(execution, "MARI"));
    }

    private static ProtectiveStop Stop(string stopId) => new()
    {
        StopId = stopId,
        Symbol = "LUCK",
        StopTrigger = 415m,
        StopLimit = 410.85m,
        DesiredQuantity = 0,
        Recurring = true,
        State = "pending_fill",
        OperatorOriginated = true
    };

    private static OrderResult Buy(string symbol, string orderId, int quantity) => new()
    {
        Success = true,
        OrderId = orderId,
        Action = "BUY",
        Symbol = symbol,
        Quantity = quantity,
        Message = "accepted"
    };

    private static async Task<string> BeginAsync(SqliteTradingRepository repository, string key)
    {
        var claim = await repository.TryBeginExecutionAsync(key, "{}", "test-policy");
        return claim.ExecutionId;
    }

    private async Task<List<(string BrokerId, string Symbol)>> BrokerOrderIdsAsync()
    {
        var path = Path.Combine(_root, "trading", "trading.db");
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = path, Pooling = false
            }.ToString());
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText =
            "SELECT broker_order_id, json_extract(order_json,'$.Symbol') FROM broker_orders";

        var rows = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
        return rows;
    }
}
