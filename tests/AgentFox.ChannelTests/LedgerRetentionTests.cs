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
        var removed = (await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14))).Removed;

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

        Assert.AreEqual(0, (await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14))).Removed);
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

        Assert.AreEqual(0, (await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14))).Removed);
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

        Assert.AreEqual(2, (await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14))).Removed);
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

        Assert.AreEqual(1, (await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14))).Removed);
        Assert.AreEqual(0, (await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14))).Removed,
            "a second sweep on the same day must be a no-op, not an error");
    }

    /// <summary>An automation campaign, open unless <paramref name="closedUtc"/> is given.</summary>
    private async Task SeedCampaignAsync(
        string id, string symbol, DateTime startedUtc, DateTime? closedUtc = null)
    {
        const string profileJson = "{}";
        var started = startedUtc.ToString("O");
        var closed = closedUtc is null ? "NULL" : $"'{closedUtc.Value:O}'";
        await ExecuteAsync($"""
            INSERT INTO automation_campaigns
                (campaign_id, symbol, profile_id, profile_json, state, origin, deployed_pkr,
                 max_legs, completed_legs, quantity, started_utc, updated_utc, closed_utc)
            VALUES ('{id}', '{symbol}', 'p1', '{profileJson}', 'running', 'auto', '0',
                    1, 0, 10, '{started}', '{started}', {closed});
            """);
    }

    [TestMethod]
    public async Task AnOpenCampaignHoldsBackTheCutoff_SoItsFillsSurvive()
    {
        // The owner's decision, pinned: functionality outranks the retention cap. A campaign that
        // has been running for 90 days still needs its OPENING fills when it closes, because
        // realised P&L is computed from every fill back to StartedUtc. Sweeping them would produce
        // a plausible wrong number rather than an error — and then keep it for 1095 days.
        var repository = Repository();
        await repository.GetStatusAsync();

        await SeedExecutionAsync("campaign-entry", "accepted", DateTime.UtcNow.AddDays(-90));
        await SeedCampaignAsync("c1", "OGDC", DateTime.UtcNow.AddDays(-95));

        var result = await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14));

        Assert.AreEqual(0, result.Removed,
            "A 14-day cutoff must be pulled back behind a campaign open since day 95.");
        Assert.AreEqual(1, await CountAsync("fills"), "the campaign's opening fill was swept");
        Assert.IsTrue(result.HeldBackByOpenCampaign,
            "The sweep must SAY it was held back — otherwise 'nothing was old enough' and 'a "
            + "campaign is holding the cutoff' look identical to an operator.");
        Assert.IsTrue(result.EffectiveCutoff < DateTime.UtcNow.AddDays(-90),
            "The reported cutoff must be the campaign's start, not the configured window.");
    }

    [TestMethod]
    public async Task AClosedCampaignDoesNotHoldBackTheCutoff()
    {
        // Once closed, its realised figure is already computed and stored. Holding retention back
        // for it forever would make the floor a leak rather than a safeguard.
        var repository = Repository();
        await repository.GetStatusAsync();

        await SeedExecutionAsync("old", "accepted", DateTime.UtcNow.AddDays(-90));
        await SeedCampaignAsync("c1", "OGDC", DateTime.UtcNow.AddDays(-95), DateTime.UtcNow.AddDays(-60));

        Assert.AreEqual(1, (await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14))).Removed);
        Assert.AreEqual(0, await CountAsync("fills"));
    }

    [TestMethod]
    public async Task TheFloorIsTheOLDESTOpenCampaign_NotTheNewest()
    {
        // Taking the newest would sweep the older campaign's fills — the failure this guards, with
        // one extra campaign present to hide it.
        var repository = Repository();
        await repository.GetStatusAsync();

        await SeedExecutionAsync("old", "accepted", DateTime.UtcNow.AddDays(-90));
        await SeedCampaignAsync("recent", "PAEL", DateTime.UtcNow.AddDays(-3));
        await SeedCampaignAsync("ancient", "OGDC", DateTime.UtcNow.AddDays(-120));

        Assert.AreEqual(0, (await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14))).Removed);
        Assert.AreEqual(1, await CountAsync("trading_executions"));
    }

    [TestMethod]
    public async Task AnOpenCampaignNewerThanTheCutoffChangesNothing()
    {
        // The floor only ever pulls the cutoff BACK. A campaign started yesterday must not extend
        // retention forward and start sweeping rows the operator configured to keep.
        var repository = Repository();
        await repository.GetStatusAsync();

        await SeedExecutionAsync("old", "accepted", DateTime.UtcNow.AddDays(-90));
        await SeedExecutionAsync("recent", "accepted", DateTime.UtcNow.AddDays(-2));
        await SeedCampaignAsync("c1", "OGDC", DateTime.UtcNow.AddDays(-1));

        var result = await repository.PruneExecutionsAsync(DateTime.UtcNow.AddDays(-14));

        Assert.AreEqual(1, result.Removed);
        Assert.AreEqual(1, await CountAsync("trading_executions"), "the recent execution was swept");
        Assert.IsFalse(result.HeldBackByOpenCampaign,
            "A campaign newer than the cutoff is not holding anything back and must not say it is.");
    }

    [TestMethod]
    public async Task ReconciliationSnapshotsAreNotHeldBackByAnOpenCampaign()
    {
        // Deliberately exempt: nothing reads a historical snapshot, so no campaign depends on one.
        // Giving them the floor too would defeat the sweep that actually needed writing.
        var repository = Repository();
        await repository.GetStatusAsync();

        await SeedCampaignAsync("c1", "OGDC", DateTime.UtcNow.AddDays(-120));
        const string emptyJson = "{}";
        await ExecuteAsync($"""
            INSERT INTO reconciliation_runs (reconciliation_id, state, details_json, started_utc)
            VALUES ('r-old', 'healthy', '{emptyJson}', '{DateTime.UtcNow.AddDays(-30):O}');
            """);

        Assert.AreEqual(1, await repository.PruneReconciliationRunsAsync(DateTime.UtcNow.AddDays(-14)));
    }

    [TestMethod]
    public void RetentionDefaultsAreSetAndSweepable()
    {
        var options = new TradingAgentOptions();

        Assert.AreEqual(14, options.LedgerRetentionDays);
        Assert.AreEqual(14, options.ReconciliationRetentionDays);
    }
}
