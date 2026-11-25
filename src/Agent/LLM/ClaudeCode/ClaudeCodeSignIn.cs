using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentFox.LLM.ClaudeCode;

[JsonConverter(typeof(JsonStringEnumConverter<ClaudeCodeSignInState>))]
public enum ClaudeCodeSignInState { Idle, Starting, AwaitingCode, Exchanging, Completed, Failed, Expired, Cancelled }

/// <param name="Link">The claude.ai link to open; only while waiting for the code.</param>
public sealed record ClaudeCodeSignInSnapshot(ClaudeCodeSignInState State, string? Link, string? Error, DateTimeOffset? ExpiresUtc);

/// <summary>
/// "Sign in with Claude": runs the official <c>claude setup-token</c> on this machine and relays its link and code
/// prompt to the browser. The owner approves on claude.ai; the CLI does the OAuth exchange; nothing here is a sign-in
/// page of our own. Measured 2026-11-25 on CLI 2.1.258:
/// <list type="bullet">
/// <item><c>setup-token</c> prints nothing without a terminal, so it runs under <c>script</c> (Linux only — Windows
/// would need ConPTY; a Windows owner pastes a token instead).</item>
/// <item>The code and Enter must be SEPARATE writes: an Enter in the same write is swallowed with the paste.</item>
/// <item>The CLI's own "retry" restarts with a NEW link, so on any error this attempt ends and the owner starts
/// again rather than pasting a code that can no longer work.</item>
/// </list>
/// One attempt at a time, memory only, ended by its deadline (§0.1). The terminal output holds the token and is
/// never logged or returned; it is dropped when the attempt ends.
/// </summary>
public sealed partial class ClaudeCodeSignIn : IDisposable
{
    private const int MaxOutputChars = 128 * 1024;

    private readonly ClaudeCodeOptions _options;
    private readonly ClaudeCodePaths _paths;
    private readonly ClaudeCodeStore _store;
    private readonly ClaudeCodeActivity _activity;
    private readonly ILogger<ClaudeCodeSignIn> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Attempt? _attempt;
    private ClaudeCodeSignInSnapshot _last = new(ClaudeCodeSignInState.Idle, null, null, null);

    public ClaudeCodeSignIn(ClaudeCodeOptions options, ClaudeCodePaths paths, ClaudeCodeStore store,
        ClaudeCodeActivity activity, ILogger<ClaudeCodeSignIn> logger)
    {
        _options = options;
        _paths = paths;
        _store = store;
        _activity = activity;
        _logger = logger;
    }

    /// <summary>The relay needs <c>script</c> (util-linux); on Windows the owner pastes a token.</summary>
    public static bool IsSupported => !OperatingSystem.IsWindows() && ScriptPath() is not null;

    public ClaudeCodeSignInSnapshot Current
    {
        get
        {
            var a = _attempt;
            if (a is not null && a.Deadline <= DateTimeOffset.UtcNow && !a.Finished)
                End(a, ClaudeCodeSignInState.Expired, "The sign-in took too long. Start again.");
            return _last;
        }
    }

