using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Persistence;
using TradingAgent.Trading;

namespace AgentFox.ChannelTests;

/// <summary>
/// A same-day RETRY of a persistent order must be able to record its own placement row.
///
/// <para>
/// <c>persistent_order_placements</c> was created with <c>UNIQUE(intent_id, session_date)</c> — one row
/// per session, which was true before same-day retries existed. Once
/// <c>PersistentOrderWorker.TryAutoRetryFailedTodayAsync</c> was added, the second attempt of a day hit
/// SQLite error 19, the exception aborted the intent's whole lifecycle pass, and it repeated forever
/// because the condition never cleared. Observed live 2026-09-01 on a TISL intent for a manual-only
/// symbol: an ERR with a stack trace on every cycle, and that intent never maintained again.
/// </para>
/// </summary>
[TestClass]
public sealed class PersistentPlacementRetryTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-placements-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string DbPath => Path.Combine(_root, "trading", "trading.db");

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

    private async Task<string> DdlAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText =
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='persistent_order_placements'";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int> PlacementCountAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM persistent_order_placements";
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Recreates the pre-2026-09-01 table AND the row that was in it, so the migration has real data to
    /// carry across rather than an empty table it cannot get wrong.
    /// </summary>
    private async Task RollBackToTheNarrowConstraintAsync(string intentId, int attempt)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=off;
            DROP TABLE persistent_order_placements;
            CREATE TABLE persistent_order_placements (
                placement_id       TEXT PRIMARY KEY,
                intent_id          TEXT NOT NULL,
                session_date       TEXT NOT NULL,
                attempt            INTEGER NOT NULL,
                quantity           INTEGER NOT NULL,
                broker_order_no    TEXT NULL,
                execution_id       TEXT NULL,
                state              TEXT NOT NULL,
                requested_price    TEXT NULL,
                submitted_price    TEXT NULL,
                message            TEXT NULL,
                created_utc        TEXT NOT NULL,
                UNIQUE(intent_id, session_date),
                FOREIGN KEY (intent_id) REFERENCES persistent_order_intents(intent_id)
            );
            INSERT INTO persistent_order_placements
                (placement_id, intent_id, session_date, attempt, quantity, state, created_utc)
            VALUES ('pre-migration-row', $intent, '2026-09-01', $attempt, 100, 'failed',
                    '2026-09-01T04:00:00Z');
            """;
        command.Parameters.AddWithValue("$intent", intentId);
        command.Parameters.AddWithValue("$attempt", attempt);
        await command.ExecuteNonQueryAsync();
    }

    private static PersistentOrderPlacement Placement(string intentId, int attempt) => new()
    {
        PlacementId = Guid.NewGuid().ToString("N"),
        IntentId = intentId,
        SessionDate = new DateOnly(2026, 9, 1),
        Attempt = attempt,
        Quantity = 100,
        State = "failed",
        RequestedPrice = 3.36m,
        Message = $"attempt {attempt}",
        CreatedUtc = DateTime.UtcNow
    };

    private async Task<string> ArmIntentAsync(SqliteTradingRepository repository)
    {
        var intent = new PersistentOrderIntent
        {
            IntentId = Guid.NewGuid().ToString("N"),
            Symbol = "TISL",
            Action = "BUY",
            Quantity = 100,
            OrderType = "LIMIT",
            Price = 3.36m,
            ExpiresUtc = DateTime.UtcNow.AddDays(5),
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        await repository.SavePersistentOrderAsync(intent);
        return intent.IntentId;
    }

    private static readonly DateOnly Session = new(2026, 9, 1);

    [TestMethod]
    public async Task TwoAttemptsInOneSessionEachGetTheirOwnRow()
    {
        // The real sequence: claim the session's attempt, record that it failed, then claim a RETRY for
        // the same day and record that too. The second record is what used to throw.
        var repository = NewRepository();
        var intentId = await ArmIntentAsync(repository);

        var first = await repository.TryClaimPersistentOrderAttemptAsync(intentId, Session);
        Assert.IsTrue(first.Acquired);
        await repository.RecordPersistentOrderPlacementAsync(
            Placement(intentId, first.Attempt), "active", "first try");

        var retry = await repository.TryClaimPersistentOrderRetryAsync(intentId, Session);
        Assert.IsTrue(retry.Acquired, "a failed placement should be retryable the same day");
        Assert.AreNotEqual(first.Attempt, retry.Attempt, "a retry is a new attempt");
        await repository.RecordPersistentOrderPlacementAsync(
            Placement(intentId, retry.Attempt), "active", "retry");

        Assert.AreEqual(2, await PlacementCountAsync(),
            "both attempts must be on record — losing the retry loses what was actually sent");
    }

    [TestMethod]
    public async Task TheSameAttemptTwiceIsStillRefused()
    {
        // Widening the key must not switch it off. Re-recording an attempt number that already has a row
        // is a double-write, and the constraint is what catches it.
        var repository = NewRepository();
        var intentId = await ArmIntentAsync(repository);

        var first = await repository.TryClaimPersistentOrderAttemptAsync(intentId, Session);
        await repository.RecordPersistentOrderPlacementAsync(
            Placement(intentId, first.Attempt), "active", "first try");

        // Claim a retry so the intent is back in 'placing' and the state guard is satisfied, then record
        // the OLD attempt number anyway.
        await repository.TryClaimPersistentOrderRetryAsync(intentId, Session);

        var threw = false;
        try
        {
            await repository.RecordPersistentOrderPlacementAsync(
                Placement(intentId, first.Attempt), "active", "same attempt again");
        }
        catch (SqliteException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "re-recording the same attempt must still hit the constraint");
    }

    [TestMethod]
    public async Task AnOlderDatabaseIsMigratedWithoutLosingARow()
    {
        // The path every existing deployment takes. A row written under the narrow constraint has to
        // survive the rebuild, or the migration trades one bug for a worse one.
        var repository = NewRepository();
        var intentId = await ArmIntentAsync(repository);
        var first = await repository.TryClaimPersistentOrderAttemptAsync(intentId, Session);
        await repository.RecordPersistentOrderPlacementAsync(
            Placement(intentId, first.Attempt), "active", "before");

        var rowsBefore = await PlacementCountAsync();
        await RollBackToTheNarrowConstraintAsync(intentId, first.Attempt);
        StringAssert.Contains(await DdlAsync(), "UNIQUE(intent_id, session_date)");

        // A fresh repository re-runs initialization, which is where the migration lives.
        SqliteConnection.ClearAllPools();
        var migrated = NewRepository();
        var retry = await migrated.TryClaimPersistentOrderRetryAsync(intentId, Session);
        Assert.IsTrue(retry.Acquired);
        await migrated.RecordPersistentOrderPlacementAsync(
            Placement(intentId, retry.Attempt), "active", "retry after migration");

        StringAssert.Contains(await DdlAsync(), "UNIQUE(intent_id, session_date, attempt)");
        Assert.AreEqual(rowsBefore + 1, await PlacementCountAsync(),
            "the pre-migration row must still be there, plus the retry");
    }
}
