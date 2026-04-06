namespace AgentFox.Doctor.Checks;

using AgentFox.Doctor;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Health check for the session management subsystem.
///
/// Verifies that the session and archive directories exist, are writable,
/// and that there are no orphaned .md files (session files with no accompanying
/// index.json in the same folder).
/// </summary>
public class SessionHealthCheck : IHealthCheckable
{
    private const string ComponentLabel = "Session Storage";

    // Tag strings embedded in result.Message to identify fixable results in TryFixAsync
    private const string TagMissingSessionDir  = "[fix:create-session-dir]";
    private const string TagMissingArchiveDir  = "[fix:create-archive-dir]";
    private const string TagOrphanedSessions   = "[fix:orphan-cleanup]";

    private readonly string _sessionDir;
    private readonly string _archiveDir;

    public string ComponentName => "Session Storage";

    public SessionHealthCheck(IConfiguration config, string workspacePath)
    {
        var sessionRel = config["Sessions:SessionDirectory"] ?? "sessions";
        var archiveRel = config["Sessions:ArchiveDirectory"] ?? "archive/sessions";

        _sessionDir = ResolvePath(workspacePath, sessionRel);
        _archiveDir = ResolvePath(workspacePath, archiveRel);
    }

    // ── IHealthCheckable ────────────────────────────────────────────────────────

    public Task<IReadOnlyList<HealthCheckResult>> CheckHealthAsync(CancellationToken ct = default)
    {
        var results = new List<HealthCheckResult>();

        CheckDirectory(_sessionDir, "Session directory", TagMissingSessionDir, results);
        CheckDirectory(_archiveDir, "Archive directory", TagMissingArchiveDir, results);

        // Write permission probes (only when directories exist)
        if (Directory.Exists(_sessionDir))
            CheckWritePermission(_sessionDir, "Session directory", results);

        if (Directory.Exists(_archiveDir))
            CheckWritePermission(_archiveDir, "Archive directory", results);

        // Orphaned session check
        CheckOrphanedSessions(results);

        return Task.FromResult<IReadOnlyList<HealthCheckResult>>(results);
    }

    public Task<FixResult> TryFixAsync(HealthCheckResult result, CancellationToken ct = default)
    {
        // Create missing session directory (non-destructive)
        if (result.Message.Contains(TagMissingSessionDir))
            return Task.FromResult(TryCreateDirectory(_sessionDir));

        // Create missing archive directory (non-destructive)
        if (result.Message.Contains(TagMissingArchiveDir))
            return Task.FromResult(TryCreateDirectory(_archiveDir));

        // Orphan cleanup — requires explicit destructive confirmation
        if (result.Message.Contains(TagOrphanedSessions))
            return Task.FromResult(TryCleanOrphans());

        return Task.FromResult(new FixResult(false, "No matching fix available"));
    }

    // ── Checks ──────────────────────────────────────────────────────────────────

    private void CheckDirectory(
        string path, string label, string tag, List<HealthCheckResult> results)
    {
        if (Directory.Exists(path))
        {
            results.Add(Healthy($"{label} exists: {path}"));
        }
        else
        {
            results.Add(new HealthCheckResult(
                HealthStatus.Critical,
                ComponentLabel,
                $"{label} missing: {path} {tag}",
                CanAutoFix: true,
                FixDescription: $"Create missing directory: {path}"));
        }
    }

    private void CheckWritePermission(string path, string label, List<HealthCheckResult> results)
    {
        var probe = Path.Combine(path, $".doctor_write_probe_{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            results.Add(Healthy($"{label} is writable"));
        }
        catch (Exception ex)
        {
            results.Add(new HealthCheckResult(
                HealthStatus.Critical,
                ComponentLabel,
                $"{label} is not writable ({path}): {ex.Message}",
                CanAutoFix: false));
        }
    }

