namespace AgentFox.Helpers;

/// <summary>
/// A cancellable, time-bounded replacement for <c>Console.ReadLine()</c>.
///
/// <para>
/// <c>Console.ReadLine()</c> cannot be cancelled or timed out. Racing it against a delay does not
/// help: the losing read stays parked inside the console's input path, still consuming keystrokes
/// that were meant for the interactive prompt — which is the exact failure this replaces. Polling
/// <c>KeyAvailable</c> and assembling the line ourselves means we can stop reading and actually let
/// go of the terminal.
/// </para>
/// </summary>
public static class ConsoleLineReader
{
    private const int PollIntervalMs = 25;

    /// <summary>
    /// Reads one line from the console, echoing as it goes.
    /// Returns <c>null</c> if <paramref name="timeout"/> elapses, the token is cancelled, or the
    /// console has no interactive input — never leaving a reader attached to stdin.
    /// </summary>
    public static async Task<string?> ReadLineAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (Console.IsInputRedirected)
            return null;

        var deadline = timeout == Timeout.InfiniteTimeSpan
            ? DateTime.MaxValue
            : DateTime.UtcNow + timeout;

        var buffer = new System.Text.StringBuilder();

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            if (!Console.KeyAvailable)
            {
                try { await Task.Delay(PollIntervalMs, ct); }
                catch (OperationCanceledException) { return null; }
                continue;
            }

            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();

                case ConsoleKey.Backspace:
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        Console.Write("\b \b");
                    }
                    continue;

                case ConsoleKey.Escape:
                    Console.WriteLine();
                    return null;

                default:
                    // Control characters other than the ones handled above carry no text.
                    if (key.KeyChar == '\0' || char.IsControl(key.KeyChar))
                        continue;
                    buffer.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                    continue;
            }
        }

        Console.WriteLine();
        return null;
    }
}