    public async Task<ClaudeCodeSignInSnapshot> StartAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported)
            return _last = new(ClaudeCodeSignInState.Failed, null, "Sign-in from here is not available on this machine. Paste a token instead.", null);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_attempt is { Finished: false } old) End(old, ClaudeCodeSignInState.Cancelled, null);
            _paths.Ensure();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, _options.SignInTimeoutSeconds));

            var psi = new ProcessStartInfo(ScriptPath()!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _paths.Work,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            // A wide terminal so the token is less likely to wrap (the parser copes either way). The executable path
            // is configuration, quoted for the shell; nothing from a request reaches this command line.
            psi.ArgumentList.Add("-qfc");
            psi.ArgumentList.Add($"stty cols 1000 rows 50 2>/dev/null; exec {ShellQuote(_options.ExecutablePath)} setup-token");
            psi.ArgumentList.Add("/dev/null");
            psi.Environment.Clear();
            foreach (var (k, v) in ClaudeCodeCommand.Environment(Environment.GetEnvironmentVariables(), token: null, _paths))
                psi.Environment[k] = v;
            psi.Environment["TERM"] = "xterm";

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                process.Dispose();
                return _last = new(ClaudeCodeSignInState.Failed, null, "The sign-in could not be started on this machine.", null);
            }

            var attempt = new Attempt(process, deadline);
            _attempt = attempt;
            _last = new(ClaudeCodeSignInState.Starting, null, null, deadline);
            attempt.Reader = Task.Run(() => ReadAsync(attempt));
            attempt.Timer = new Timer(_ => { if (!attempt.Finished) End(attempt, ClaudeCodeSignInState.Expired, "The sign-in took too long. Start again."); },
                null, deadline - DateTimeOffset.UtcNow, Timeout.InfiniteTimeSpan);

            // Wait for the link (the CLI prints it within a few seconds).
            for (var i = 0; i < 300 && !attempt.Finished; i++)
            {
                var link = attempt.Link;
                if (link is not null)
                    return _last = new(ClaudeCodeSignInState.AwaitingCode, link, null, deadline);
                await Task.Delay(100, cancellationToken);
            }
            if (!attempt.Finished)
                End(attempt, ClaudeCodeSignInState.Failed, "Claude did not offer a sign-in link. Check that this machine can reach claude.com, then start again.");
            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ClaudeCodeSignInSnapshot> SubmitCodeAsync(string code, CancellationToken cancellationToken)
    {
        code = (code ?? "").Trim();
        if (!CodeShape().IsMatch(code))
            return _last with { Error = "That does not look like the code Claude showed. Copy it again." };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var a = _attempt;
            if (a is null || a.Finished || _last.State != ClaudeCodeSignInState.AwaitingCode)
                return _last = new(ClaudeCodeSignInState.Failed, null, "This sign-in is no longer waiting for a code. Start again.", null);

            _last = new(ClaudeCodeSignInState.Exchanging, null, null, a.Deadline);
            try
            {
                await a.Process.StandardInput.WriteAsync(code.AsMemory(), cancellationToken);
                await a.Process.StandardInput.FlushAsync(cancellationToken);
                await Task.Delay(1000, cancellationToken);
                await a.Process.StandardInput.WriteAsync("\r".AsMemory(), cancellationToken);
                await a.Process.StandardInput.FlushAsync(cancellationToken);
            }
            catch (IOException)
            {
                End(a, ClaudeCodeSignInState.Failed, "The sign-in stopped before the code could be used. Start again.");
                return _last;
            }

            for (var i = 0; i < 900 && !a.Finished; i++) // up to 90 s for the exchange
            {
                var (token, error) = a.Outcome();
                if (token is not null)
                {
                    _store.SaveToken(token, "signin");
                    _activity.ClearErrors();
                    End(a, ClaudeCodeSignInState.Completed, null);
                    _logger.LogInformation("[ClaudeCode] Signed in with Claude; a new token was saved.");
                    return _last;
                }
                if (error is not null)
                {
                    End(a, ClaudeCodeSignInState.Failed, $"Claude refused or could not finish the sign-in ({error}). Start again for a new link.");
                    return _last;
                }
                await Task.Delay(100, cancellationToken);
            }
            if (!a.Finished)
                End(a, ClaudeCodeSignInState.Failed, "Claude did not finish the sign-in. Start again for a new link.");
            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ClaudeCodeSignInSnapshot Cancel()
    {
        if (_attempt is { Finished: false } a) End(a, ClaudeCodeSignInState.Cancelled, null);
        return _last;
    }

    private void End(Attempt a, ClaudeCodeSignInState state, string? error)
    {
        lock (a)
        {
            if (a.Finished) return;
            a.Finished = true;
        }
        try { if (!a.Process.HasExited) a.Process.Kill(entireProcessTree: true); } catch { /* exiting */ }
        a.Timer?.Dispose();
        a.ClearOutput();
        _last = new(state, null, error, null);
    }

    private async Task ReadAsync(Attempt a)
    {
        var buffer = new char[2048];
        try
        {
            int n;
            while ((n = await a.Process.StandardOutput.ReadAsync(buffer)) > 0)
                a.Append(buffer, n);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        if (!a.Finished)
        {
            // The CLI exited on its own. A printed token is still taken by SubmitCodeAsync's loop on its next look.
            await Task.Delay(1500);
            if (!a.Finished && a.Outcome().Token is null && _last.State != ClaudeCodeSignInState.Exchanging)
                End(a, ClaudeCodeSignInState.Failed, "The sign-in ended unexpectedly. Start again.");
        }
    }

    private static string? ScriptPath() =>
        new[] { "/usr/bin/script", "/bin/script" }.FirstOrDefault(File.Exists);

    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public void Dispose()
    {
        if (_attempt is { Finished: false } a) End(a, ClaudeCodeSignInState.Cancelled, null);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-#]{10,512}$")] private static partial Regex CodeShape();

    private sealed class Attempt(Process process, DateTimeOffset deadline)
    {
        private readonly StringBuilder _raw = new();
        public Process Process { get; } = process;
        public DateTimeOffset Deadline { get; } = deadline;
        public bool Finished { get; set; }
        public Task? Reader { get; set; }
        public Timer? Timer { get; set; }

        public void Append(char[] buffer, int count)
        {
            lock (_raw)
            {
                if (_raw.Length + count <= MaxOutputChars) _raw.Append(buffer, 0, count);
            }
        }

        public string? Link
        {
            get
            {
                lock (_raw)
                {
                    var raw = _raw.ToString();
                    return ClaudeCodeTerminalText.IsAwaitingCode(ClaudeCodeTerminalText.Strip(raw))
                        ? ClaudeCodeTerminalText.FindSignInLinkInRaw(raw) : null;
                }
            }
        }

        public (string? Token, string? Error) Outcome()
        {
            lock (_raw)
            {
                var stripped = ClaudeCodeTerminalText.Strip(_raw.ToString());
                return (ClaudeCodeTerminalText.FindToken(stripped), ClaudeCodeTerminalText.FindError(stripped));
            }
        }

        public void ClearOutput()
        {
            lock (_raw) _raw.Clear();
            try { Process.Dispose(); } catch { /* already disposed */ }
        }
    }
}
