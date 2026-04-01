using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using AgentFox.Tools;

namespace AgentFox.Memory;

/// <summary>
/// Configuration for the long-term memory backend.
/// Controlled via the "Memory" section of appsettings.json.
/// </summary>
public class MemoryConfig
{
    /// <summary>Which backend to use: "Markdown" (default) or "Sqlite"</summary>
    public string LongTermStorage { get; set; } = "Markdown";

    /// <summary>Relative path for the markdown file (resolved against workspace)</summary>
    public string MarkdownPath { get; set; } = "LongTermMemory.md";

    /// <summary>Relative path for the SQLite database (resolved against workspace)</summary>
    public string SqlitePath { get; set; } = "LongTermMemory.db";
}

/// <summary>
/// Creates the correct long-term memory backend from configuration.
/// </summary>
public static class MemoryBackendFactory
{
    public static IMemory CreateLongTermStorage(IConfiguration configuration, WorkspaceManager workspaceManager)
    {
        var config = configuration.GetSection("Memory").Get<MemoryConfig>() ?? new MemoryConfig();

        return config.LongTermStorage.Trim().ToLowerInvariant() switch
        {
            "sqlite" => new SqliteLongTermMemory(workspaceManager.ResolvePath(config.SqlitePath)),
            _        => new MarkdownLongTermMemory(workspaceManager.ResolvePath(config.MarkdownPath))
        };
    }
}