    /// <summary>
    /// An orphaned session is a .md file whose containing folder has no index.json.
    ///
    /// Session file layout (from SessionManager):
    ///   sessions/
    ///     index.json                     ← master index for all top-level sessions
    ///     console.md
    ///     ch_whatsapp_12345.md
    ///     agentfox/                      ← sub-agent subdirectory
    ///       index.json                   ← (may or may not exist for sub-dirs)
    ///       sa_abc123.md
    ///
    /// The root sessions/ folder always has index.json when the manager is running.
    /// Sub-agent subdirectories may not have one. We flag .md files in any folder
    /// (including the root) that lacks an index.json as orphans.
    /// </summary>
    private void CheckOrphanedSessions(List<HealthCheckResult> results)
    {
        if (!Directory.Exists(_sessionDir))
            return;

        try
        {
            var orphanedFiles = FindOrphanedSessionFiles(_sessionDir);

            if (orphanedFiles.Count == 0)
            {
                results.Add(Healthy("No orphaned session files found"));
            }
            else
            {
                results.Add(new HealthCheckResult(
                    HealthStatus.Warning,
                    ComponentLabel,
                    $"{orphanedFiles.Count} orphaned session file(s) found (no index.json in same folder) {TagOrphanedSessions}",
                    CanAutoFix: true,
                    FixDescription: $"Delete {orphanedFiles.Count} orphaned .md file(s)"));
            }
        }
        catch (Exception ex)
        {
            results.Add(Warning($"Could not scan for orphaned sessions: {ex.Message}"));
        }
    }

    // ── Fixes ───────────────────────────────────────────────────────────────────

    private static FixResult TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return new FixResult(true, $"Created directory: {path}");
        }
        catch (Exception ex)
        {
            return new FixResult(false, $"Could not create directory {path}: {ex.Message}");
        }
    }

    private FixResult TryCleanOrphans()
    {
        if (!Directory.Exists(_sessionDir))
            return new FixResult(false, "Session directory does not exist");

        List<string> orphans;
        try
        {
            orphans = FindOrphanedSessionFiles(_sessionDir);
        }
        catch (Exception ex)
        {
            return new FixResult(false, $"Could not enumerate orphaned files: {ex.Message}");
        }

        if (orphans.Count == 0)
            return new FixResult(true, "No orphaned session files to remove");

        var confirmed = DoctorUI.ConfirmDestructive(
            action:      $"Delete {orphans.Count} orphaned session file(s)",
            consequence: "These .md files have no associated index.json and may contain unrecovered conversation history.");

        if (!confirmed)
            return new FixResult(false, "Orphan cleanup cancelled by user");

        int deleted = 0;
        var errors  = new List<string>();
        foreach (var file in orphans)
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (errors.Count == 0)
            return new FixResult(true, $"Deleted {deleted} orphaned session file(s)");

        return new FixResult(
            deleted > 0,
            $"Deleted {deleted}/{orphans.Count} file(s). Failures: {string.Join("; ", errors)}");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all .md files (recursively) whose containing directory has no index.json.
    /// </summary>
    private static List<string> FindOrphanedSessionFiles(string sessionDir)
    {
        var orphans = new List<string>();

        foreach (var mdFile in Directory.EnumerateFiles(sessionDir, "*.md", SearchOption.AllDirectories))
        {
            var folder = Path.GetDirectoryName(mdFile)!;
            if (!File.Exists(Path.Combine(folder, "index.json")))
                orphans.Add(mdFile);
        }

        return orphans;
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> against <paramref name="workspacePath"/>
    /// when it is not already an absolute path.
    /// </summary>
    private static string ResolvePath(string workspacePath, string relativePath)
        => Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.GetFullPath(Path.Combine(workspacePath, relativePath));

    // ── Result factories ─────────────────────────────────────────────────────────

    private static HealthCheckResult Healthy(string msg)
        => new(HealthStatus.Healthy, ComponentLabel, msg);

    private static HealthCheckResult Warning(string msg)
        => new(HealthStatus.Warning, ComponentLabel, msg);
}
