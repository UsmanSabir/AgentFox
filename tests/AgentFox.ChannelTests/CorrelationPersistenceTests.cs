using AgentFox.Plugins.Observability;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Persistence;

namespace AgentFox.ChannelTests;

/// <summary>
/// The correlation id actually reaching SQLite, against a real database file.
///
/// <para>
/// Worth its own fixture because the writes go through hand-written SQL and an additive migration,
/// and both fail in ways unit tests of the ambient context cannot see: a column named in an INSERT
/// but never added by the migration throws at runtime on the first write, and an id read from the
/// wrong place stores NULL forever while every other test still passes.
/// </para>
/// </summary>
[TestClass]
public sealed class CorrelationPersistenceTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-correlation-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

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

    private async Task<string?> ReadCorrelationAsync(string table, string idColumn, string id)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_root, "trading", "trading.db")}");
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = $"SELECT correlation_id FROM {table} WHERE {idColumn} = $id";
        command.Parameters.AddWithValue("$id", id);

        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? null : (string)value;
    }

    [TestMethod]
    public async Task AnExecutionRecordsTheAmbientCorrelationId()
    {
        var repository = Repository();

        using (CorrelationContext.Begin("exec-correlation"))
        {
            var claim = await repository.TryBeginExecutionAsync("idem-1", "{}", "policy-v1");
            Assert.IsTrue(claim.Acquired);

            Assert.AreEqual(
                "exec-correlation",
                await ReadCorrelationAsync("trading_executions", "execution_id", claim.ExecutionId),
                "The execution row must carry the correlation of whatever caused it.");

            // The ledger event written against that execution must agree, or a trace can find the
            // submission and not the events it produced.
            await repository.AppendEventAsync(claim.ExecutionId, "accepted", "{}");
        }

        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_root, "trading", "trading.db")}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT correlation_id FROM trading_order_events";
        Assert.AreEqual("exec-correlation", (string?)await command.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task AProposalRecordsTheAmbientCorrelationId()
    {
        var repository = Repository();

        using (CorrelationContext.Begin("proposal-correlation"))
        {
            var proposalId = await repository.CreateProposalAsync("idem-p1", "{}", "policy-v1");

            Assert.AreEqual(
                "proposal-correlation",
                await ReadCorrelationAsync("trade_proposals", "proposal_id", proposalId));
        }
    }

    [TestMethod]
    public async Task AWriteOutsideAnyCorrelationStoresNull_NotAMintedId()
    {
        // Unknown is not zero, and here it is not a fresh id either. A row written with no cause
        // to point at must say so — inventing one produces a correlation group of exactly one row
        // that reads like real evidence when someone later queries by it.
        var repository = Repository();

        // Detached so no ambient scope leaks in from the test host.
        var executionId = await Task.Run(async () =>
        {
            Assert.IsNull(CorrelationContext.Current);
            var claim = await repository.TryBeginExecutionAsync("idem-2", "{}", "policy-v1");
            return claim.ExecutionId;
        });

        Assert.IsNull(await ReadCorrelationAsync("trading_executions", "execution_id", executionId));
    }

    [TestMethod]
    public async Task TheMigrationAppliesToADatabaseCreatedBeforeTheColumnExisted()
    {
        // The realistic upgrade path, not a fresh install: an existing trading.db with the old
        // schema must gain the column rather than throwing on the first write after an update.
        var databasePath = Path.Combine(_root, "trading", "trading.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        await using (var seed = new SqliteConnection($"Data Source={databasePath}"))
        {
            await seed.OpenAsync();
            var create = seed.CreateCommand();
            create.CommandText = """
                CREATE TABLE trading_executions (
                    execution_id TEXT PRIMARY KEY,
                    idempotency_key TEXT NOT NULL UNIQUE,
                    state TEXT NOT NULL,
                    request_json TEXT NOT NULL,
                    result_json TEXT NULL,
                    policy_version TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var repository = Repository();
        using (CorrelationContext.Begin("after-upgrade"))
        {
            var claim = await repository.TryBeginExecutionAsync("idem-upgrade", "{}", "policy-v1");

            Assert.AreEqual(
                "after-upgrade",
                await ReadCorrelationAsync("trading_executions", "execution_id", claim.ExecutionId),
                "A pre-existing database must be migrated, not left one column short of the INSERT.");
        }
    }
}
