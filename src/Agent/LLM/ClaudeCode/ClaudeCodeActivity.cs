namespace AgentFox.LLM.ClaudeCode;

/// <summary>
/// What the settings card shows about recent use: last success, last error, last test, the subscription's usage.
/// Memory only, a fixed handful of values (§0.1: nothing grows; a restart forgets it).
/// </summary>
public sealed class ClaudeCodeActivity
{
    private readonly object _gate = new();
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    public DateTimeOffset? LastSuccessUtc { get; private set; }
    public string? LastModel { get; private set; }
    public DateTimeOffset? LastErrorUtc { get; private set; }
    public string? LastError { get; private set; }
    public ClaudeCodeFailure? LastErrorKind { get; private set; }
    public ClaudeCodeUsageWindows? Usage { get; private set; }
    public DateTimeOffset? UsageReadUtc { get; private set; }
    public DateTimeOffset? LastTestUtc { get; private set; }
    public bool? LastTestPassed { get; private set; }

    public void RecordSuccess(string model)
    {
        lock (_gate) { LastSuccessUtc = DateTimeOffset.UtcNow; LastModel = model; }
    }

    public void RecordFailure(ClaudeCodeFailure kind, string message)
    {
        lock (_gate) { LastErrorUtc = DateTimeOffset.UtcNow; LastError = message; LastErrorKind = kind; }
    }

    public void RecordUsage(ClaudeCodeUsageWindows usage)
    {
        lock (_gate) { Usage = usage; UsageReadUtc = DateTimeOffset.UtcNow; }
    }

    public void RecordTest(bool passed)
    {
        lock (_gate) { LastTestUtc = DateTimeOffset.UtcNow; LastTestPassed = passed; }
    }

    /// <summary>The sign-in was replaced or removed: errors about the old one no longer apply.</summary>
    public void ClearErrors()
    {
        lock (_gate) { LastError = null; LastErrorKind = null; LastErrorUtc = null; }
    }

    /// <summary>At most hourly, from a call that is starting anyway (no worker of its own).</summary>
    public void SweepIfDue(ClaudeCodePaths paths)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _lastSweep < TimeSpan.FromHours(1)) return;
            _lastSweep = DateTimeOffset.UtcNow;
        }
        ClaudeCodeHousekeeping.Sweep(paths, DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// Retention for the CLI's private folders (§0.1). Even with session saving off, the CLI writes a backup of its
/// <c>.claude.json</c> on each run plus <c>projects/</c> and <c>sessions/</c> entries (measured 2026-11-25), and a
/// crashed call can leave its system-prompt file. Kept: 1 day. The CLI's own small config files stay.
/// </summary>
public static class ClaudeCodeHousekeeping
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(1);

    public static int Sweep(ClaudeCodePaths paths, DateTimeOffset now)
    {
        var removed = 0;
        foreach (var dir in new[]
                 {
                     Path.Combine(paths.Config, "backups"), Path.Combine(paths.Config, "projects"),
                     Path.Combine(paths.Config, "sessions"), paths.Work,
                 })
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (now - File.GetLastWriteTimeUtc(file) > Keep) { File.Delete(file); removed++; }
                    }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                {
                    try { if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub); }
                    catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return removed;
    }
}