/// <summary>
/// SQLite-backed long-term memory with hybrid search.
///
/// Schema:
///   memories       — canonical store (id, content, type, timestamp, importance, metadata)
///   memories_fts   — FTS5 virtual table mirroring content, kept in sync via triggers
///
/// Hybrid search score = BM25 relevance × 0.5 + importance × 0.4 + recency bonus × 0.1
/// Falls back to LIKE search if the FTS query cannot be parsed.
/// </summary>
public class SqliteLongTermMemory : IMemory, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteLongTermMemory(string? dbPath = null)
    {
        var path = dbPath ?? "long_term_memory.db";
        _connectionString = $"Data Source={path};Mode=ReadWriteCreate;Cache=Shared";
        InitializeSchema();
    }

    // -------------------------------------------------------------------------
    // IMemory implementation
    // -------------------------------------------------------------------------

    public async Task AddAsync(MemoryEntry entry)
    {
        await _writeLock.WaitAsync();
        try
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();

            // Upsert — fires AFTER UPDATE trigger on conflict (keeps FTS in sync)
            cmd.CommandText = @"
                INSERT INTO memories (id, content, type, timestamp, importance, metadata)
                VALUES (@id, @content, @type, @timestamp, @importance, @metadata)
                ON CONFLICT(id) DO UPDATE SET
                    content    = excluded.content,
                    importance = excluded.importance,
                    metadata   = excluded.metadata;
            ";
            cmd.Parameters.AddWithValue("@id",         entry.Id);
            cmd.Parameters.AddWithValue("@content",    entry.Content);
            cmd.Parameters.AddWithValue("@type",       entry.Type.ToString());
            cmd.Parameters.AddWithValue("@timestamp",  entry.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@importance", entry.Importance);
            cmd.Parameters.AddWithValue("@metadata",   System.Text.Json.JsonSerializer.Serialize(entry.Metadata));
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<MemoryEntry>> SearchAsync(string query, int limit = 10)
    {
        await using var conn = OpenConnection();
        try
        {
            return await SearchFts5Async(conn, query, limit);
        }
        catch
        {
            // FTS5 query parse error — fall back to substring match
            return await SearchFallbackAsync(conn, query, limit);
        }
    }

    public async Task<List<MemoryEntry>> GetAllAsync()
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, content, type, timestamp, importance, metadata
            FROM memories
            ORDER BY timestamp DESC;
        ";
        return await ReadEntriesAsync(cmd);
    }

    public async Task<List<MemoryEntry>> GetRecentAsync(int count = 10)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, content, type, timestamp, importance, metadata
            FROM memories
            ORDER BY timestamp DESC
            LIMIT @count;
        ";
        cmd.Parameters.AddWithValue("@count", count);
        return await ReadEntriesAsync(cmd);
    }

    public async Task ClearAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            await using var conn = OpenConnection();
            await using var cmd = conn.CreateCommand();
            // Delete all rows, then rebuild FTS index
            cmd.CommandText = @"
                DELETE FROM memories;
                INSERT INTO memories_fts(memories_fts) VALUES('rebuild');
            ";
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        SqliteConnection.ClearAllPools();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void InitializeSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            -- Canonical memory store
            CREATE TABLE IF NOT EXISTS memories (
                rowid     INTEGER PRIMARY KEY AUTOINCREMENT,
                id        TEXT    NOT NULL UNIQUE,
                content   TEXT    NOT NULL,
                type      TEXT    NOT NULL DEFAULT 'Fact',
                timestamp TEXT    NOT NULL,
                importance REAL   NOT NULL DEFAULT 0.5,
                metadata  TEXT    NOT NULL DEFAULT '{}'
            );

            -- FTS5 virtual table (mirrors 'content' column of memories)
            CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(
                content,
                content='memories',
                content_rowid='rowid'
            );

            -- Keep FTS5 in sync with the base table
            CREATE TRIGGER IF NOT EXISTS memories_ai
                AFTER INSERT ON memories BEGIN
                    INSERT INTO memories_fts(rowid, content)
                    VALUES (new.rowid, new.content);
                END;

            CREATE TRIGGER IF NOT EXISTS memories_ad
                AFTER DELETE ON memories BEGIN
                    INSERT INTO memories_fts(memories_fts, rowid, content)
                    VALUES ('delete', old.rowid, old.content);
                END;

            CREATE TRIGGER IF NOT EXISTS memories_au
                AFTER UPDATE ON memories BEGIN
                    INSERT INTO memories_fts(memories_fts, rowid, content)
                    VALUES ('delete', old.rowid, old.content);
                    INSERT INTO memories_fts(rowid, content)
                    VALUES (new.rowid, new.content);
                END;
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Hybrid search: BM25 text relevance (50%) + importance (40%) + recency bonus (10%).
    /// BM25 rank in FTS5 is negative — more negative = better match, so we negate it.
    /// </summary>
    private static async Task<List<MemoryEntry>> SearchFts5Async(
        SqliteConnection conn, string query, int limit)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT m.id, m.content, m.type, m.timestamp, m.importance, m.metadata
            FROM memories m
            JOIN memories_fts ON memories_fts.rowid = m.rowid
            WHERE memories_fts MATCH @query
            ORDER BY (
                (-memories_fts.rank) * 0.5
                + m.importance       * 0.4
                + CASE WHEN julianday(m.timestamp) > julianday('now') - 30
                       THEN 0.1 ELSE 0.0 END
            ) DESC
            LIMIT @limit;
        ";
        cmd.Parameters.AddWithValue("@query", BuildFtsQuery(query));
        cmd.Parameters.AddWithValue("@limit", limit);
        return await ReadEntriesAsync(cmd);
    }

    /// <summary>Fallback when FTS5 cannot parse the query — uses LIKE substring match.</summary>
    private static async Task<List<MemoryEntry>> SearchFallbackAsync(
        SqliteConnection conn, string query, int limit)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, content, type, timestamp, importance, metadata
            FROM memories
            WHERE content LIKE @pattern
            ORDER BY importance DESC, timestamp DESC
            LIMIT @limit;
        ";
        cmd.Parameters.AddWithValue("@pattern", $"%{query}%");
        cmd.Parameters.AddWithValue("@limit", limit);
        return await ReadEntriesAsync(cmd);
    }

    private static async Task<List<MemoryEntry>> ReadEntriesAsync(SqliteCommand cmd)
    {
        var results = new List<MemoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var entry = new MemoryEntry
            {
                Id         = reader.GetString(0),
                Content    = reader.GetString(1),
                Timestamp  = DateTime.Parse(reader.GetString(3), null,
                                 System.Globalization.DateTimeStyles.RoundtripKind),
                Importance = reader.GetDouble(4)
            };

            if (Enum.TryParse<MemoryType>(reader.GetString(2), ignoreCase: true, out var type))
                entry.Type = type;

            try
            {
                var meta = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string>>(reader.GetString(5));
                if (meta != null) entry.Metadata = meta;
            }
            catch { /* ignore bad metadata */ }

            results.Add(entry);
        }
        return results;
    }

    /// <summary>
    /// Converts a plain-text search phrase into a safe FTS5 query.
    /// Each word is quoted so special FTS5 operators are treated as literals.
    /// Example: "user python" → "user" "python"
    /// </summary>
    private static string BuildFtsQuery(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => $"\"{w.Replace("\"", "\"\"")}\"");
        return string.Join(" ", terms);
    }
}
