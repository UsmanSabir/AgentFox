using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentFox.LLM.Subscription;
using Microsoft.Extensions.Logging;

namespace AgentFox.LLM.Codex;

[JsonConverter(typeof(JsonStringEnumConverter<CodexSignInState>))]
public enum CodexSignInState { Idle, Starting, AwaitingApproval, Completed, Failed, Expired, Cancelled }

/// <param name="Link">Where the owner enters <paramref name="Code"/>; only while waiting for approval.</param>
/// <param name="Code">OpenAI's one-time device code. It signs nothing in by itself: it only lets the owner approve this
/// waiting sign-in in their own ChatGPT session.</param>
public sealed record CodexSignInSnapshot(CodexSignInState State, string? Link, string? Code, string? Error, DateTimeOffset? ExpiresUtc);

/// <summary>
/// "Sign in with ChatGPT" for Codex: runs the official <c>codex login --device-auth</c> and relays its link and
/// one-time code. MEASURED 2026-11-25 (CLI 0.172.0) on Windows and Linux: it needs no terminal, prints
/// <c>https://auth.openai.com/codex/device</c> and the code, and exits 0 with "Successfully logged in" once the owner
/// approves. Device sign-in has to be enabled in the owner's ChatGPT security settings first; otherwise the attempt
/// just waits out its 15 minutes. The CLI writes and later refreshes its own <c>auth.json</c>; nothing here reads it.
/// One attempt at a time, memory only, ended by its deadline (§0.1).
/// </summary>
public sealed partial class CodexSignIn : IDisposable
{
    private const int MaxOutputChars = 64 * 1024;

    private readonly CodexOptions _options;
    private readonly CodexPaths _paths;
    private readonly CodexActivity _activity;
    private readonly ILogger<CodexSignIn> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Attempt? _attempt;
    private CodexSignInSnapshot _last = new(CodexSignInState.Idle, null, null, null, null);

    public CodexSignIn(CodexOptions options, CodexPaths paths, CodexActivity activity, ILogger<CodexSignIn> logger)
    {
        _options = options;
        _paths = paths;
        _activity = activity;
        _logger = logger;
    }

    public CodexSignInSnapshot Current => _last;

    public async Task<CodexSignInSnapshot> StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_attempt is { Finished: false } old) End(old, CodexSignInState.Cancelled, null);
            _paths.Ensure();
            var psi = new ProcessStartInfo(_options.ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _paths.Work,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            psi.ArgumentList.Add("login");
            psi.ArgumentList.Add("--device-auth");
            psi.Environment.Clear();
            foreach (var (k, v) in CodexCommand.Environment(Environment.GetEnvironmentVariables(), _paths))
                psi.Environment[k] = v;

            var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
                process.StandardInput.Close();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                process.Dispose();
                return _last = new(CodexSignInState.Failed, null, null, "The Codex CLI is not installed where this agent runs.", null);
            }

            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, _options.SignInTimeoutSeconds));
            var attempt = new Attempt(process, deadline);
            _attempt = attempt;
            _last = new(CodexSignInState.Starting, null, null, null, deadline);
            _ = Task.Run(() => PumpAsync(attempt, process.StandardOutput));
            _ = Task.Run(() => PumpAsync(attempt, process.StandardError));
            _ = Task.Run(() => WatchAsync(attempt));
            attempt.Timer = new Timer(_ => { if (!attempt.Finished) End(attempt, CodexSignInState.Expired, "The code expired before it was approved. Start again."); },
                null, deadline - DateTimeOffset.UtcNow, Timeout.InfiniteTimeSpan);

            for (var i = 0; i < 300 && !attempt.Finished; i++)
            {
                var (link, code) = CodexDeviceText.Parse(attempt.Output());
                if (link is not null && code is not null)
                    return _last = new(CodexSignInState.AwaitingApproval, link, code, null, deadline);
                await Task.Delay(100, cancellationToken);
            }
            if (!attempt.Finished)
                End(attempt, CodexSignInState.Failed, "ChatGPT did not offer a sign-in code. Check that this machine can reach auth.openai.com, then start again.");
            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    public CodexSignInSnapshot Cancel()
    {
        if (_attempt is { Finished: false } a) End(a, CodexSignInState.Cancelled, null);
        return _last;
    }

    private async Task WatchAsync(Attempt a)
    {
        try { await a.Process.WaitForExitAsync(); } catch (InvalidOperationException) { return; }
        await Task.Delay(300); // let the pumps drain the last lines
        if (a.Finished) return;
        var succeeded = a.Process.ExitCode == 0 && (CodexDeviceText.SaysSignedIn(a.Output()) || File.Exists(_paths.AuthFile));
        if (succeeded)
        {
            _activity.ClearErrors();
            _logger.LogInformation("[Codex] Signed in with ChatGPT.");
            End(a, CodexSignInState.Completed, null);
        }
        else
        {
            End(a, CodexSignInState.Failed, "The sign-in was not completed. Make sure device sign-in is enabled in your ChatGPT security settings, then start again.");
        }
    }

    private static async Task PumpAsync(Attempt a, StreamReader reader)
    {
        var buffer = new char[1024];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buffer)) > 0) a.Append(buffer, n);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void End(Attempt a, CodexSignInState state, string? error)
    {
        lock (a)
        {
            if (a.Finished) return;
            a.Finished = true;
        }
        try { if (!a.Process.HasExited) a.Process.Kill(entireProcessTree: true); } catch { /* exiting */ }
        a.Timer?.Dispose();
        a.Clear();
        _last = new(state, null, null, error, null);
    }

    public void Dispose()
    {
        if (_attempt is { Finished: false } a) End(a, CodexSignInState.Cancelled, null);
    }

    private sealed class Attempt(Process process, DateTimeOffset deadline)
    {
        private readonly StringBuilder _out = new();
        public Process Process { get; } = process;
        public DateTimeOffset Deadline { get; } = deadline;
        public bool Finished { get; set; }
        public Timer? Timer { get; set; }

        public void Append(char[] buffer, int count)
        {
            lock (_out) if (_out.Length + count <= MaxOutputChars) _out.Append(buffer, 0, count);
        }

        public string Output()
        {
            lock (_out) return _out.ToString();
        }

        public void Clear()
        {
            lock (_out) _out.Clear();
            try { Process.Dispose(); } catch { /* already disposed */ }
        }
    }
}

/// <summary>Reads what <c>codex login --device-auth</c> prints. Pure.</summary>
public static partial class CodexDeviceText
{
    public static (string? Link, string? Code) Parse(string raw)
    {
        var text = Ansi().Replace(raw, "");
        var link = Link().Match(text);
        var code = Code().Match(text);
        return (link.Success ? link.Value : null, code.Success ? code.Groups[1].Value : null);
    }

    public static bool SaysSignedIn(string raw) => Ansi().Replace(raw, "").Contains("Successfully logged in", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\x1b\[[0-9;?]*[A-Za-z]")] private static partial Regex Ansi();
    [GeneratedRegex(@"https://auth\.openai\.com/codex/device\S*")] private static partial Regex Link();
    // The code is printed on its own line after "Enter this one-time code".
    [GeneratedRegex(@"one-time code[^\n]*\n\s*([A-Z0-9]{4,5}-[A-Z0-9]{4,6})\b", RegexOptions.IgnoreCase)] private static partial Regex Code();
}
