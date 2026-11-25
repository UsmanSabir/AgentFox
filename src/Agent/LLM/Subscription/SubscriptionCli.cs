using System.Collections;
using System.Text.RegularExpressions;

namespace AgentFox.LLM.Subscription;

/// <summary>Why a subscription CLI call (Claude Code, Codex) did not answer.</summary>
public enum SubscriptionFailure
{
    NotSignedIn, NotInstalled, NotAllowed, ReauthenticationRequired, RateLimited, Busy, TimedOut, Failed,
}

/// <summary>A subscription CLI call failed. The message is written for the owner and never holds a credential.
/// Only <see cref="SubscriptionFailure.Busy"/> is worth asking again: a refused sign-in or an exhausted subscription
/// is not, and a call whose outcome is unknown must not be repeated.</summary>
public sealed class SubscriptionCliException(SubscriptionFailure kind, string message)
    : AiProviderException(message, retryable: kind is SubscriptionFailure.Busy)
{
    public SubscriptionFailure Kind { get; } = kind;
}

/// <summary>
/// What a subscription CLI child process may inherit. An allow-list, not a strip-list: both CLIs put an API key
/// (ANTHROPIC_API_KEY / OPENAI_API_KEY and friends) ABOVE the subscription, so inheriting anything not named here
/// could switch a call to paid API billing.
/// </summary>
public static class CliEnvironment
{
    private static readonly string[] Inherited =
    [
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec", "OS", "PROCESSOR_ARCHITECTURE",
        "NUMBER_OF_PROCESSORS", "TEMP", "TMP", "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE", "TZ",
        "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY", "https_proxy", "http_proxy", "no_proxy",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "NODE_EXTRA_CA_CERTS",
    ];

    /// <summary>Unix-only names. On Windows they are skipped: a shell like Git Bash sets <c>TMPDIR=/c/Users/...</c>,
    /// a path Windows programs cannot use, and Codex then fails every call ("workspace routing discovery failed",
    /// measured 2026-11-25). Windows programs read <c>TEMP</c>/<c>TMP</c>.</summary>
    private static readonly string[] UnixOnly = ["TMPDIR", "TERM"];

    /// <summary>The allow-listed part of <paramref name="parent"/>, with a private home folder: with the owner's own
    /// home, a CLI loads their own skills, plugins and memory into every call (measured on Claude Code).</summary>
    public static Dictionary<string, string> Base(IDictionary parent, string home)
    {
        // Looked up WITHOUT regard to case: Windows names are case-insensitive but the dictionary is not, and the
        // spelling depends on who started the agent (PowerShell has "Path"; Git Bash exports "SYSTEMROOT"). Missing
        // SystemRoot breaks a child's networking on Windows: Codex failed every call ("workspace routing discovery
        // failed", measured 2026-11-25).
        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in parent)
            if (entry.Key is string key && entry.Value is string value && value.Length > 0)
                source[key] = value;
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in OperatingSystem.IsWindows() ? Inherited : [.. Inherited, .. UnixOnly])
            if (source.TryGetValue(name, out var value))
                env[name] = value;
        env["HOME"] = home;
        env["USERPROFILE"] = home;
        env["APPDATA"] = Path.Combine(home, "AppData", "Roaming");
        env["LOCALAPPDATA"] = Path.Combine(home, "AppData", "Local");
        env["XDG_CONFIG_HOME"] = Path.Combine(home, ".config");
        env["XDG_CACHE_HOME"] = Path.Combine(home, ".cache");
        env["BROWSER"] = "none"; // a sign-in relays its link; nothing opens on the server
        return env;
    }
}

/// <summary>Masks anything shaped like a credential before text reaches a log or the UI.</summary>
public static partial class SecretRedaction
{
    public static string Redact(string text)
    {
        var s = AnthropicToken().Replace(text, "sk-ant-****");
        s = OpenAiKey().Replace(s, "sk-****");
        s = Jwt().Replace(s, "eyJ****");
        return s;
    }

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_-]+")] private static partial Regex AnthropicToken();
    [GeneratedRegex(@"\bsk-(?:proj-)?[A-Za-z0-9_-]{20,}")] private static partial Regex OpenAiKey();
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]+")] private static partial Regex Jwt();
}

/// <summary>
/// What a provider's settings card shows about recent use: last success, last error, last test. Memory only, a fixed
/// handful of values (§0.1: nothing grows; a restart forgets it). One subclass per provider so each has its own.
/// </summary>
public abstract class SubscriptionActivity
{
    protected readonly object Gate = new();
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    public DateTimeOffset? LastSuccessUtc { get; private set; }
    public string? LastModel { get; private set; }
    public DateTimeOffset? LastErrorUtc { get; private set; }
    public string? LastError { get; private set; }
    public SubscriptionFailure? LastErrorKind { get; private set; }
    public DateTimeOffset? LastTestUtc { get; private set; }
    public bool? LastTestPassed { get; private set; }

    public void RecordSuccess(string model)
    {
        lock (Gate) { LastSuccessUtc = DateTimeOffset.UtcNow; LastModel = model; }
    }

    public void RecordFailure(SubscriptionFailure kind, string message)
    {
        lock (Gate) { LastErrorUtc = DateTimeOffset.UtcNow; LastError = message; LastErrorKind = kind; }
    }

    public void RecordTest(bool passed)
    {
        lock (Gate) { LastTestUtc = DateTimeOffset.UtcNow; LastTestPassed = passed; }
    }

    /// <summary>The sign-in was replaced or removed: errors about the old one no longer apply.</summary>
    public void ClearErrors()
    {
        lock (Gate) { LastError = null; LastErrorKind = null; LastErrorUtc = null; }
    }

    /// <summary>At most hourly, from a call that is starting anyway (no worker of its own).</summary>
    public void SweepIfDue(IEnumerable<string> directories)
    {
        lock (Gate)
        {
            if (DateTimeOffset.UtcNow - _lastSweep < TimeSpan.FromHours(1)) return;
            _lastSweep = DateTimeOffset.UtcNow;
        }
        SubscriptionHousekeeping.Sweep(directories, DateTimeOffset.UtcNow);
    }
}

/// <summary>Retention for a CLI's leftover files (§0.1): kept 1 day, then deleted. Each provider names its own
/// folders; a CLI's own small config and credential files are never in them.</summary>
public static class SubscriptionHousekeeping
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(1);

    public static int Sweep(IEnumerable<string> directories, DateTimeOffset now)
    {
        var removed = 0;
        foreach (var dir in directories)
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
