using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Data.Sqlite;

namespace AgentFox.Runtime;

/// <summary>
/// SQLite-backed implementation of <see cref="ICheckpointStore{T}"/> for
/// <see cref="JsonElement"/> payloads.
///
/// Integrates with <c>CheckpointManager.CreateJson(store, options)</c> so the
/// same store can be handed to <c>InProcessExecution</c> when workflows are
/// used, while <see cref="ConversationCheckpointService"/> uses it directly
/// for turn-level checkpointing of <c>ChatClientAgent</c> sessions.
///
/// Schema
/// ------
///   checkpoints
///     checkpoint_id  TEXT  PK
///     session_id     TEXT  (indexed)
///     parent_id      TEXT  (nullable — checkpoint chain)
///     created_at     TEXT  ISO-8601 UTC
///     data           TEXT  raw JSON
/// </summary>
public sealed class SqliteJsonCheckpointStore : ICheckpointStore<JsonElement>, IDisposable
{
    private readonly string _connectionString;
    private volatile bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public SqliteJsonCheckpointStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
    }

    // ── ICheckpointStore<JsonElement> ────────────────────────────────────────

    public async ValueTask<CheckpointInfo> CreateCheckpointAsync(
        string sessionId, JsonElement value, CheckpointInfo? parent)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        var checkpointId = Guid.NewGuid().ToString("N");
        var info = new CheckpointInfo(sessionId, checkpointId);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO checkpoints (checkpoint_id, session_id, parent_id, created_at, data)
            VALUES ($cid, $sid, $pid, $ts, $data)
            """;
        cmd.Parameters.AddWithValue("$cid", checkpointId);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$pid", (object?)parent?.CheckpointId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$data", value.GetRawText());
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        return info;
    }

    public async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT data FROM checkpoints
            WHERE session_id = $sid AND checkpoint_id = $cid
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$cid", key.CheckpointId);

        var raw = (string?)await cmd.ExecuteScalarAsync().ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"Checkpoint '{key.CheckpointId}' not found for session '{sessionId}'.");

        return JsonSerializer.Deserialize<JsonElement>(raw);
    }

    public async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(
        string sessionId, CheckpointInfo? withParent)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        if (withParent is null)
        {
            cmd.CommandText = """
                SELECT checkpoint_id FROM checkpoints
                WHERE session_id = $sid
                ORDER BY created_at
                """;
            cmd.Parameters.AddWithValue("$sid", sessionId);
        }
        else
        {
            cmd.CommandText = """
                SELECT checkpoint_id FROM checkpoints
                WHERE session_id = $sid AND parent_id = $pid
                ORDER BY created_at
                """;
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.Parameters.AddWithValue("$pid", withParent.CheckpointId);
        }

        var results = new List<CheckpointInfo>();
        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            results.Add(new CheckpointInfo(sessionId, reader.GetString(0)));

        return results;
    }

    // ── Additional helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Returns metadata (id + timestamp) for all checkpoints of a session,
    /// newest first.
    /// </summary>
    public async Task<IReadOnlyList<CheckpointEntry>> ListEntriesAsync(string sessionId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT checkpoint_id, created_at
            FROM checkpoints
            WHERE session_id = $sid
            ORDER BY created_at DESC
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);

        var results = new List<CheckpointEntry>();
        using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            results.Add(new CheckpointEntry(
                new CheckpointInfo(sessionId, reader.GetString(0)),
                DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return results;
    }

    /// <summary>Deletes all checkpoints stored for a session.</summary>
    public async Task DeleteSessionCheckpointsAsync(string sessionId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM checkpoints WHERE session_id = $sid";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    // ── Initialization ────────────────────────────────────────────────────────

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS checkpoints (
                    checkpoint_id TEXT NOT NULL PRIMARY KEY,
                    session_id    TEXT NOT NULL,
                    parent_id     TEXT,
                    created_at    TEXT NOT NULL,
                    data          TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_chk_session
                    ON checkpoints (session_id, created_at);
                """;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public void Dispose() => _initLock.Dispose();
}

/// <summary>Checkpoint metadata row returned by <see cref="SqliteJsonCheckpointStore.ListEntriesAsync"/>.</summary>
public sealed record CheckpointEntry(CheckpointInfo Info, DateTime CreatedAt);
