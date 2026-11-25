using System.Collections.Concurrent;

namespace AgentFox.LLM.Subscription;

/// <summary>
/// Whether a CLI's program is present where this agent runs, without starting it. The router asks on every routed
/// call, so a sign-in left on the volume by an image that no longer carries the CLI (a hosted release built without
/// it) sends the purpose to the configured AI instead of failing every call with "not installed".
/// </summary>
/// <remarks>
/// Follows how <see cref="System.Diagnostics.Process"/> finds a program with UseShellExecute off: a path is checked
/// as given; a bare name is looked for in the app's folder, the current folder and each PATH folder, and on Windows
/// with ".exe" added when it has no extension (a .cmd shim is never started that way, so it does not count).
/// An answer is kept for <see cref="CacheFor"/>: an install or removal is noticed within that, and a busy agent
/// does not walk PATH per call. Bounded by the number of configured executables (two).
/// </remarks>
public static class CliExecutable
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, (bool Installed, DateTimeOffset At)> Answers = new(StringComparer.Ordinal);

    public static bool IsInstalled(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        var now = DateTimeOffset.UtcNow;
        if (Answers.TryGetValue(executable, out var known) && now - known.At < CacheFor) return known.Installed;
        var installed = Find(executable, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows()) is not null;
        if (Answers.Count > 32) Answers.Clear(); // a backstop only; there are two executables
        Answers[executable] = (installed, now);
        return installed;
    }

    /// <summary>The file the executable name resolves to, or null. Pure apart from the file-existence checks.</summary>
    public static string? Find(string executable, string? path, bool windows)
    {
        var candidates = new List<string> { executable };
        if (windows && !Path.HasExtension(executable)) candidates.Add(executable + ".exe");

        if (Path.IsPathRooted(executable) || executable.Contains('/') || (windows && executable.Contains('\\')))
            return candidates.FirstOrDefault(File.Exists);

        var folders = new List<string> { AppContext.BaseDirectory, Environment.CurrentDirectory };
        if (!string.IsNullOrEmpty(path))
            folders.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var folder in folders)
            foreach (var name in candidates)
            {
                string full;
                try { full = Path.Combine(folder.Trim('"'), name); }
                catch (ArgumentException) { continue; }
                if (File.Exists(full)) return full;
            }
        return null;
    }
}
