namespace AgentFox.ChannelTests.Simulation;

/// <summary>
/// The protective-stop pipeline end to end against a <see cref="SimulatedBroker"/>.
///
/// <para>
/// <b>Two kinds of test live here.</b> Ordinary ones pin behaviour that is right and must stay right.
/// <c>KnownGap_*</c> ones pin behaviour that is WRONG today, so the harness is proven to reproduce the
/// defect before anything fixes it. Each names the plan step that must invert it; when that step lands,
/// the test fails, and is rewritten to assert the fixed behaviour rather than deleted.
/// </para>
/// </summary>
[TestClass]
public sealed class ProtectionScenarioTests
{
    private const string Symbol = "PPL";

    // ── The simulator keeps the venue's measured rules ────────────────────────

    [TestMethod]
    public async Task Sim_RefusesASellAboveTheSharesNotAlreadyCommittedToRestingSells()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        h.Broker.SetHolding(Symbol, 100, 230m);

        var first = await h.Broker.PlaceOrderGroupsAsync([[Stop(100)]]);
        var second = await h.Broker.PlaceOrderGroupsAsync([[Stop(1)]]);

        Assert.IsTrue(first[0][0].Success);
        Assert.IsFalse(second[0][0].Success);
        StringAssert.Contains(second[0][0].Message, "cannot sell more than 0 shares of PPL");
    }

    [TestMethod]
    public async Task Sim_TheCloseCancelsEveryRestingOrderAsClx()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        h.Broker.SetHolding(Symbol, 100, 230m);
        var placed = await h.Broker.PlaceOrderGroupsAsync([[Stop(100)]]);

        h.Broker.EndOfDay();
        var book = await h.Broker.ReadSnapshotAsync();

        Assert.AreEqual(0, book.OpenOrders.Count);
        Assert.IsTrue(book.OrderEvents.Any(e => e.OrderNo == placed[0][0].OrderId && e.Action == "CLX"));
    }

    [TestMethod]
    public async Task Sim_ATriggeredStopKeepsItsShortNumberInTheAlternateColumn()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        h.Broker.SetHolding(Symbol, 100, 230m);
        var shortNo = (await h.Broker.PlaceOrderGroupsAsync([[Stop(100)]]))[0][0].OrderId;

        h.Broker.Trade(Symbol, 210m);   // through the 220 trigger and below the 217.80 limit: stranded
        var row = (await h.Broker.ReadSnapshotAsync()).OpenOrders.Single();

        Assert.AreEqual("LIMIT", row.OrderType);
        Assert.AreEqual(shortNo, row.AlternateOrderNo);
        Assert.AreNotEqual(shortNo, row.OrderNo);
        Assert.AreEqual(100, h.Broker.Custody(Symbol), "a limit above the market does not fill");
    }

    [TestMethod]
    public async Task Sim_ALostReplyStillLandsTheOrder()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        h.Broker.SetHolding(Symbol, 100, 230m);
        h.Broker.ThrowAfterNextSend();

        await Assert.ThrowsAsync<IOException>(() => h.Broker.PlaceOrderGroupsAsync([[Stop(100)]]));

        Assert.AreEqual(1, h.Broker.RestingSells(Symbol).Count);
    }

    // ── Scenario 2: the close clears the stop; the next session re-places it ───

    [TestMethod]
    public async Task Eod_AStopClearedAtTheCloseIsPlacedAgainAtTheNextSession()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);

        await h.RunPassAsync();
        var dayOne = h.Broker.RestingSells(Symbol).Single();
        Assert.AreEqual(100, dayOne.Remaining);

        h.RollToNextSession();
        Assert.AreEqual(0, h.Broker.RestingSells(Symbol).Count, "the venue cleared the book");

        await h.RunPassAsync();
        var dayTwo = h.Broker.RestingSells(Symbol).Single();
        Assert.AreEqual(100, dayTwo.Remaining);
        Assert.AreNotEqual(dayOne.OrderNo, dayTwo.OrderNo);
        Assert.AreEqual(2, h.Broker.PlacementCalls);
    }

    // ── Scenario 1: a halt cancels the stop mid-session ────────────────────────

    [TestMethod]
    public async Task Halt_NoPlacementIsAttemptedWhileTheVenueIsHalted()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);
        await h.RunPassAsync();

        h.Broker.Halt();
        await h.RunPassAsync();

        Assert.AreEqual(1, h.Broker.PlacementCalls, "nothing was sent to a halted venue");
    }

    /// <summary>
    /// KNOWN GAP (audit B6). Inverted by plan step 3: coverage must come from the live book, so the
    /// reopen pass sees the cancelled stop and places it again.
    /// </summary>
    [TestMethod]
    public async Task KnownGap_AStopCancelledByAHaltIsNotPlacedAgainTheSameSession()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        var stop = await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);
        await h.RunPassAsync();
        Assert.AreEqual(1, h.Broker.RestingSells(Symbol).Count);

        h.Broker.Halt();
        h.Broker.Reopen();
        await h.RunPassAsync();

        Assert.AreEqual(0, h.Broker.RestingSells(Symbol).Count,
            "GAP: the position has no native stop after the reopen");
        Assert.AreEqual(1, h.Broker.PlacementCalls);
        Assert.AreEqual(h.Today, (await h.ReloadAsync(stop.StopId)).LastPlacedSessionDate,
            "the ledger still says today's stop is placed");
        Assert.IsTrue(h.Log.Lines.Any(l => l.Contains("Already placed for")),
            "skipped on the ledger's word, not the book's");
    }

    /// <summary>
    /// Only the worker's DECISION stands between a halt and a re-placed stop: once it decides to place, the
    /// execution boundary lets the same-day order through to the venue. Plan step 3 therefore needs to fix
    /// the decision, not unpick a replay.
    /// </summary>
    [TestMethod]
    public async Task AfterAHalt_AReSubmissionShapedLikeTheWorkersReachesTheBrokerAndRests()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        var stop = await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);
        await h.RunPassAsync();
        h.Broker.Halt();
        h.Broker.Reopen();

        var again = await h.SubmitAsWorkerWouldAsync(stop, 100);

        Assert.IsFalse(again.IsReplay, again.Reason);
        Assert.AreEqual(2, h.Broker.PlacementCalls);
        Assert.AreEqual(100, h.Broker.RestingSells(Symbol).Single().Remaining);
    }

    /// <summary>
    /// What actually keeps a duplicate stop off the book: the execution boundary re-reads the broker and
    /// sizes every SELL against custody minus resting SELLs, so a second identical stop is refused by our
    /// own code before the broker is asked. The venue's identical rule is the net beneath it.
    /// </summary>
    [TestMethod]
    public async Task ASecondIdenticalStopIsRefusedAtTheExecutionBoundaryBeforeTheBroker()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        var stop = await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);

        var first = await h.SubmitAsWorkerWouldAsync(stop, 100);
        var second = await h.SubmitAsWorkerWouldAsync(stop, 100);

        Assert.IsTrue(first.Executed, first.Reason);
        Assert.IsFalse(second.Executed);
        StringAssert.Contains(second.Reason, "No uncommitted PPL shares");
        Assert.AreEqual(1, h.Broker.PlacementCalls);
        Assert.AreEqual(1, h.Broker.RestingSells(Symbol).Count);
    }

    [TestMethod]
    public async Task Idempotency_TheSameSignalObjectSubmittedTwiceUnderOneKeyIsReplayed()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        var groups = new[] { (IReadOnlyList<TradingAgent.Models.TradingSignal>)[Buy()] };

        var first = await h.Manager.ExecuteGroupsAsync(groups, "same-key");
        var second = await h.Manager.ExecuteGroupsAsync(groups, "same-key");

        Assert.IsTrue(first.Executed, first.Reason);
        Assert.IsTrue(second.IsReplay, second.Reason);
        Assert.AreEqual(1, h.Broker.PlacementCalls);
    }

    /// <summary>
    /// FINDING (2026-11-28), pinned so a change to it is deliberate. <c>TradingSignal.Timestamp</c> defaults
    /// to the moment of construction and is serialised into the hashed request, so two signals built
    /// separately for the same order get DIFFERENT idempotency keys even under the same source key. A
    /// caller that rebuilds its signal per attempt therefore gets no replay protection from the key —
    /// for a BUY nothing else at the boundary stops the second. Protective stops are covered by the SELL
    /// sizing above; plan step 3 settles the key for the re-place path (hash without the timestamp, plus
    /// an attempt number from the stop's own order ledger).
    /// </summary>
    [TestMethod]
    public async Task Finding_TwoSeparatelyBuiltIdenticalBuysUnderOneKeyBothReachTheBroker()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        var built = new DateTime(2026, 11, 30, 5, 0, 0, DateTimeKind.Utc);

        var first = await h.Manager.ExecuteGroupsAsync([[Buy(built)]], "same-key");
        // Built a millisecond later, as the next pass would build it. Nothing else differs.
        var second = await h.Manager.ExecuteGroupsAsync([[Buy(built.AddMilliseconds(1))]], "same-key");

        Assert.IsTrue(first.Executed, first.Reason);
        Assert.IsFalse(second.IsReplay, second.Reason);
        Assert.AreEqual(2, h.Broker.PlacementCalls);
    }

    /// <summary>The control for the finding above: the construction time is the ONLY thing that differed.</summary>
    [TestMethod]
    public async Task Finding_SeparatelyBuiltBuysWithTheSameTimestampAreReplayed()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        var built = new DateTime(2026, 11, 30, 5, 0, 0, DateTimeKind.Utc);

        await h.Manager.ExecuteGroupsAsync([[Buy(built)]], "same-key");
        var second = await h.Manager.ExecuteGroupsAsync([[Buy(built)]], "same-key");

        Assert.IsTrue(second.IsReplay, second.Reason);
        Assert.AreEqual(1, h.Broker.PlacementCalls);
    }

    // ── Scenario 6: the reply is lost after the order landed ───────────────────

    [TestMethod]
    public async Task LostReply_NeverLeavesTwoStopsOverOneHolding_EvenAcrossARestart()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);
        h.Broker.ThrowAfterNextSend();

        await h.RunPassAsync();
        Assert.AreEqual(1, h.Broker.RestingSells(Symbol).Count, "the order landed despite the error");

        // A restart drops the in-memory refusal backoff, so the next pass decides from the book alone.
        h.Restart();
        await h.RunPassAsync();
        await h.RunPassAsync();

        var resting = h.Broker.RestingSells(Symbol);
        Assert.AreEqual(1, resting.Count);
        Assert.IsTrue(resting.Sum(o => o.Remaining) <= h.Broker.Custody(Symbol));
    }

    // ── Scenario 7: the book lags the placement ────────────────────────────────

    [TestMethod]
    public async Task BookLag_AStopNotYetVisibleInTheBookIsNotPlacedTwice()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);
        h.Broker.HideNewOrdersForReads(3);

        await h.RunPassAsync();
        await h.RunPassAsync();
        h.Restart();
        await h.RunPassAsync();

        Assert.AreEqual(1, h.Broker.PlacementCalls);
        Assert.AreEqual(1, h.Broker.RestingSells(Symbol).Count);
    }

    // ── Scenario 9: the order-value cap meets a protective stop ────────────────

    /// <summary>
    /// KNOWN GAP (audit correction, 2026-11-28). The value cap is meant to bound new exposure, yet it
    /// refuses a SELL that only reduces a position, so a holding worth more than the cap at its stop price
    /// gets no native stop at all. Hosted tenants run with the cap at 0 and do not hit this; the shipped
    /// default of 50,000 PKR does. Inverted by plan step 2 (reduce-only sells exempt).
    /// </summary>
    [TestMethod]
    public async Task KnownGap_TheOrderValueCapRefusesAProtectiveStopAboveIt()
    {
        using var h = new ProtectionHarness(50_000m, Symbol);
        await h.ProtectHoldingAsync(Symbol, 1_000, 105m, trigger: 100m, limit: 99m);   // 100,000 PKR

        await h.RunPassAsync();

        Assert.AreEqual(0, h.Broker.PlacementCalls, "GAP: refused before it reached the broker");
        Assert.AreEqual(0, h.Broker.RestingSells(Symbol).Count);
        Assert.IsTrue(h.Notifier.Sent.Any(s => s.Topic?.EndsWith("placement-failed") == true),
            "the owner is at least told");
    }

    [TestMethod]
    public async Task WithTheCapOff_TheSameStopIsPlaced()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        await h.ProtectHoldingAsync(Symbol, 1_000, 105m, trigger: 100m, limit: 99m);

        await h.RunPassAsync();

        Assert.AreEqual(1_000, h.Broker.RestingSells(Symbol).Single().Remaining);
    }

    private static TradingAgent.Models.TradingSignal Buy(DateTime? built = null) => new()
    {
        IsSignal = true,
        Action = "BUY",
        Symbol = Symbol,
        Quantity = 10,
        OrderType = "LIMIT",
        EntryPrice = 225m,
        Confidence = "HIGH",
        Timestamp = built ?? DateTime.UtcNow
    };

    private static TradingAgent.Models.TradingSignal Stop(int quantity) => new()
    {
        IsSignal = true,
        Action = "SELL",
        Symbol = Symbol,
        Quantity = quantity,
        OrderType = "STOPLOSS",
        EntryPrice = 220m,
        LimitPrice = 217.80m,
        Confidence = "HIGH"
    };
}
