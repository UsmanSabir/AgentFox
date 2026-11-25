using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace AgentFox.LLM.ClaudeCode;

/// <param name="Environment">The child's WHOLE environment (<see cref="ClaudeCodeCommand.Environment"/>); nothing
/// else is inherited.</param>
public sealed record ClaudeCodeInvocation(
    string Executable, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory, string? StandardInput, TimeSpan Timeout);

/// <summary>The CLI exited unsuccessfully, or could not be started.</summary>
public sealed class ClaudeCodeProcessException(string message, int? exitCode = null, string? stderrTail = null)
    : Exception(message)
{
    public int? ExitCode { get; } = exitCode;
    /// <summary>The end of stderr, with anything shaped like a token masked.</summary>
    public string? StderrTail { get; } = stderrTail;
    public bool NotInstalled { get; init; }
    public bool TimedOut { get; init; }
}

/// <summary>Starts the CLI and yields its stdout lines. A seam, so the chat client is tested without a process.</summary>
public interface IClaudeCodeProcessRunner
{
    /// <summary>Yields each stdout line; throws <see cref="ClaudeCodeProcessException"/> after the last line when the
    /// exit code is not zero.</summary>
    IAsyncEnumerable<string> RunAsync(ClaudeCodeInvocation invocation, CancellationToken cancellationToken);
}

public sealed class ClaudeCodeProcessRunner : IClaudeCodeProcessRunner
{
    /// <summary>Output beyond this ends the call: a reply is never this large, a runaway process might be.</summary>
    private const long MaxOutputChars = 16 * 1024 * 1024;
    private const int StderrTailChars = 4096;

    public async IAsyncEnumerable<string> RunAsync(ClaudeCodeInvocation invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(invocation.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = invocation.WorkingDirectory,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in invocation.Arguments) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in invocation.Environment) psi.Environment[k] = v;

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                throw new ClaudeCodeProcessException("The Claude Code CLI could not be started.") { NotInstalled = true };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ClaudeCodeProcessException(
                $"The Claude Code CLI was not found ({Path.GetFileName(invocation.Executable)}).") { NotInstalled = true };
        }

        using var timeout = new CancellationTokenSource(invocation.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked.Token;
        var stderr = new StringBuilder();
        var stderrTask = ReadTailAsync(process.StandardError, stderr);

        try
        {
            // The prompt goes on stdin: never in the process list, no escaping, no command-line length limit.
            await process.StandardInput.WriteAsync((invocation.StandardInput ?? "").AsMemory(), token);
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            Kill(process);
            if (token.IsCancellationRequested) throw Cancelled(cancellationToken, timeout);
            throw new ClaudeCodeProcessException("The Claude Code CLI stopped before reading the request.");
        }

        long total = 0;
        while (true)
        {
            string? line;
            try
            {
                line = await process.StandardOutput.ReadLineAsync(token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw Cancelled(cancellationToken, timeout);
            }
            if (line is null) break;
            total += line.Length;
            if (total > MaxOutputChars)
            {
                Kill(process);
                throw new ClaudeCodeProcessException("The Claude Code CLI produced more output than expected.");
            }
            yield return line;
        }

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw Cancelled(cancellationToken, timeout);
        }
        await stderrTask;
        if (process.ExitCode != 0)
        {
            string tail;
            lock (stderr) tail = ClaudeCodeTerminalText.Redact(stderr.ToString()).Trim();
            throw new ClaudeCodeProcessException($"The Claude Code CLI exited with code {process.ExitCode}.",
                process.ExitCode, tail.Length == 0 ? null : tail);
        }
    }

    private static Exception Cancelled(CancellationToken caller, CancellationTokenSource timeout)
    {
        if (caller.IsCancellationRequested) return new OperationCanceledException(caller);
        if (timeout.IsCancellationRequested)
            return new ClaudeCodeProcessException("The Claude Code call took too long and was stopped.") { TimedOut = true };
        return new OperationCanceledException();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already gone */ }
        catch (System.ComponentModel.Win32Exception) { /* exiting */ }
    }

    private static async Task ReadTailAsync(StreamReader reader, StringBuilder tail)
    {
        var buffer = new char[1024];
        int n;
        try
        {
            while ((n = await reader.ReadAsync(buffer)) > 0)
                lock (tail)
                {
                    tail.Append(buffer, 0, n);
                    if (tail.Length > StderrTailChars) tail.Remove(0, tail.Length - StderrTailChars);
                }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
}
