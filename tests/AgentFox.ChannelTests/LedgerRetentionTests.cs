using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Persistence;

namespace AgentFox.ChannelTests;

/// <summary>
/// Retention for the ledger tables that had none until 2026-09-06: trading_executions and its
/// children, and reconciliation_runs.
///
/// <para>
/// The cases that matter are not "does DELETE work" but the two ways a sweeper on an audit trail
/// goes wrong: deleting a row somebody still needs, and failing halfway because the foreign keys
/// were cut in the wrong order.
/// </para>
/// </summary>
[TestClass]
public sealed class LedgerRetentionTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-retention-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string DbPath => Path.Combine(_root, "trading", "trading.db");

    private SqliteTradingRepository Repository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspaces:0"] = _root })
            .Build();

        return new SqliteTradingRepository(
            Options.Create(new TradingAgentOptions { DatabasePath = "trading/trading.db" }),
            configuration,
            NullLogger<SqliteTradingRepository>.Instance);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();
        var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        await pragma.ExecuteNonQueryAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>An execution plus one order event, one broker order and one fill hanging off it.</summary>
    private async Task SeedExecutionAsync(string id, string state, DateTime updatedUtc)
    {
        var stamp = updatedUtc.ToString("O");
        // Braces are the interpolation opener in a raw interpolated string, so JSON literals are
        // passed in as values rather than escaped inline.
        const string emptyJson = "{}";
        const string orderJson = "{\"Symbol\":\"OGDC\"}";
        await ExecuteAsync($"""
            INSERT INTO trading_executions
                (execution_id, idempotency_key, state, request_json, policy_version, created_utc, updated_utc)
            VALUES ('{id}', 'key-{id}', '{state}', '{emptyJson}', 'v1', '{stamp}', '{stamp}');

            INSERT INTO trading_order_events (execution_id, event_type, payload_json, created_utc)
            VALUES ('{id}', '{state}', '{emptyJson}', '{stamp}');

            INSERT INTO broker_orders
                (broker_order_id, execution_id, client_order_id, state, order_json, created_utc, updated_utc)
            VALUES ('bo-{id}', '{id}', 'cl-{id}', '{state}', '{orderJson}', '{stamp}', '{stamp}');

            INSERT INTO fills (fill_id, broker_order_id, quantity, price, filled_utc)
            VALUES ('f-{id}', 'bo-{id}', 10, '100', '{stamp}');
            """);
    }

    [TestMethod]
    public async Task PruningAnExecutionRemovesItsEventsBrokerOrdersAndFills()
    {
        var repository = Repository();
        await repository.GetStatusAsync();   // force schema creation

        await SeedExecutionAsync("old", "accepted", DateTime.UtcNow.AddDays(-30));

        // Foreign keys are ON and the children reference the parent, so this only succeeds if the
        // deletes run children-first. A wrong order fails the statement, not the test's assertions.
        var removed = await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14));

        Assert.AreEqual(1, removed);
        Assert.AreEqual(0, await CountAsync("trading_executions"));
        Assert.AreEqual(0, await CountAsync("trading_order_events"), "order events were orphaned");
        Assert.AreEqual(0, await CountAsync("broker_orders"), "broker orders were orphaned");
        Assert.AreEqual(0, await CountAsync("fills"), "fills were orphaned");
    }

    [TestMethod]
    public async Task RecentExecutionsAreKept()
    {
        var repository = Repository();
        await repository.GetStatusAsync();

        await SeedExecutionAsync("recent", "accepted", DateTime.UtcNow.AddDays(-2));

        Assert.AreEqual(0, await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14)));
        Assert.AreEqual(1, await CountAsync("trading_executions"));
        Assert.AreEqual(1, await CountAsync("fills"));
    }

    [TestMethod]
    public async Task AnUnresolvedExecutionIsNeverAgedOut()
    {
        // The case the whole guard exists for. 'unknown' means the broker's answer was never
        // established — the one row a human still has to reconcile by hand. Deleting it on a timer
        // would discard the evidence the ledger exists to hold, so age must not reach it.
        var repository = Repository();
        await repository.GetStatusAsync();

        await SeedExecutionAsync("unknown-old", "unknown", DateTime.UtcNow.AddDays(-400));
        await SeedExecutionAsync("submitting-old", "submitting", DateTime.UtcNow.AddDays(-400));

        Assert.AreEqual(0, await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14)));
        Assert.AreEqual(2, await CountAsync("trading_executions"));
        Assert.AreEqual(2, await CountAsync("fills"),
            "children of a kept execution must survive with it");
    }

    [TestMethod]
    public async Task PruningKeepsTerminalAndUnresolvedRowsStraightInOnePass()
    {
        var repository = Repository();
        await repository.GetStatusAsync();

        await SeedExecutionAsync("old-accepted", "accepted", DateTime.UtcNow.AddDays(-30));
        await SeedExecutionAsync("old-failed", "failed", DateTime.UtcNow.AddDays(-30));
        await SeedExecutionAsync("old-unknown", "unknown", DateTime.UtcNow.AddDays(-30));
        await SeedExecutionAsync("new-accepted", "accepted", DateTime.UtcNow.AddDays(-1));

        Assert.AreEqual(2, await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14)));
        Assert.AreEqual(2, await CountAsync("trading_executions"));
        Assert.AreEqual(2, await CountAsync("broker_orders"));
    }

    [TestMethod]
    public async Task ReconciliationSnapshotsArePrunedByAge()
    {
        var repository = Repository();
        await repository.GetStatusAsync();

        const string emptyJson = "{}";
        await ExecuteAsync($"""
            INSERT INTO reconciliation_runs (reconciliation_id, state, details_json, started_utc)
            VALUES ('r-old', 'healthy', '{emptyJson}', '{DateTime.UtcNow.AddDays(-30):O}'),
                   ('r-new', 'healthy', '{emptyJson}', '{DateTime.UtcNow.AddDays(-1):O}');
            """);

        Assert.AreEqual(1, await repository.PruneReconciliationRunsAsync(DateTime.UtcNow.AddDays(-14)));
        Assert.AreEqual(1, await CountAsync("reconciliation_runs"));
    }

    [TestMethod]
    public async Task PruningIsIdempotent()
    {
        var repository = Repository();
        await repository.GetStatusAsync();

        await SeedExecutionAsync("old", "accepted", DateTime.UtcNow.AddDays(-30));

        Assert.AreEqual(1, await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14)));
        Assert.AreEqual(0, await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14)),
            "a second sweep on the same day must be a no-op, not an error");
    }

    [TestMethod]
    public void RetentionDefaultsAreSetAndSweepable()
    {
        var options = new TradingAgentOptions();

        Assert.AreEqual(14, options.LedgerRetentionDays);
        Assert.AreEqual(14, options.ReconciliationRetentionDays);
    }
}
