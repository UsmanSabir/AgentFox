using TradingAgent.Broker;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// The break-before-make sequencer — the one code path in the protective-stop machinery that cancels a
/// LIVE broker order.
///
/// <para>
/// Every test here is about a failure, because the success path is the easy half. What makes this safe
/// to run against a real account is that each way it can go wrong leaves MORE protection resting, never
/// less: no cover armed means nothing is cancelled; a cancel that throws or cannot be verified means
/// nothing is cancelled. The recorded order of operations matters as much as the outcome — cover before
/// cancel, cancel before retire, retire before the caller places anything — so the ports record their
/// calls and the tests assert on that sequence.
/// </para>
/// </summary>
[TestClass]
public sealed class StopReplacementTests
{
    /// <summary>Records what the sequencer actually did, in order, and lets each step be made to fail.</summary>
    private sealed class Ports
    {
        public List<string> Calls { get; } = [];
        public bool CoverWriteSucceeds { get; set; } = true;
        public BrokerCancellationResult Cancellation { get; set; } =
            new(Gone: true, RequestAccepted: true, Verified: true, "cancelled and gone");
        public Exception? CancelThrows { get; set; }
        public bool RetireApplies { get; set; } = true;

        public StopReplacementPorts Build(ProtectiveStop successor) => new(
            ArmCoverAsync: (stop, quantity, _) =>
            {
                Calls.Add($"cover:{stop.StopId}:{quantity}");
                return Task.CompletedTask;
            },
            ReloadStopAsync: (stopId, _) =>
            {
                Calls.Add($"reload:{stopId}");
                return Task.FromResult<ProtectiveStop?>(CoverWriteSucceeds
                    ? successor with { LocalBackstopArmedId = "backstop-1" }
                    : successor);
            },
            CancelOrderAsync: (orderNo, _) =>
            {
                Calls.Add($"cancel:{orderNo}");
                if (CancelThrows is not null) throw CancelThrows;
                return Task.FromResult(Cancellation);
            },
            RetirePredecessorAsync: (stop, _, _) =>
            {
                Calls.Add($"retire:{stop.StopId}");
                return Task.FromResult(RetireApplies);
            },
            AnnounceAsync: (planned, result, _) =>
            {
                Calls.Add(planned ? "announce:planned" : $"announce:resolved:{result!.Outcome}");
                return Task.CompletedTask;
            });
    }

    private static ProtectiveStop Predecessor => new()
    {
        StopId = "old", Symbol = "FFC", State = "active",
        StopTrigger = 120m, StopLimit = 118.8m, DesiredQuantity = 100,
        LastOrderNo = "OLD-120"
    };

    private static ProtectiveStop Successor => new()
    {
        StopId = "new", Symbol = "FFC", State = "active",
        StopTrigger = 125m, StopLimit = 123.75m, DesiredQuantity = 100,
        SupersedesStopId = "old"
    };

    private static Task<StopReplacementResult> RunAsync(
        Ports ports, ProtectiveStop? successor = null, decimal? held = 100m) =>
        StopReplacement.OpenWindowAsync(
            successor ?? Successor, Predecessor, held, "test", ports.Build(successor ?? Successor),
            CancellationToken.None);

    // ── The success path, and the ORDER it happens in ────────────────────────

    [TestMethod]
    public async Task CoverIsArmedAndConfirmedBeforeAnythingIsCancelled()
    {
        var ports = new Ports();

        var result = await RunAsync(ports);

        Assert.AreEqual(StopReplacementOutcome.PlaceReplacement, result.Outcome, result.Reason);
        CollectionAssert.AreEqual(
            new[]
            {
                "announce:planned", "cover:new:100", "reload:new", "cancel:OLD-120", "retire:old",
                "announce:resolved:PlaceReplacement"
            },
            ports.Calls,
            "announce before anything is touched, cover before cancel, cancel before retire, "
            + "resolution last — the whole point of the sequence");
    }

    // ── The announcement contract ────────────────────────────────────────────
    //
    // This is the half that used to live in the worker's wiring, where no test could reach it and
    // only a live broker run could prove it. The guarantee is about ORDER and PAIRING, so it belongs
    // with the sequence that opens the gap.

