namespace AgentFox.Doctor.Checks;

using AgentFox.Doctor;
using AgentFox.Memory;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

public class MemoryHealthCheck : IHealthCheckable
{
    private readonly IMemory _longTermMemory;
    private readonly IConfiguration _config;
    private readonly string _resolvedDbPath;

    public string ComponentName => "Long-Term Memory";

    public MemoryHealthCheck(IMemory longTermMemory, IConfiguration config, string workspacePath)
    {
        _longTermMemory = longTermMemory;
        _config = config;
        var sqlitePath = config["Memory:SqlitePath"] ?? "LongTermMemory.db";
        _resolvedDbPath = Path.IsPathRooted(sqlitePath)
            ? sqlitePath
            : Path.Combine(workspacePath, sqlitePath);
    }

    public async Task<IReadOnlyList<HealthCheckResult>> CheckHealthAsync(CancellationToken ct = default)
    {
        var results = new List<HealthCheckResult>();
        var storage = (_config["Memory:LongTermStorage"] ?? "sqlite").Trim().ToLowerInvariant();

        if (storage == "sqlite")
            await CheckSqliteAsync(results, ct);
        else
            CheckMarkdown(results);

        return results;
    }

    private async Task CheckSqliteAsync(List<HealthCheckResult> results, CancellationToken ct)
    {
        if (!File.Exists(_resolvedDbPath))
        {
            results.Add(Warning($"SQLite DB not yet created at {_resolvedDbPath} (will be created on first memory write)"));
            return;
        }

        results.Add(Healthy($"SQLite DB exists: {_resolvedDbPath}"));

        try
        {
            var cs = $"Data Source={_resolvedDbPath};Mode=ReadOnly;Cache=Shared";
            await using var conn = new SqliteConnection(cs);
            conn.Open();

            // 1. Integrity check
            await using var intCmd = conn.CreateCommand();
            intCmd.CommandText = "PRAGMA integrity_check;";
            var integrity = (string?)await intCmd.ExecuteScalarAsync(ct) ?? "";
            results.Add(integrity.Equals("ok", StringComparison.OrdinalIgnoreCase)
                ? Healthy("SQLite integrity check passed")
                : Critical($"SQLite integrity check failed: {integrity}", canFix: false));

            // 2. FTS5 virtual table exists
            await using var ftsCmd = conn.CreateCommand();
            ftsCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='memories_fts';";
            var ftsCount = Convert.ToInt64(await ftsCmd.ExecuteScalarAsync(ct) ?? 0L);
            if (ftsCount == 0)
                results.Add(Critical("FTS5 index (memories_fts) missing", canFix: true, "Rebuild FTS5 index"));
            else
                results.Add(Healthy("FTS5 index (memories_fts) present"));

            // 3. Orphaned vectors
            await using var orphanCmd = conn.CreateCommand();
            orphanCmd.CommandText = @"
                SELECT COUNT(*) FROM memory_vectors mv
                WHERE NOT EXISTS (SELECT 1 FROM memories m WHERE m.id = mv.id);";
            var orphans = Convert.ToInt64(await orphanCmd.ExecuteScalarAsync(ct) ?? 0L);
            if (orphans > 0)
                results.Add(Warning($"{orphans} orphaned vector(s) found (vectors with no matching memory entry)",
                    canFix: true, "Delete orphaned vectors"));
            else
                results.Add(Healthy("No orphaned vectors"));
        }
        catch (Exception ex)
        {
            results.Add(Critical($"Cannot open SQLite DB: {ex.Message}"));
        }
    }

    private void CheckMarkdown(List<HealthCheckResult> results)
    {
        var mdPath = _config["Memory:MarkdownPath"] ?? "LongTermMemory.md";
        if (!File.Exists(mdPath))
        {
            results.Add(Warning($"Markdown memory file not yet created: {mdPath}"));
            return;
        }
        results.Add(Healthy($"Markdown memory file exists: {mdPath}"));

        try
        {
            var content = File.ReadAllText(mdPath);
            results.Add(content.Contains("---")
                ? Healthy("Markdown memory file is parseable")
                : Warning("Markdown memory file exists but has no entries (no '---' separator found)"));
        }
        catch (Exception ex)
        {
            results.Add(Critical($"Cannot read markdown memory file: {ex.Message}"));
        }
    }

    public async Task<FixResult> TryFixAsync(HealthCheckResult result, CancellationToken ct = default)
    {
        if (result.FixDescription == "Rebuild FTS5 index")
            return await RebuildFtsAsync(ct);
        if (result.FixDescription == "Delete orphaned vectors")
            return await DeleteOrphanedVectorsAsync(ct);
        return new FixResult(false, "No fix available for this issue");
    }

    private async Task<FixResult> RebuildFtsAsync(CancellationToken ct)
    {
        try
        {
            var cs = $"Data Source={_resolvedDbPath};Mode=ReadWrite;Cache=Shared";
            await using var conn = new SqliteConnection(cs);
            conn.Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO memories_fts(memories_fts) VALUES('rebuild');";
            await cmd.ExecuteNonQueryAsync(ct);
            return new FixResult(true, "FTS5 index rebuilt successfully");
        }
        catch (Exception ex)
        {
            return new FixResult(false, $"FTS5 rebuild failed: {ex.Message}");
        }
    }

    private async Task<FixResult> DeleteOrphanedVectorsAsync(CancellationToken ct)
    {
        if (!DoctorUI.ConfirmDestructive(
            "Delete orphaned vectors",
            "This will permanently remove vector embeddings that have no matching memory entry."))
            return new FixResult(false, "Cancelled by user");

        try
        {
            var cs = $"Data Source={_resolvedDbPath};Mode=ReadWrite;Cache=Shared";
            await using var conn = new SqliteConnection(cs);
            conn.Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM memory_vectors
                WHERE NOT EXISTS (SELECT 1 FROM memories m WHERE m.id = memory_vectors.id);";
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            return new FixResult(true, $"Deleted {deleted} orphaned vector(s)");
        }
        catch (Exception ex)
        {
            return new FixResult(false, $"Delete failed: {ex.Message}");
        }
    }

    private static HealthCheckResult Healthy(string msg) =>
        new(HealthStatus.Healthy, "Long-Term Memory", msg);
    private static HealthCheckResult Warning(string msg, bool canFix = false, string? fixDesc = null) =>
        new(HealthStatus.Warning, "Long-Term Memory", msg, canFix, fixDesc);
    private static HealthCheckResult Critical(string msg, bool canFix = false, string? fixDesc = null) =>
        new(HealthStatus.Critical, "Long-Term Memory", msg, canFix, fixDesc);
}
