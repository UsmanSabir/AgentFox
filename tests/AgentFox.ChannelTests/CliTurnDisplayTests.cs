using Spectre.Console;

namespace AgentFox.ChannelTests;

/// <summary>
/// The REPL now shows a spinner from the moment a turn is queued, then hands off to the streaming
/// <c>Live</c> view when the first chunk arrives. Spectre permits only one interactive display at a
/// time — starting <c>Live</c> while the spinner still holds the exclusivity lock throws
/// "Trying to run one or more interactive functions concurrently" — so the handoff has to be a
/// strict sequence, and the spinner has to unwind on the paths that never stream at all
/// (a turn that fails before OnStart, an empty-task guard, a "/new" reset).
/// <para>
/// These exercise the sequencing against the real Spectre version rather than a mock, because the
/// failure mode being guarded is a library invariant, not our own bookkeeping.
/// </para>
/// </summary>
[TestClass]
public sealed class CliTurnDisplayTests
{
    private IAnsiConsole _console = null!;

    [TestInitialize]
    public void SetUp()
    {
        // The test host has no console handle, so the legacy backend throws on cursor writes.
        // An ANSI console over a StringWriter renders the same way the terminal does — and keeps
        // its own exclusivity lock, which is the invariant under test.
        _console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi        = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.NoColors,
            Out         = new AnsiConsoleOutput(new StringWriter()),
            Interactive = InteractionSupport.Yes,
        });
    }

    /// <summary>
    /// Mirrors <c>CliWorker.RunBusyIndicatorAsync</c>: spin until streaming starts or the turn ends,
    /// then return — which releases Spectre's interactive lock.
    /// </summary>
    private Task RunBusyIndicatorAsync(Task streamStarted, Task completed) =>
        _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[dim]Sending...[/]", async ctx =>
            {
                var settled = Task.WhenAny(streamStarted, completed);
                while (await Task.WhenAny(settled, Task.Delay(20)) != settled)
                    ctx.Refresh();
            });

    [TestMethod]
    public async Task Spinner_ReleasesExclusivityBeforeLiveDisplayStarts()
    {
        var streamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var busy = RunBusyIndicatorAsync(streamStarted.Task, completed.Task);
        await Task.Delay(50); // let the spinner take the lock, as a queued turn would

        // The OnStart sequence: signal, drain the spinner, only then open the live view.
        streamStarted.TrySetResult();
        await busy;

        await _console.Live(new Text("Working..."))
            .AutoClear(false)
            .StartAsync(ctx => { ctx.Refresh(); return Task.CompletedTask; });

        completed.TrySetResult();
        Assert.IsTrue(busy.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task Spinner_UnwindsWhenTurnEndsWithoutStreaming()
    {
        // A turn that throws during session restore or memory recall never reaches OnStart, so
        // nothing else will ever signal the spinner — only the result completing can.
        var streamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var busy = RunBusyIndicatorAsync(streamStarted.Task, completed.Task);
        await Task.Delay(50);
        completed.TrySetResult();

        await busy.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(busy.IsCompletedSuccessfully, "spinner must unwind when a turn ends without streaming");

        // Lock actually released: a later display must be able to start.
        await _console.Live(new Text("after"))
            .StartAsync(ctx => { ctx.Refresh(); return Task.CompletedTask; });
    }

    [TestMethod]
    public async Task Spinner_AlreadySignalledBeforeItStarts_StillCompletes()
    {
        // Race guard: OnStart can fire before the spinner reaches its wait loop.
        var streamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        streamStarted.TrySetResult();

        var busy = RunBusyIndicatorAsync(streamStarted.Task, new TaskCompletionSource().Task);
        await busy.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(busy.IsCompletedSuccessfully);
    }
}
