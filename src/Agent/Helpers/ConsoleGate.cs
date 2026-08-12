namespace AgentFox.Helpers;

/// <summary>
/// The single serialization point for console output that does not originate from the
/// interactive REPL thread.
///
/// <para>
/// The REPL shares one terminal with every background producer in the process: cron turns,
/// sub-agent announcements, HITL approval banners, channel handlers. Nothing coordinated them,
/// which produced two distinct failures. Concurrent producers (Background lane runs 3 at a time,
/// Subagent lane up to <c>MaxConcurrentSubAgents</c>) interleaved mid-renderable, so a Spectre
/// table from one turn was shredded by a markup line from another. And any write issued while
/// PrettyPrompt had the prompt on screen — or while the streaming <c>Live</c> display owned the
/// screen — scrolled the terminal underneath a renderer that tracks its own cursor position,
/// leaving it drawing at stale coordinates. That is what made the completion pane stop appearing.
/// </para>
///
/// <para>
/// Two guarantees: writes are serialized against each other, and writes issued while the terminal
/// is <see cref="Suspend">suspended</see> are buffered until the owner releases it. When no CLI is
/// attached (service / web-only / redirected mode) the suspend depth is never raised, so every
/// write passes straight through and behaviour is unchanged.
/// </para>
/// </summary>
public static class ConsoleGate
{
    private static readonly object Sync = new();
    private static readonly List<Action> Pending = [];

    private static int _suspendDepth;
    private static int _interactiveReadDepth;
    private static int _droppedWhileSuspended;

    /// <summary>
    /// Ceiling on buffered writes. A wedged prompt plus a chatty cron fleet must not grow this
    /// without bound; past the cap the oldest entries are dropped and reported on flush.
    /// </summary>
    private const int MaxPending = 500;

    /// <summary>
    /// True when at least one write is waiting for the terminal to be released. The CLI's console
    /// wrapper polls this so an idle prompt can surrender the line and let the backlog through,
    /// rather than sitting on it until the user happens to press Enter.
    /// </summary>
    public static bool HasPendingOutput
    {
        get { lock (Sync) return Pending.Count > 0; }
    }

    /// <summary>True while some owner holds the terminal.</summary>
    public static bool IsSuspended
    {
        get { lock (Sync) return _suspendDepth > 0; }
    }

    /// <summary>
    /// True while a tool is reading a line from the user inside a running turn. The CLI's streaming
    /// display polls this and stops repainting, because a spinner refreshing every 120ms on top of
    /// the line the user is typing makes the answer impossible to see.
    /// </summary>
    public static bool InteractiveReadInProgress
    {
        get { lock (Sync) return _interactiveReadDepth > 0; }
    }

    /// <summary>
    /// Emit <paramref name="write"/> now, or buffer it if the terminal is currently held.
    /// The action should perform ordinary <c>AnsiConsole</c> / <c>Console</c> calls; it runs under
    /// the gate's lock so two producers can never interleave inside one renderable.
    /// </summary>
    public static void Write(Action write)
    {
        ArgumentNullException.ThrowIfNull(write);

        lock (Sync)
        {
            if (_suspendDepth == 0)
            {
                Emit(write);
                return;
            }

            if (Pending.Count >= MaxPending)
            {
                Pending.RemoveAt(0);
                _droppedWhileSuspended++;
            }

            Pending.Add(write);
        }
    }

    /// <summary>
    /// Take the terminal. Every <see cref="Write"/> issued until the returned handle is disposed is
    /// buffered; disposal flushes them in arrival order. Nested suspends are counted, so the
    /// innermost release does not flush early.
    /// </summary>
    public static IDisposable Suspend()
    {
        lock (Sync) _suspendDepth++;
        return new Resumer(interactive: false);
    }

    /// <summary>
    /// Take the terminal for a prompt the user is expected to type into. Implies
    /// <see cref="Suspend"/>, and additionally tells the CLI's streaming display to hold still.
    /// </summary>
    public static IDisposable BeginInteractiveRead()
    {
        lock (Sync)
        {
            _suspendDepth++;
            _interactiveReadDepth++;
        }
        return new Resumer(interactive: true);
    }

    /// <summary>
    /// Emit everything buffered so far without changing the suspend depth. Used by the REPL when it
    /// briefly steps out of the prompt specifically to let background output through.
    /// </summary>
    public static void Flush()
    {
        lock (Sync)
        {
            if (Pending.Count == 0 && _droppedWhileSuspended == 0)
                return;

            foreach (var write in Pending)
                Emit(write);
            Pending.Clear();

            if (_droppedWhileSuspended > 0)
            {
                var dropped = _droppedWhileSuspended;
                _droppedWhileSuspended = 0;
                Emit(() => Spectre.Console.AnsiConsole.MarkupLine(
                    $"[dim]… {dropped} earlier background message(s) dropped — output buffer overflowed.[/]"));
            }
        }
    }

    /// <summary>
    /// Runs one buffered/direct write. A producer that throws (a Spectre markup parse error on
    /// model-authored text is the recurring case) must not take down the gate or the writes queued
    /// behind it.
    /// </summary>
    private static void Emit(Action write)
    {
        try { write(); }
        catch { /* a broken renderable is not worth losing the rest of the output over */ }
    }

    private static void Release(bool interactive)
    {
        lock (Sync)
        {
            if (interactive && _interactiveReadDepth > 0)
                _interactiveReadDepth--;
            if (_suspendDepth > 0)
                _suspendDepth--;
            if (_suspendDepth > 0)
                return;
        }

        Flush();
    }

    private sealed class Resumer(bool interactive) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            Release(interactive);
        }
    }
}
