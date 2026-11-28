using TradingAgent.Market;
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
    /// Was <c>KnownGap_AStopCancelledByAHaltIsNotPlacedAgainTheSameSession</c> (audit B6), asserting the position had
    /// no native stop after the reopen because the ledger said "already placed today". Flipped by plan step 3:
    /// coverage is read from the book order by order, so the cancelled stop is placed again — once, and counted once.
    /// </summary>
    [TestMethod]
    public async Task AStopCancelledByAHaltIsPlacedAgainAfterTheReopen()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        var stop = await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);
        await h.RunPassAsync();
        var morning = h.Broker.RestingSells(Symbol).Single();

        h.Broker.Halt();
        h.Broker.Reopen();
        await h.RunPassAsync();
        await h.RunPassAsync();   // and a later pass does not add a second

        var resting = h.Broker.RestingSells(Symbol).Single();
        Assert.AreNotEqual(morning.OrderNo, resting.OrderNo);
        Assert.AreEqual(100, resting.Remaining);
        Assert.AreEqual(2, h.Broker.PlacementCalls);
        Assert.AreEqual(100, (await h.ReloadAsync(stop.StopId)).PlacedQuantity,
            "the cancelled order no longer counts; only the replacement does");
        Assert.IsTrue(h.Notifier.Sent.Any(s => s.Topic?.EndsWith("left-book") == true),
            "the owner hears that the stop order disappeared");
    }

    /// <summary>
    /// Was <c>KnownGap_ARefusalDuringTheHaltDelaysTheRePlaceAfterTheReopen</c>, asserting that a placement refused during
    /// the halt started a backoff that held the re-place back after the reopen. Flipped by plan step 5: the worker
    /// follows the detected market phase, attempts nothing while halted, and the first pass after the reopen places.
    /// </summary>
    [TestMethod]
    public async Task AStopIsRePlacedOnTheFirstPassAfterTheReopen_TheHaltSpentNoBackoff()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);
        await h.RunPassAsync();

        h.Broker.Halt();
        await h.RunPassAsync();
        Assert.AreEqual(MarketPhase.Halted, h.MarketState.Current.Phase);
        Assert.AreEqual(1, h.Broker.PlacementCalls, "nothing attempted while halted");

        h.Broker.Reopen();
        await h.RunPassAsync();

        Assert.AreEqual(MarketPhase.ReopenSettling, h.MarketState.Current.Phase);
        Assert.AreEqual(100, h.Broker.RestingSells(Symbol).Single().Remaining);
        Assert.IsTrue(h.Notifier.Sent.Any(s => s.Topic == "trading.market.halt"));
        Assert.IsTrue(h.Notifier.Sent.Any(s => s.Topic == "trading.market.reopen"));
    }

    /// <summary>Owner Q2a: a stop the price already fell through would trigger the instant it rested; it waits.</summary>
    [TestMethod]
    public async Task AfterTheReopenAStopThePriceAlreadyFellThroughWaitsOutTheSettlingMinutes()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);
        await h.RunPassAsync();

        h.Broker.Halt();
        await h.RunPassAsync();
        h.Broker.Quote(Symbol, 215m);   // reopens below the 220 trigger, above the 209 catastrophic level
        h.Broker.Reopen();
        await h.RunPassAsync();

        Assert.AreEqual(0, h.Broker.RestingSells(Symbol).Count);
        Assert.AreEqual(1, h.Broker.PlacementCalls);
    }

    [TestMethod]
    public async Task AfterTheReopenAStopBelowTheCatastrophicLevelIsPlacedAtOnce()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);
        await h.RunPassAsync();

        h.Broker.Halt();
        await h.RunPassAsync();
        h.Broker.Quote(Symbol, 205m);   // below 220 × 0.95 = 209
        h.Broker.Reopen();
        await h.RunPassAsync();

        Assert.AreEqual(2, h.Broker.PlacementCalls, "the catastrophic level overrides the settling hold");
    }

    /// <summary>The rollout's Shadow for halts: the worker keeps trying as before, and the log records it would not have.</summary>
    [TestMethod]
    public async Task InShadow_HaltsAreDetectedAndLogged_ButNothingIsGatedOrAlerted()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        h.Options.MarketHaltHandling = TradingAgent.Config.RuleRollout.Shadow;
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);
        await h.RunPassAsync();

        h.Broker.Halt();
        await h.RunPassAsync();

        Assert.AreEqual(MarketPhase.Halted, h.MarketState.Current.Phase, "detected");
        Assert.IsFalse(h.Notifier.Sent.Any(s => s.Topic?.StartsWith("trading.market") == true), "nobody alerted");
        var log = await h.Repository.GetRuleShadowDecisionsAsync(DateTime.UtcNow.AddHours(-1), "market.halt-handling");
        Assert.IsTrue(log.Any(r => r.ShadowDecision == "Halted"), "the transition is in the decision log");
        Assert.IsTrue(log.Any(r => r.ShadowDecision == "Skip passes until the reopen"), "and what the worker would not have done");
    }

    /// <summary>
    /// The rollout's Shadow: the halt-cancelled stop is NOT re-placed (the old rule still acts), the owner is NOT
    /// alerted, and the decision log records that live coverage would have placed it — the evidence the owner reviews
    /// before switching it On.
    /// </summary>
    [TestMethod]
    public async Task InShadow_TheOldRuleActs_AndTheDecisionLogRecordsWhatLiveCoverageWouldHaveDone()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        h.Options.ProtectiveStopLiveCoverage = TradingAgent.Config.RuleRollout.Shadow;
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);
        await h.RunPassAsync();

        h.Broker.Halt();
        h.Broker.Reopen();
        await h.RunPassAsync();

        Assert.AreEqual(0, h.Broker.RestingSells(Symbol).Count, "Shadow acts on nothing");
        Assert.IsFalse(h.Notifier.Sent.Any(s => s.Topic?.EndsWith("left-book") == true), "and alerts nobody");
        var log = await h.Repository.GetRuleShadowDecisionsAsync(DateTime.UtcNow.AddHours(-1));
        var row = log.Single();
        Assert.AreEqual("protective-stop.live-coverage", row.Rule);
        Assert.AreEqual("Skip", row.CurrentDecision);
        Assert.AreEqual("Place 100", row.ShadowDecision);
        StringAssert.Contains(row.Detail, "left the book");
    }

    [TestMethod]
    public async Task InShadow_AgreementIsNotLogged()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        h.Options.ProtectiveStopLiveCoverage = TradingAgent.Config.RuleRollout.Shadow;
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);

        await h.RunPassAsync();
        await h.RunPassAsync();

        Assert.AreEqual(0, (await h.Repository.GetRuleShadowDecisionsAsync(DateTime.UtcNow.AddHours(-1))).Count);
    }

    [TestMethod]
    public void TheProductDefaultIsShadowUntilTheOwnerSwitchesItOn()
    {
        Assert.AreEqual(TradingAgent.Config.RuleRollout.Shadow,
            new TradingAgent.Config.TradingAgentOptions().ProtectiveStopLiveCoverage);
    }

    // ── Step 6: new accounts trade in ApprovalRequired ─────────────────────────

    /// <summary>
    /// Found while building step 6: in ApprovalRequired with the default approval mode ("Always"), every unattended
    /// order was denied — including the daily re-placement of every protective stop — so an account in that mode had
    /// no protection at all. Protection is now the owner's standing instruction and is placed without asking.
    /// </summary>
    [TestMethod]
    public async Task InApprovalRequired_ProtectiveStopsAreStillPlaced_ButABuyStillWaitsForAPerson()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        h.Options.ExecutionMode = "ApprovalRequired";
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);

        await h.RunPassAsync();
        var buy = await h.Manager.ExecuteGroupsAsync([[Buy()]], "unapproved-entry");

        Assert.AreEqual(100, h.Broker.RestingSells(Symbol).Single().Remaining, "the stop is placed");
        Assert.IsFalse(h.Notifier.Sent.Any(s => s.Topic?.EndsWith("unauthorised") == true));
        Assert.IsFalse(buy.Executed, "an entry with no approval is refused");
        Assert.AreEqual(1, h.Broker.PlacementCalls);
    }

    /// <summary>Owner Q4: with the reconciliation gate on, a sell needs only a fresh book read, a buy a fresh account read.</summary>
    [TestMethod]
    public async Task WithTheReconciliationGateOn_AStopIsPlacedOnItsOwnFreshBookRead_AndABuyReadsTheAccountOnDemand()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        h.Options.RequireReconciliationHealthy = true;   // nothing has reconciled yet: the cached state is "not run"
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);

        await h.RunPassAsync();
        var buy = await h.Manager.ExecuteGroupsAsync([[Buy()]], "entry");

        Assert.AreEqual(1, h.Broker.RestingSells(Symbol).Count, "the sell used the read its own sizing took");
        Assert.IsTrue(buy.Executed, buy.Reason);
    }

    [TestMethod]
    public async Task WithTheReconciliationGateOn_ABuyIsRefusedWhenTheAccountCannotBeRead()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        h.Options.RequireReconciliationHealthy = true;
        h.Broker.FailNextRead();

        var buy = await h.Manager.ExecuteGroupsAsync([[Buy()]], "entry");

        Assert.IsFalse(buy.Executed);
        StringAssert.Contains(buy.Reason, "A buy needs a healthy, recent read");
    }

    [TestMethod]
    public void ReadinessIsReachedOnTheFirstHealthyReadAndNotLostAfterwards()
    {
        var state = new TradingAgent.Reconciliation.TradingReconciliationState();
        Assert.IsFalse(state.HasBeenHealthy);

        state.Update(new(true, false, "down", DateTime.UtcNow));
        Assert.IsFalse(state.HasBeenHealthy);
        state.Update(new(true, true, "ok", DateTime.UtcNow));
        state.Update(new(true, false, "down again", DateTime.UtcNow));
        Assert.IsTrue(state.HasBeenHealthy, "reconciled once since start; later health is the gate's own business");
    }

    [TestMethod]
    public void TheApprovalRequiredChoiceSaysProtectiveStopsAreTheException()
    {
        var choice = TradingAgent.TradingModeChoices.All.Single(c => c.Mode == "ApprovalRequired");
        StringAssert.Contains(choice.Description, "except protective stops");
    }

    [TestMethod]
    public async Task AStopCancelledAtTheBrokersOwnAppIsPlacedAgain()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);
        await h.RunPassAsync();

        await h.Broker.CancelOrderAsync(h.Broker.RestingSells(Symbol).Single().OrderNo);
        await h.RunPassAsync();

        Assert.AreEqual(1, h.Broker.RestingSells(Symbol).Count);
        Assert.AreEqual(2, h.Broker.PlacementCalls);
    }

    /// <summary>
    /// An order that is simply not listed — no cancel, no trade on record — is not believed gone on one read: a book
    /// read that omitted a live order looks exactly like this, and believing it places a second stop.
    /// </summary>
    [TestMethod]
    public async Task AnOrderMissingWithNoRecordIsReplacedOnlyWhenALaterSnapshotConfirmsIt()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        h.Options.ProtectiveStopPropagationSeconds = 0;
        h.Options.ProtectiveStopAbsenceConfirmSeconds = 0;
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);
        await h.RunPassAsync();
        h.Broker.VanishWithoutTrace(h.Broker.RestingSells(Symbol).Single().OrderNo);

        await h.RunPassAsync();
        Assert.AreEqual(1, h.Broker.PlacementCalls, "one read is not proof");

        await h.RunPassAsync();
        Assert.AreEqual(2, h.Broker.PlacementCalls, "a second snapshot confirms it");
        Assert.AreEqual(1, h.Broker.RestingSells(Symbol).Count);
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

        // As the worker would see it: the ledger now names this morning's order, so the key differs from
        // the morning's and the boundary treats this as a new instruction, not the first one asked again.
        var again = await h.SubmitAsWorkerWouldAsync(await h.ReloadAsync(stop.StopId), 100);

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
    /// Was <c>Finding_TwoSeparatelyBuiltIdenticalBuysUnderOneKeyBothReachTheBroker</c>, asserting that BOTH were
    /// sent: <c>TradingSignal.Timestamp</c> was hashed into the idempotency key, so a signal rebuilt for the
    /// same order never matched. Flipped by plan step 2 (2026-11-28): the key ignores the construction time.
    /// </summary>
    [TestMethod]
    public async Task Idempotency_ARebuiltDuplicateBuyIsNotSentTwice()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        var built = new DateTime(2026, 11, 30, 5, 0, 0, DateTimeKind.Utc);

        var first = await h.Manager.ExecuteGroupsAsync([[Buy(built)]], "same-key");
        // Rebuilt a millisecond later, as the next pass would build it. Nothing else differs.
        var second = await h.Manager.ExecuteGroupsAsync([[Buy(built.AddMilliseconds(1))]], "same-key");

        Assert.IsTrue(first.Executed, first.Reason);
        Assert.IsTrue(second.IsReplay, second.Reason);
        Assert.AreEqual(1, h.Broker.PlacementCalls);
    }

    /// <summary>
    /// What makes ignoring the construction time safe for retries: a key whose stored outcome is a refusal
    /// of every order is retried as a numbered attempt rather than answered with that refusal for ever. And
    /// once an attempt is ACCEPTED, asking again replays it.
    /// </summary>
    [TestMethod]
    public async Task Idempotency_ARefusedBuyAskedAgainIsANewAttempt_AndAnAcceptedOneIsNot()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        h.Broker.RejectNext(1, "Order Rejected! Chances of Wash Trade against this Order");

        var refused = await h.Manager.ExecuteGroupsAsync([[Buy()]], "same-key");
        var retried = await h.Manager.ExecuteGroupsAsync([[Buy()]], "same-key");
        var askedAgain = await h.Manager.ExecuteGroupsAsync([[Buy()]], "same-key");

        Assert.IsFalse(refused.Groups.Single().Single().Success);
        Assert.IsFalse(retried.IsReplay, retried.Reason);
        Assert.IsTrue(retried.Groups.Single().Single().Success, retried.Reason);
        Assert.IsTrue(askedAgain.IsReplay, askedAgain.Reason);
        Assert.AreEqual(2, h.Broker.PlacementCalls, "the refusal and the retry; never a third");
    }

    /// <summary>
    /// The worker's own retry path. Its key does not change between passes, so without numbered attempts the
    /// boundary would replay the first refusal on every retry and the stop would never be placed.
    /// </summary>
    [TestMethod]
    public async Task ARefusedStopIsPlacedOnALaterPass_NotReplayedAsARefusal()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);
        h.Broker.RejectNext(1, "Order Rejected! Chances of Wash Trade against this Order");

        await h.RunPassAsync();
        Assert.AreEqual(0, h.Broker.RestingSells(Symbol).Count);

        h.Restart();   // skips the worker's in-memory backoff, which is not what is under test here
        await h.RunPassAsync();

        Assert.AreEqual(2, h.Broker.PlacementCalls);
        Assert.AreEqual(100, h.Broker.RestingSells(Symbol).Single().Remaining);
    }

    /// <summary>The control for the key: separately built signals with the same timestamp were always replayed.</summary>
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
    /// Was <c>KnownGap_TheOrderValueCapRefusesAProtectiveStopAboveIt</c>, asserting the stop was refused
    /// before reaching the broker (audit correction, 2026-11-28). Flipped by plan step 2: the value caps
    /// bound BUYs only, so a protective SELL above the cap is placed.
    /// </summary>
    [TestMethod]
    public async Task TheOrderValueCapNoLongerBlocksAProtectiveStopAboveIt()
    {
        using var h = new ProtectionHarness(50_000m, Symbol);
        await h.ProtectHoldingAsync(Symbol, 1_000, 105m, trigger: 100m, limit: 99m);   // 100,000 PKR

        await h.RunPassAsync();

        Assert.AreEqual(1_000, h.Broker.RestingSells(Symbol).Single().Remaining);
        Assert.IsFalse(h.Notifier.Sent.Any(s => s.Topic?.EndsWith("placement-failed") == true));
    }

    [TestMethod]
    public async Task TheOrderValueCapStillRefusesABuyAboveIt()
    {
        using var h = new ProtectionHarness(50_000m, Symbol);

        var buy = await h.Manager.ExecuteGroupsAsync([[Buy()]], "small-buy");   // 2,250 PKR
        var big = await h.Manager.ExecuteGroupsAsync(
            [[new TradingAgent.Models.TradingSignal
            {
                IsSignal = true, Action = "BUY", Symbol = Symbol, Quantity = 1_000,
                OrderType = "LIMIT", EntryPrice = 100m, Confidence = "HIGH"
            }]], "bigger-buy");

        Assert.IsTrue(buy.Executed, buy.Reason);
        Assert.IsFalse(big.Executed);
        StringAssert.Contains(big.Reason, "MaxOrderValuePkr");
    }

    // ── Scenario 8: the trading stop's two levels ───────────────────────────────

    [TestMethod]
    public async Task EntriesOff_ProtectiveStopsKeepBeingPlaced_AndABuyIsRefused()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        h.Options.TradingStop = TradingAgent.Config.TradingStopMode.EntriesOff;
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);

        await h.RunPassAsync();
        var buy = await h.Manager.ExecuteGroupsAsync([[Buy()]], "entry");

        Assert.AreEqual(100, h.Broker.RestingSells(Symbol).Single().Remaining);
        Assert.IsFalse(buy.Executed);
        StringAssert.Contains(buy.Reason, "entries are switched off");
        Assert.AreEqual(1, h.Broker.PlacementCalls);
    }

    [TestMethod]
    public async Task FullStop_NothingIsSent_AndTheOwnerIsToldTheStopIsNotPlaced()
    {
        using var h = new ProtectionHarness(0m, Symbol);
        h.Options.KillSwitch = true;
        await h.ProtectHoldingAsync(Symbol, 100, 230m, trigger: 220m, limit: 217.80m);

        await h.RunPassAsync();
        var buy = await h.Manager.ExecuteGroupsAsync([[Buy()]], "entry");

        Assert.AreEqual(0, h.Broker.PlacementCalls);
        Assert.IsFalse(buy.Executed);
        Assert.IsTrue(h.Notifier.Sent.Any(s => s.Topic?.EndsWith("unauthorised") == true));
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