    [TestMethod]
    public async Task TheGapIsAnnouncedBeforeANYPortThatCouldCancel()
    {
        var ports = new Ports();

        await RunAsync(ports);

        Assert.AreEqual("announce:planned", ports.Calls[0],
            "nothing may be armed, cancelled or retired before the operator has been told");
    }

    [TestMethod]
    public async Task EveryHeldOutcomeStillGetsItsResolution()
    {
        // The branch that matters most: nothing was cancelled, so the reader must be told the raise
        // did not happen rather than being left holding the alarming half of the story.
        foreach (var ports in new[]
        {
            new Ports { CoverWriteSucceeds = false },
            new Ports { CancelThrows = new InvalidOperationException("socket died") },
            new Ports { Cancellation = new(false, true, false, "accepted, still outstanding") }
        })
        {
            var result = await RunAsync(ports);

            Assert.AreEqual(StopReplacementOutcome.HoldOldOrderIntact, result.Outcome);
            Assert.AreEqual("announce:planned", ports.Calls.First());
            Assert.AreEqual("announce:resolved:HoldOldOrderIntact", ports.Calls.Last(),
                "a plan announced must always be resolved, whatever went wrong");
        }
    }

    [TestMethod]
    public async Task AnUnreadableHoldingIsStillAnnouncedAndResolved()
    {
        var ports = new Ports();

        await RunAsync(ports, held: null);

        CollectionAssert.AreEqual(
            new[] { "announce:planned", "announce:resolved:HoldOldOrderIntact" }, ports.Calls,
            "nothing was touched, but the pairing still holds");
    }

    [TestMethod]
    public async Task NothingIsAnnouncedWhenThereIsNoGapToOpen()
    {
        // No broker order behind the predecessor means no cancellation and no uncovered moment —
        // announcing one would be reporting an event that never happens.
        var ports = new Ports();

        await StopReplacement.OpenWindowAsync(
            Successor, Predecessor with { LastOrderNo = null }, 100m, "test",
            ports.Build(Successor), CancellationToken.None);

        Assert.AreEqual(0, ports.Calls.Count);
    }

    [TestMethod]
    public async Task TheSequenceWorksWithNoObserverAtAll()
    {
        // The community edition registers none. An absent announcer must not change the outcome.
        var ports = new Ports();
        var silent = ports.Build(Successor) with { AnnounceAsync = null };

        var result = await StopReplacement.OpenWindowAsync(
            Successor, Predecessor, 100m, "test", silent, CancellationToken.None);

        Assert.AreEqual(StopReplacementOutcome.PlaceReplacement, result.Outcome, result.Reason);
        CollectionAssert.AreEqual(
            new[] { "cover:new:100", "reload:new", "cancel:OLD-120", "retire:old" }, ports.Calls);
    }

    [TestMethod]
    public async Task CoverIsSizedToTheHoldingWhenItIsSmallerThanTheIntent()
    {
        // Never cover more than is actually owned, whatever the stop's intent says.
        var ports = new Ports();

        await RunAsync(ports, held: 40m);

        Assert.AreEqual("cover:new:40", ports.Calls[1], "after the announcement");
    }

    [TestMethod]
    public async Task AStopThatAlreadyHasCoverDoesNotArmASecond()
    {
        var ports = new Ports();
        var covered = Successor with { LocalBackstopArmedId = "existing" };

        var result = await RunAsync(ports, covered);

        Assert.AreEqual(StopReplacementOutcome.PlaceReplacement, result.Outcome, result.Reason);
        CollectionAssert.AreEqual(
            new[]
            {
                "announce:planned", "cancel:OLD-120", "retire:old",
                "announce:resolved:PlaceReplacement"
            },
            ports.Calls);
    }

    // ── Failures. Each one must leave the old order resting ──────────────────

    [TestMethod]
    public async Task UnconfirmedCoverCancelsNothing()
    {
        // The write appeared to go out but storage does not show it. Unconfirmed cover is no cover, and
        // this is the last point at which doing nothing is still safe.
        var ports = new Ports { CoverWriteSucceeds = false };

        var result = await RunAsync(ports);

        Assert.AreEqual(StopReplacementOutcome.HoldOldOrderIntact, result.Outcome);
        Assert.IsFalse(result.CancelAttempted);
        Assert.IsFalse(ports.Calls.Any(c => c.StartsWith("cancel")), "nothing may be cancelled");
        StringAssert.Contains(result.Reason, "could not be confirmed armed");
    }

