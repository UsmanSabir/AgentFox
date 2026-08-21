using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Models;
using TradingAgent.Persistence;
using TradingAgent.Reconciliation;

namespace AgentFox.ChannelTests;

/// <summary>
/// Covers the order-number and fill rows added on 2026-08-19.
///
/// <para>
/// The tables (<c>broker_orders</c>, <c>fills</c>) had existed in the schema since the beginning and were
/// never written to, so the exchange's order number lived only inside a serialized JSON blob. These tests
/// exist because the two failure modes of the fix are both silent: a re-record that duplicates rows (the
/// reconciliation pass re-reads the same activity log every minute, all day), and a foreign key that
/// rejects a fill from an order this system did not place.
/// </para>
/// </summary>
[TestClass]
public sealed class OrderLedgerPersistenceTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"agentfox-ledger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    [TestMethod]
    public async Task BrokerOrders_AreKeyedByExchangeNumber_AndReRecordUpdatesRatherThanDuplicates()
    {
        var repository = NewRepository();
        var claim = await repository.TryBeginExecutionAsync("idem-1", "{}", "v1");
        Assert.IsTrue(claim.Acquired);

        // Two attempts in one execution: one the broker numbered, one it did not.
        await repository.RecordBrokerOrdersAsync(claim.ExecutionId, new List<OrderResult>
        {
            new() { Success = true,  OrderId = "0010TJZJC700RH12", Action = "BUY",  Symbol = "SYS", Quantity = 1 },
            new() { Success = false, OrderId = null,                Action = "SELL", Symbol = "SYS", Quantity = 1 }
        });

        var rows = QueryBrokerOrders();
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual("0010TJZJC700RH12", rows[0].BrokerOrderId, "the exchange number is the key when there is one");
        Assert.AreEqual("accepted", rows[0].State);
        StringAssert.StartsWith(rows[1].BrokerOrderId, "pending:",
            "an attempt with no exchange number must be visibly unnumbered, not given a plausible-looking id");
        Assert.AreEqual("failed", rows[1].State);

        // The same execution recorded again — as happens when a result is re-persisted — must UPDATE the
        // two attempts, not add two more. This is the assertion that would fail if the upsert keyed on the
        // exchange number, because the unnumbered attempt has no stable one.
        await repository.RecordBrokerOrdersAsync(claim.ExecutionId, new List<OrderResult>
        {
            new() { Success = true, OrderId = "0010TJZJC700RH12", Action = "BUY",  Symbol = "SYS", Quantity = 1 },
            new() { Success = true, OrderId = "0611XK66",         Action = "SELL", Symbol = "SYS", Quantity = 1 }
        });

        rows = QueryBrokerOrders();
        Assert.AreEqual(2, rows.Count, "a re-record must not duplicate the attempts");
        Assert.AreEqual("0611XK66", rows[1].BrokerOrderId,
            "the second attempt's number arrived late and must replace the pending placeholder");
        Assert.AreEqual("accepted", rows[1].State);
    }

    [TestMethod]
    public async Task Fills_AreIdempotent_AcrossRepeatedReconciliationPasses()
    {
        var repository = NewRepository();
        var filledUtc = new DateTime(2026, 8, 19, 7, 18, 13, DateTimeKind.Utc);

        var fills = new List<BrokerFill>
        {
            new("0010TJZJC700RH12", "SYS", "BUY", 1, 115.89m, filledUtc)
        };

        Assert.AreEqual(1, await repository.RecordFillsAsync(fills), "the first pass stores the fill");
        Assert.AreEqual(0, await repository.RecordFillsAsync(fills),
            "the same fill seen again must store nothing — reconciliation re-reads today's whole log every "
          + "pass, so an append-only insert would multiply it by the passes left in the day");
        Assert.AreEqual(1, CountFills());
    }

    [TestMethod]
    public async Task Fills_FromAnOrderThisSystemNeverPlaced_AreStoredRatherThanDroppedByTheForeignKey()
    {
        var repository = NewRepository();

        // A manual order placed in the portal by a human. fills.broker_order_id is a foreign key and
        // PRAGMA foreign_keys is ON, so without a parent row this insert would be rejected — and the
        // position would move with nothing in the ledger to explain it.
        var stored = await repository.RecordFillsAsync(new List<BrokerFill>
        {
            new("MANUAL-0001", "OGDC", "SEL", 10, 318.0m, DateTime.UtcNow)
        });

        Assert.AreEqual(1, stored);
        Assert.AreEqual(1, CountFills());

        var parents = QueryBrokerOrders();
        Assert.AreEqual(1, parents.Count);
        Assert.AreEqual("external", parents[0].State,
            "a fill from outside this system must be visible AS being from outside it");
    }

    [TestMethod]
    public async Task UnknownExecution_RequiresOneExplicitAuditedResolution()
    {
        var repository = NewRepository();
        var claim = await repository.TryBeginExecutionAsync("unknown-1", "{}", "v1");
        await repository.CompleteExecutionAsync(
            claim.ExecutionId, "unknown", "{\"reason\":\"broker reply lost\"}");

        Assert.AreEqual(1, (await repository.GetStatusAsync()).UnknownExecutions);
        Assert.IsTrue(await repository.ResolveUnknownExecutionAsync(
            claim.ExecutionId, "placed", "{\"note\":\"found in broker activity\"}"));
        Assert.IsFalse(await repository.ResolveUnknownExecutionAsync(
            claim.ExecutionId, "not_placed", "{\"note\":\"stale second click\"}"),
            "the compare-and-set must not let a second decision rewrite the first one");

        Assert.AreEqual(0, (await repository.GetStatusAsync()).UnknownExecutions);
        Assert.AreEqual("resolved_placed", (await repository.GetExecutionsAsync()).Single().State);
        var audit = (await repository.GetEventsAsync()).Single();
        Assert.AreEqual("unknown_resolved", audit.EventType);
        Assert.AreEqual("found in broker activity", audit.Payload.GetProperty("note").GetString());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_root, "trading", "trading.db")}");
        connection.Open();
        return connection;
    }

    private List<(string BrokerOrderId, string State)> QueryBrokerOrders()
    {
        using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT broker_order_id, state FROM broker_orders ORDER BY client_order_id";
        using var reader = command.ExecuteReader();

        var rows = new List<(string, string)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    private int CountFills()
    {
        using var connection = Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM fills";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
