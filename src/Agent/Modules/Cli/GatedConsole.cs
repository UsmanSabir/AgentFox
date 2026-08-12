using System.Text;
using AgentFox.Helpers;
using PrettyPrompt.Consoles;

namespace AgentFox.Modules.Cli;

/// <summary>
/// The console PrettyPrompt reads and draws through, wrapped so background output and the prompt
/// can share one terminal.
///
/// <para>
/// PrettyPrompt renders incrementally against its own model of where the cursor is. Anything else
/// that writes while the prompt is on screen scrolls the terminal underneath that model, and every
/// later render lands at coordinates that no longer mean what the renderer thinks — which is how the
/// prompt ends up drawing nothing visible and the completion pane stops appearing. So background
/// writes are held by <see cref="ConsoleGate"/> while the prompt is live.
/// </para>
///
/// <para>
/// Holding them is only half of it: output the operator never sees is not better than corrupted
/// output. While the prompt sits idle with an empty input buffer, this wrapper hands PrettyPrompt a
/// synthetic Ctrl+C — the one input that makes it tear its render down cleanly and return — so the
/// REPL can flush the backlog onto a clean screen and immediately draw a fresh prompt. If the
/// operator has already typed something, the interrupt is withheld and the backlog waits until they
/// submit; discarding half-typed input to print a notification would be a worse trade.
/// </para>
/// </summary>
internal sealed class GatedConsole : IConsole
{
    /// <summary>Ctrl+C — PrettyPrompt's "abandon this line" input, returned as a wake-up.</summary>
    private static readonly ConsoleKeyInfo Interrupt =
        new('', ConsoleKey.C, shift: false, alt: false, control: true);

    private const int IdlePollMs = 25;

    private readonly IConsole _inner;
    private readonly Func<bool> _inputIsEmpty;

    /// <param name="inputIsEmpty">
    /// Whether the prompt's edit buffer is currently empty. Only then may a pending background
    /// write interrupt the read.
    /// </param>
    public GatedConsole(Func<bool> inputIsEmpty, IConsole? inner = null)
    {
        _inputIsEmpty = inputIsEmpty;
        _inner        = inner ?? new SystemConsole();
    }

    public ConsoleKeyInfo ReadKey(bool intercept)
    {
        while (true)
        {
            if (_inner.KeyAvailable)
                return _inner.ReadKey(intercept);

            if (ConsoleGate.HasPendingOutput && _inputIsEmpty())
                return Interrupt;

            Thread.Sleep(IdlePollMs);
        }
    }

    // ── Everything else is straight delegation ───────────────────────────────

    public int CursorTop         => _inner.CursorTop;
    public int BufferWidth       => _inner.BufferWidth;
    public int WindowHeight      => _inner.WindowHeight;
    public int WindowTop         => _inner.WindowTop;
    public bool KeyAvailable     => _inner.KeyAvailable;
    public bool IsErrorRedirected => _inner.IsErrorRedirected;

    public bool CaptureControlC
    {
        get => _inner.CaptureControlC;
        set => _inner.CaptureControlC = value;
    }

    public event ConsoleCancelEventHandler CancelKeyPress
    {
        add    => _inner.CancelKeyPress += value;
        remove => _inner.CancelKeyPress -= value;
    }

    public void Write(StringBuilder value, bool hideCursor)          => _inner.Write(value, hideCursor);
    public void WriteLine(StringBuilder value, bool hideCursor)      => _inner.WriteLine(value, hideCursor);
    public void WriteError(StringBuilder value, bool hideCursor)     => _inner.WriteError(value, hideCursor);
    public void WriteErrorLine(StringBuilder value, bool hideCursor) => _inner.WriteErrorLine(value, hideCursor);

    public void Write(string? value)          => _inner.Write(value);
    public void WriteLine(string? value)      => _inner.WriteLine(value);
    public void WriteError(string? value)     => _inner.WriteError(value);
    public void WriteErrorLine(string? value) => _inner.WriteErrorLine(value);

    public void Write(ReadOnlySpan<char> value)          => _inner.Write(value);
    public void WriteLine(ReadOnlySpan<char> value)      => _inner.WriteLine(value);
    public void WriteError(ReadOnlySpan<char> value)     => _inner.WriteError(value);
    public void WriteErrorLine(ReadOnlySpan<char> value) => _inner.WriteErrorLine(value);

    public void ShowCursor() => _inner.ShowCursor();
    public void HideCursor() => _inner.HideCursor();

    public void Clear()                          => _inner.Clear();
    public void InitVirtualTerminalProcessing()  => _inner.InitVirtualTerminalProcessing();
    public void SetModifyOtherKeys(bool enable)  => _inner.SetModifyOtherKeys(enable);
    public void SetNewlineAutoReturn(bool enable) => _inner.SetNewlineAutoReturn(enable);
}