    [TestMethod]
    public async Task UnreadableHoldingsCancelNothing()
    {
        // Unknown is never zero: with no holding reading, cover cannot be sized, so the old order stays.
        var ports = new Ports();

        var result = await RunAsync(ports, held: null);

        Assert.AreEqual(StopReplacementOutcome.HoldOldOrderIntact, result.Outcome);
        CollectionAssert.AreEqual(
            new[] { "announce:planned", "announce:resolved:HoldOldOrderIntact" }, ports.Calls,
            "the gap is announced and resolved, but not even the cover write should be attempted");
    }

    [TestMethod]
    public async Task AZeroHoldingCancelsNothing()
    {
        var ports = new Ports();

        var result = await RunAsync(ports, held: 0m);

        Assert.AreEqual(StopReplacementOutcome.HoldOldOrderIntact, result.Outcome);
        CollectionAssert.AreEqual(
            new[] { "announce:planned", "announce:resolved:HoldOldOrderIntact" }, ports.Calls);
    }

    [TestMethod]
    public async Task ACancelThatThrowsLeavesTheOldOrderAssumedResting()
    {
        var ports = new Ports { CancelThrows = new InvalidOperationException("socket died") };

        var result = await RunAsync(ports);

        Assert.AreEqual(StopReplacementOutcome.HoldOldOrderIntact, result.Outcome);
        Assert.IsTrue(result.CancelAttempted);
        Assert.IsFalse(ports.Calls.Contains("retire:old"),
            "the predecessor must NOT be retired on an unresolved cancel");
        StringAssert.Contains(result.Reason, "socket died");
    }

    [TestMethod]
    public async Task ACancelAcceptedButNotVERIFIEDGoneIsTreatedAsStillResting()
    {
        // The dangerous middle case: the broker took the request and the order is still in the book.
        // Trusting acceptance here would retire a row whose order is live and leave the position bare.
        var ports = new Ports
        {
            Cancellation = new(Gone: false, RequestAccepted: true, Verified: false,
                "accepted, but still outstanding")
        };

        var result = await RunAsync(ports);

        Assert.AreEqual(StopReplacementOutcome.HoldOldOrderIntact, result.Outcome);
        Assert.IsFalse(ports.Calls.Contains("retire:old"));
        StringAssert.Contains(result.Reason, "not confirmed cancelled");
    }

    [TestMethod]
    public async Task AnOrderAlreadyAbsentFromTheBookCountsAsGone()
    {
        // CancelExactAsync reports Gone=true for an order that had already left the book. That is a
        // genuine "nothing is holding the shares", so the replacement may proceed.
        var ports = new Ports
        {
            Cancellation = new(Gone: true, RequestAccepted: false, Verified: true,
                "already absent from the outstanding book")
        };

        var result = await RunAsync(ports);

        Assert.AreEqual(StopReplacementOutcome.PlaceReplacement, result.Outcome, result.Reason);
        Assert.IsTrue(ports.Calls.Contains("retire:old"));
    }

    [TestMethod]
    public async Task APredecessorWithNoBrokerOrderNeedsNoWindowAtAll()
    {
        var ports = new Ports();

        var result = await StopReplacement.OpenWindowAsync(
            Successor, Predecessor with { LastOrderNo = null }, 100m, "test",
            ports.Build(Successor), CancellationToken.None);

        Assert.AreEqual(StopReplacementOutcome.PlaceReplacement, result.Outcome, result.Reason);
        Assert.AreEqual(0, ports.Calls.Count, "nothing to cover for and nothing to cancel");
    }

    [TestMethod]
    public async Task AConfirmedCancelStillProceedsWhenTheRetirementTransitionDidNotApply()
    {
        // Someone else already moved the predecessor. The order is proven gone either way, so the
        // shares are free and holding the replacement back would protect nothing.
        var ports = new Ports { RetireApplies = false };

        var result = await RunAsync(ports);

        Assert.AreEqual(StopReplacementOutcome.PlaceReplacement, result.Outcome, result.Reason);
    }
}
