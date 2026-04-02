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

    /// <summary>Name of a model entry under the top-level "Models" section to use for embeddings.</summary>
    public string? ModelRef { get; set; }
}

/// <summary>
/// Creates the correct long-term memory backend from configuration.
/// </summary>
public static class MemoryBackendFactory
{
    public static IMemory CreateLongTermStorage(IConfiguration configuration, WorkspaceManager workspaceManager)
    {
        var config = configuration.GetSection("Memory").Get<MemoryConfig>() ?? new MemoryConfig();
        var embeddingService = EmbeddingServiceFactory.Create(configuration);

        return config.LongTermStorage.Trim().ToLowerInvariant() switch
        {
            "sqlite" => new SqliteLongTermMemory(
                workspaceManager.ResolvePath(config.SqlitePath),
                embeddingService),
            _ => new MarkdownLongTermMemory(workspaceManager.ResolvePath(config.MarkdownPath))
        };
    }
}

/// <summary>
/// SQLite-backed long-term memory with hybrid BM25 + vector search.
///
/// Schema:
///   memories        — canonical store (id, content, type, timestamp, importance, metadata)
///   memories_fts    — FTS5 virtual table mirroring content, kept in sync via triggers
///   memory_vectors  — per-entry float32 embedding stored as BLOB; populated when an
///                     IEmbeddingService is configured
///
/// Search priority:
///   1. Vector cosine similarity (when embedding service is configured and entry has a vector)
///   2. Hybrid BM25 score = BM25 relevance × 0.5 + importance × 0.4 + recency bonus × 0.1
///   3. LIKE fallback if FTS5 query cannot be parsed
/// </summary>
public class SqliteLongTermMemory : IMemory, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly IEmbeddingService _embedding;

    public SqliteLongTermMemory(string? dbPath = null, IEmbeddingService? embeddingService = null)
    {
        var path = dbPath ?? "long_term_memory.db";
        _connectionString = $"Data Source={path};Mode=ReadWriteCreate;Cache=Shared";
        _embedding = embeddingService ?? new NullEmbeddingService();
        InitializeSchema();
    }

    // -------------------------------------------------------------------------
    // IMemory implementation
    // -------------------------------------------------------------------------

    public async Task AddAsync(MemoryEntry entry)
    {
        // Generate embedding before acquiring the write lock to keep critical section short.
        var vector = await _embedding.GenerateAsync(entry.Content);

        await _writeLock.WaitAsync();
        try
        {
            await using var conn = OpenConnection();
            await using var tx = conn.BeginTransaction();

            // Upsert canonical row (fires AFTER UPDATE trigger — keeps FTS in sync)
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
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

            // Store embedding when available
            if (vector is { Length: > 0 })
            {
                await using var vecCmd = conn.CreateCommand();
                vecCmd.Transaction = tx;
                vecCmd.CommandText = @"
                    INSERT INTO memory_vectors (id, embedding, dims)
                    VALUES (@id, @embedding, @dims)
                    ON CONFLICT(id) DO UPDATE SET
                        embedding = excluded.embedding,
                        dims      = excluded.dims;
                ";
                vecCmd.Parameters.AddWithValue("@id",        entry.Id);
                vecCmd.Parameters.AddWithValue("@embedding", FloatsToBlob(vector));
                vecCmd.Parameters.AddWithValue("@dims",      vector.Length);
                await vecCmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<MemoryEntry>> SearchAsync(string query, int limit = 10)
    {
        // Try vector search first when embedding service is configured.
        var queryVector = await _embedding.GenerateAsync(query);
        if (queryVector is { Length: > 0 })
        {
            var vectorResults = await SearchByVectorAsync(queryVector, limit);
            if (vectorResults.Count > 0)
                return vectorResults;
        }

        // Fall back to BM25 / LIKE search.
        await using var conn = OpenConnection();
        try
        {
            return await SearchFts5Async(conn, query, limit);
        }
        catch
        {
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
            cmd.CommandText = @"
                DELETE FROM memories;
                DELETE FROM memory_vectors;
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
                rowid      INTEGER PRIMARY KEY AUTOINCREMENT,
                id         TEXT    NOT NULL UNIQUE,
                content    TEXT    NOT NULL,
                type       TEXT    NOT NULL DEFAULT 'Fact',
                timestamp  TEXT    NOT NULL,
                importance REAL    NOT NULL DEFAULT 0.5,
                metadata   TEXT    NOT NULL DEFAULT '{}'
            );

            -- FTS5 virtual table (mirrors 'content' column of memories)
            CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(
                content,
                content='memories',
                content_rowid='rowid'
            );

            -- Per-entry vector embeddings (float32 stored as BLOB)
            CREATE TABLE IF NOT EXISTS memory_vectors (
                id        TEXT  NOT NULL PRIMARY KEY,
                embedding BLOB  NOT NULL,
                dims      INTEGER NOT NULL
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
                    DELETE FROM memory_vectors WHERE id = old.id;
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
    /// Loads all stored vectors, computes cosine similarity against the query vector in C#,
    /// and returns the top-k entries ranked by:
    ///   score = cosine_similarity × 0.6 + importance × 0.3 + recency_bonus × 0.1
    /// </summary>
    private async Task<List<MemoryEntry>> SearchByVectorAsync(float[] queryVector, int limit)
    {
        // Load all vectors in one query (typically < a few thousand rows for a personal agent).
        var vectors = new Dictionary<string, float[]>();
        await using var conn = OpenConnection();
        await using var vecCmd = conn.CreateCommand();
        vecCmd.CommandText = "SELECT id, embedding FROM memory_vectors;";
        await using var vecReader = await vecCmd.ExecuteReaderAsync();
        while (await vecReader.ReadAsync())
        {
            var id   = vecReader.GetString(0);
            var blob = (byte[])vecReader["embedding"];
            vectors[id] = BlobToFloats(blob);
        }

        if (vectors.Count == 0)
            return [];

        // Load all memory entries so we can join with scores.
        await using var memCmd = conn.CreateCommand();
        memCmd.CommandText = @"
            SELECT id, content, type, timestamp, importance, metadata
            FROM memories
            WHERE id IN (SELECT id FROM memory_vectors);
        ";
        var entries = await ReadEntriesAsync(memCmd);

        // Score and rank.
        var now = DateTime.UtcNow;
        var minCosine = 0.55f; //0.65f; // safe default
        
        //var scored = entries
        //    .Where(e => vectors.ContainsKey(e.Id))
        //    .Select(e =>
        //    {
        //        var cosine = CosineSimilarity(queryVector, vectors[e.Id]);
        //        return (entry: e, cosine);
        //    })
        //    .Where(x => x.cosine >= minCosine)
        //    .Select(x =>
        //    {
        //        var recency = (now - x.entry.Timestamp).TotalDays < 30 ? 0.1f : 0f;
        //        var score = x.cosine * 0.6f + (float)x.entry.Importance * 0.3f + recency;
        //        return (x.entry, score);
        //    })
        //    //.Select(e =>
        //    //{
        //    //    var cosine   = CosineSimilarity(queryVector, vectors[e.Id]);
        //    //    var recency  = (now - e.Timestamp).TotalDays < 30 ? 0.1f : 0f;
        //    //    var score    = cosine * 0.6f + (float)e.Importance * 0.3f + recency;
        //    //    return (entry: e, score);
        //    //})
        //    .OrderByDescending(x => x.score)
        //    .Take(limit)
        //    .Select(x => x.entry)
        //    .ToList();
        //return scored;

        var candidates = entries
            .Where(e => vectors.ContainsKey(e.Id))
            .Select(e =>
            {
                var cosine = CosineSimilarity(queryVector, vectors[e.Id]);
                return (entry: e, cosine);
            })
            .OrderByDescending(x => x.cosine)
            .ToList();

        if (!candidates.Any())
            return new List<MemoryEntry>();

        var max = candidates.First().cosine;
        var mean = candidates.Average(x => x.cosine);
        var std = MathF.Sqrt(candidates.Average(x => MathF.Pow(x.cosine - mean, 2)));

        //adaptive threshold
        var threshold = Math.Max(
            minCosine,                 // hard floor
            max - std * 0.5f       // dynamic band
        );

        var filtered = candidates
            .Where(x => x.cosine >= threshold)
            .Select(x => x.entry)
            .ToList();

        return filtered;
    }

    /// <summary>
    /// Hybrid BM25 search: BM25 text relevance (50%) + importance (40%) + recency bonus (10%).
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
    /// </summary>
    private static string BuildFtsQuery(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => $"\"{w.Replace("\"", "\"\"")}\"");
        return string.Join(" ", terms);
    }

    // -------------------------------------------------------------------------
    // Vector helpers
    // -------------------------------------------------------------------------

    private static byte[] FloatsToBlob(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BlobToFloats(byte[] blob)
    {
        var floats = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, floats, 0, blob.Length);
        return floats;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        float dot = 0f, normA = 0f, normB = 0f;
        for (int i = 0; i < len; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom < 1e-8f ? 0f : dot / denom;
    }
}
