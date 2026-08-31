using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Models;
using TradingAgent.Persistence;

namespace AgentFox.ChannelTests;

/// <summary>
/// Recording broker orders when the broker's own order numbers are NOT unique.
///
/// <para>
/// CONFIRMED live 2026-08-28 against AHL: order numbers are formed <c>{connection}11XK{seq}</c> and the
/// sequence restarts on every new connection, so one number names several unrelated orders on the same
/// account on the same day — a real capture had <c>0411XK1</c> as a MARI buy, a QTECH stop and a SELECT
/// stop within six hours. <c>broker_order_id</c> is this table's primary key, so the second such order
/// threw <c>UNIQUE constraint failed</c>. The persist failed, and the failure surfaced to the operator
/// as "broker outcome unknown — manual reconciliation required" for an order the broker had already
/// accepted.
/// </para>
/// </summary>
[TestClass]
public sealed class BrokerOrderLedgerTests
{
    private string _root = "";

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-ledger-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Reads the rows straight from the file — there is no repository read for this table.</summary>
    private async Task<List<(string BrokerId, string ClientId, string Symbol)>> RowsAsync()
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
            "SELECT broker_order_id, client_order_id, json_extract(order_json,'$.Symbol') FROM broker_orders";

        var rows = new List<(string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2)));
        return rows;
    }

    /// <summary>
    /// Creates the parent execution row an attempt hangs off — <c>broker_orders.execution_id</c> is a
    /// foreign key and <c>PRAGMA foreign_keys</c> is ON. Returns the id the ledger generated.
    /// </summary>
    private static async Task<string> BeginAsync(SqliteTradingRepository repository, string key)
    {
        var claim = await repository.TryBeginExecutionAsync(key, "{}", "test-policy");
        return claim.ExecutionId;
    }

    private static OrderResult Order(string symbol, string? orderId, int quantity) => new()
    {
        Success = true,
        OrderId = orderId,
        Action = "SELL",
        Symbol = symbol,
        Quantity = quantity,
        Message = "accepted"
    };

    [TestMethod]
    public async Task TwoOrdersSharingABrokerNumberAreBothRecorded()
    {
        // The exact live sequence: a QTECH stop and a SELECT stop, minutes apart, both numbered
        // 0411XK1 by a broker that restarts its sequence per connection.
        var repository = NewRepository();

        await repository.RecordBrokerOrdersAsync(
            await BeginAsync(repository, "k-qtech"), [Order("QTECH", "0411XK1", 500)]);
        await repository.RecordBrokerOrdersAsync(
            await BeginAsync(repository, "k-select"), [Order("SELECT", "0411XK1", 83)]);

        var rows = await RowsAsync();
        Assert.AreEqual(2, rows.Count,
            "both orders are real and both must be recorded; the second used to throw");
        CollectionAssert.AreEquivalent(new[] { "QTECH", "SELECT" }, rows.Select(r => r.Symbol).ToList());
        Assert.AreEqual(1, rows.Count(r => r.BrokerId == "0411XK1"),
            "the first keeps the raw number");
        Assert.AreEqual(1, rows.Count(r => r.BrokerId.StartsWith("0411XK1#", StringComparison.Ordinal)),
            "the colliding one is qualified, and keeps the raw number readable at the front");
    }

    [TestMethod]
    public async Task ReRecordingTheSameAttemptStillUpdatesRatherThanDuplicating()
    {
        // The behaviour the collision handling must not break: one attempt re-recorded (a retry of the
        // same execution) updates its row, keyed on client_order_id.
        var repository = NewRepository();

        var execution = await BeginAsync(repository, "k-1");
        await repository.RecordBrokerOrdersAsync(execution, [Order("QTECH", "0411XK1", 500)]);
        await repository.RecordBrokerOrdersAsync(execution, [Order("QTECH", "0411XK1", 500)]);

        var rows = await RowsAsync();
        Assert.AreEqual(1, rows.Count, "the same attempt must update, not accumulate");
        Assert.AreEqual("0411XK1", rows[0].BrokerId, "and must NOT be qualified against itself");
    }

    [TestMethod]
    public async Task ABatchThatReusesANumberWithinItselfIsStillFullyRecorded()
    {
        // Two orders in ONE execution sharing a number — the same connection should not do this, but
        // the ledger must not lose an order if it ever does.
        var repository = NewRepository();

        await repository.RecordBrokerOrdersAsync(
            await BeginAsync(repository, "k-1"),
            [Order("QTECH", "0411XK1", 500), Order("SELECT", "0411XK1", 83)]);

        Assert.AreEqual(2, (await RowsAsync()).Count);
    }

    [TestMethod]
    public async Task OrdersWithNoBrokerNumberDoNotCollideWithEachOther()
    {
        // Failed attempts carry no number and are stored as `pending:{clientOrderId}`. Several in a row
        // is the normal shape of a retrying stop — the live capture had five for SELECT.
        var repository = NewRepository();

        for (var i = 0; i < 5; i++)
            await repository.RecordBrokerOrdersAsync(
                await BeginAsync(repository, $"k-{i}"), [Order("SELECT", null, 83)]);

        Assert.AreEqual(5, (await RowsAsync()).Count);
    }
}
