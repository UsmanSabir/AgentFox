using Microsoft.Extensions.Logging.Abstractions;
using TradingAgent.Reconciliation;

namespace AgentFox.ChannelTests;

/// <summary>
/// Pins the bookkeeping behind an event-driven account refresh. The reading needs a broker and a
/// 74-member repository; this needs a counter, which is the whole reason it was extracted.
///
/// <para>
/// The failure that matters is not an extra read — it is a pass that throws or races and leaves the
/// queued flag set, because that stops EVERY future refresh, permanently, with nothing in the log to
/// say the deployment has gone back to a 21-minute-old account picture.
/// </para>
/// </summary>
[TestClass]
public sealed class CoalescingRefreshTests
{
    [TestMethod]
    public async Task ABurstOfEventsBecomesOneRead()
    {
        var reasons = new List<string>();
        var refresh = Build(reason => { reasons.Add(reason); return Task.CompletedTask; });

        // One order's life: accepted, then filling. Every one of them describes the same account.
        refresh.Request("an order is now resting");
        refresh.Request("an order filled");
        refresh.Request("an order filled again");

        await SettleAsync(refresh);

        Assert.AreEqual(1, reasons.Count, "Three events describing one account are worth one read.");
        Assert.AreEqual("an order filled again", reasons[0],
            "The last reason wins: it is the most useful description of why the account is being read.");
    }

    [TestMethod]
    public async Task ARequestArrivingDuringAReadGetsItsOwnRead()
    {
        // The flag-clear ordering, and it is the subtle half. A read already in flight may have started
        // before this change and cannot be assumed to include it, so folding the request into that pass
        // would lose it — turning the event-driven path back into the poll it replaces.
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var reasons = new List<string>();

        var refresh = Build(async reason =>
        {
            reasons.Add(reason);
            if (reasons.Count == 1)
            {
                started.SetResult();
                await release.Task;
            }
        });

        refresh.Request("the first change");
        await started.Task;

        refresh.Request("a change made while the read was running");
        release.SetResult();

        await SettleAsync(refresh);

        Assert.AreEqual(2, reasons.Count,
            "The second change needs a read of its own; the first read may predate it.");
        Assert.AreEqual("a change made while the read was running", reasons[1]);
    }

    [TestMethod]
    public async Task AFailedReadDoesNotWedgeEveryFutureOne()
    {
        var attempts = 0;
        var refresh = Build(_ =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException(new InvalidOperationException("the broker refused the session"))
                : Task.CompletedTask;
        });

        refresh.Request("the change nobody could read");
        await SettleAsync(refresh);
        Assert.AreEqual(1, attempts);

        refresh.Request("the next change");
        await SettleAsync(refresh);

        Assert.AreEqual(2, attempts,
            "A throwing pass must leave the door open, or one broker hiccup silently ends every "
            + "event-driven refresh for the life of the process.");
    }

    [TestMethod]
    public async Task TheFailureIsSwallowed_BecauseNobodyIsAwaitingIt()
    {
        // Request() is called from a socket pump and returns immediately, so an escaping exception would
        // be an unobserved task exception rather than anything a caller could handle.
        var refresh = Build(_ => throw new InvalidOperationException("boom"));

        refresh.Request("a change");
        await SettleAsync(refresh);

        Assert.IsFalse(refresh.Pending);
    }

    private static CoalescingRefresh Build(Func<string, Task> run) =>
        // Zero burst window: these tests are about the flag mechanics, not about how long a burst is
        // given to settle. Production passes a couple of seconds.
        new(run, TimeSpan.Zero, NullLogger.Instance, "Test");

    /// <summary>
    /// Waits for the fire-and-forget pass to finish. Polls <see cref="CoalescingRefresh.Pending"/> rather
    /// than sleeping a fixed time, so the test is not a race on a slow machine.
    /// </summary>
    private static async Task SettleAsync(CoalescingRefresh refresh)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (refresh.Pending && DateTime.UtcNow < deadline) await Task.Delay(5);

        // Pending clears BEFORE the work runs, so one more yield is needed for the delegate itself.
        await Task.Delay(50);
    }
}
