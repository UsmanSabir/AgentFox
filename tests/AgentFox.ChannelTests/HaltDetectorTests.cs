using TradingAgent.Market;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// <see cref="HaltDetector"/>: when the agent believes the market is halted, reopened, settled or unknown (plan step 5).
/// Every source is optional; the index rule is the one that does not depend on an unmeasured broker token.
/// </summary>
[TestClass]
public sealed class HaltDetectorTests
{
    // 11:00 PKT on a trading day; the session opened at 09:32.
    private static readonly DateTime Now = new(2026, 11, 30, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Opened = new(2026, 11, 30, 4, 32, 0, DateTimeKind.Utc);
    private static readonly HaltOptions Options = new();

    private static readonly StateObservation None = new(null, null);

    private static HaltInputs In(StateObservation? broker = null, StateObservation? analytics = null,
        decimal? kse30 = null, DateTime? tick = null, bool open = true, DateTime? now = null, DateTime? opened = null) =>
        new(now ?? Now, open, opened ?? Opened, broker ?? None, analytics ?? None, new IndexMove(kse30, tick ?? (kse30 is null ? null : Now.AddMinutes(-1))));

    private static StateObservation Broker(string token, TimeSpan? ago = null) => new(token, Now - (ago ?? TimeSpan.FromSeconds(10)));

    private static MarketPhaseReading Was(MarketPhase phase, DateTime? since = null, bool indexSpent = false) =>
        new(phase, since ?? Now.AddMinutes(-5), "test", "previous", new DateOnly(2026, 11, 30), indexSpent);

    private static MarketPhaseReading Eval(HaltInputs input, MarketPhaseReading? previous = null) =>
        HaltDetector.Evaluate(input, previous ?? Was(MarketPhase.Open), Options);

    [TestMethod]
    public void OutsideASessionTheMarketIsClosedWhateverTheSourcesSay()
    {
        Assert.AreEqual(MarketPhase.Closed, Eval(In(Broker("HALT"), kse30: -7m, open: false)).Phase);
    }

    [TestMethod]
    public void InsideASessionWithNothingUnusualTheMarketIsOpen()
    {
        Assert.AreEqual(MarketPhase.Open, Eval(In(Broker("OPN"), kse30: -1.2m)).Phase);
    }

    [TestMethod]
    public void AFreshBrokerHaltReadingStartsAHalt()
    {
        foreach (var token in new[] { "HALT", "Halted", "SUS", "Suspended", "Break" })
        {
            var reading = Eval(In(Broker(token)));
            Assert.AreEqual(MarketPhase.Halted, reading.Phase, token);
            Assert.AreEqual("broker", reading.Source);
        }
    }

    [TestMethod]
    public void ABreakJustAfterTheOpenIsTheScheduledBreakRunningLate_NotAHalt()
    {
        Assert.AreEqual(MarketPhase.Open, Eval(In(Broker("Break"), opened: Now.AddMinutes(-1))).Phase);
    }

    [TestMethod]
    public void AStaleBrokerReadingIsNotEvidence()
    {
        Assert.AreEqual(MarketPhase.Open, Eval(In(Broker("HALT", ago: TimeSpan.FromMinutes(10)))).Phase);
    }

    [TestMethod]
    public void TheAnalyticsFeedSuspendedStartsAHalt()
    {
        Assert.AreEqual(MarketPhase.Halted, Eval(In(analytics: new StateObservation("SUS", Now.AddSeconds(-30)))).Phase);
    }

    [TestMethod]
    public void TheKse30PastFivePercentEitherWayStartsAHalt()
    {
        var down = Eval(In(kse30: -5.2m));
        Assert.AreEqual(MarketPhase.Halted, down.Phase);
        Assert.AreEqual("index", down.Source);
        Assert.IsTrue(down.IndexHaltSpent);

        Assert.AreEqual(MarketPhase.Halted, Eval(In(kse30: 5.0m)).Phase, "both directions");
        Assert.AreEqual(MarketPhase.Open, Eval(In(kse30: -4.9m)).Phase);
    }

    [TestMethod]
    public void YesterdaysIndexMoveIsNotToday()
    {
        Assert.AreEqual(MarketPhase.Open, Eval(In(kse30: -6m, tick: Now.AddDays(-1))).Phase);
    }

    /// <summary>Owner Q6: a fresh broker reading outranks the index, and the conflict is reported.</summary>
    [TestMethod]
    public void ABrokerOpenReadingTakenAfterTheIndexTickOutranksTheIndex_AndTheConflictIsReported()
    {
        var reading = Eval(In(Broker("OPN", ago: TimeSpan.FromSeconds(5)), kse30: -6m, tick: Now.AddSeconds(-30)));

        Assert.AreEqual(MarketPhase.Open, reading.Phase);
        Assert.IsNotNull(reading.Conflict);
    }

    [TestMethod]
    public void ABrokerReadingOlderThanTheIndexTickDoesNotOutrankIt()
    {
        Assert.AreEqual(MarketPhase.Halted,
            Eval(In(Broker("OPN", ago: TimeSpan.FromSeconds(90)), kse30: -6m, tick: Now.AddSeconds(-30))).Phase);
    }

    [TestMethod]
    public void AHaltEndsWhenTheBrokerOrTheAnalyticsFeedSaysOpen()
    {
        Assert.AreEqual(MarketPhase.ReopenSettling, Eval(In(Broker("OPN")), Was(MarketPhase.Halted)).Phase);
        Assert.AreEqual(MarketPhase.ReopenSettling,
            Eval(In(analytics: new StateObservation("OPN", Now.AddSeconds(-20))), Was(MarketPhase.Halted)).Phase);
    }

    [TestMethod]
    public void TheIndexCannotEndAHalt_ItsMoveOutlastsIt()
    {
        Assert.AreEqual(MarketPhase.Halted, Eval(In(kse30: -1m), Was(MarketPhase.Halted)).Phase);
    }

    [TestMethod]
    public void AHaltNothingConfirmsEndingBecomesUnknownAfterTheMaximum()
    {
        var reading = Eval(In(), Was(MarketPhase.Halted, since: Now.AddMinutes(-91)));

        Assert.AreEqual(MarketPhase.Unknown, reading.Phase);
        StringAssert.Contains(reading.Reason, "Acting normally");
    }

    [TestMethod]
    public void TheReopenSettlesIntoOpenAfterTheSettlingMinutes_OrHaltsAgain()
    {
        Assert.AreEqual(MarketPhase.ReopenSettling, Eval(In(Broker("OPN")), Was(MarketPhase.ReopenSettling, Now.AddMinutes(-4))).Phase);
        Assert.AreEqual(MarketPhase.Open, Eval(In(Broker("OPN")), Was(MarketPhase.ReopenSettling, Now.AddMinutes(-11))).Phase);
        Assert.AreEqual(MarketPhase.Halted, Eval(In(Broker("HALT")), Was(MarketPhase.ReopenSettling, Now.AddMinutes(-4))).Phase);
    }

    [TestMethod]
    public void AfterAnIndexHaltTheSameMoveDoesNotHaltAgainThatSession()
    {
        Assert.AreEqual(MarketPhase.Open, Eval(In(kse30: -6m), Was(MarketPhase.Open, indexSpent: true)).Phase);
    }

    [TestMethod]
    public void ANewSessionForgetsYesterdaysIndexHalt()
    {
        var yesterday = Was(MarketPhase.Closed, indexSpent: true) with { Session = new DateOnly(2026, 11, 27) };

        Assert.AreEqual(MarketPhase.Halted, Eval(In(kse30: -6m), yesterday).Phase);
    }

    [TestMethod]
    public void UnknownReturnsToOpenOnAnOpenReading()
    {
        Assert.AreEqual(MarketPhase.Open, Eval(In(Broker("OPN")), Was(MarketPhase.Unknown)).Phase);
        Assert.AreEqual(MarketPhase.Unknown, Eval(In(), Was(MarketPhase.Unknown)).Phase);
    }

    // ── The armed-order monitor's hold ────────────────────────────────────────

    private static readonly ArmedOrder Backstop = new()
    {
        ArmedId = "b1", Symbol = "PPL", TriggerKind = ArmedTriggerKind.PriceBelow, TriggerPrice = 220m,
        Action = "SELL", Quantity = 100, OrderType = "STOPLOSS", ProtectiveStopId = "s1"
    };

    private static string? Hold(MarketPhase phase, ArmedOrder order, decimal? price) =>
        TradingAgent.Watchlist.WatchlistMonitorWorker.HaltHoldReason(Was(phase), order, price, 5m, 10);

    [TestMethod]
    public void NothingFiresWhileHalted()
    {
        Assert.IsNotNull(Hold(MarketPhase.Halted, Backstop, 200m));
        Assert.IsNotNull(Hold(MarketPhase.Halted, Backstop with { ProtectiveStopId = null, Action = "BUY" }, 200m));
    }

    [TestMethod]
    public void AfterTheReopenABackstopHoldsUnlessBelowTheCatastrophicLevel()
    {
        Assert.IsNotNull(Hold(MarketPhase.ReopenSettling, Backstop, 215m), "below the trigger, above 209");
        Assert.IsNotNull(Hold(MarketPhase.ReopenSettling, Backstop, null), "an unknown price cannot be shown below");
        Assert.IsNull(Hold(MarketPhase.ReopenSettling, Backstop, 208m), "below trigger × 0.95 = 209: it fires");
    }

    [TestMethod]
    public void AfterTheReopenOtherArmedOrdersAreNotHeld()
    {
        Assert.IsNull(Hold(MarketPhase.ReopenSettling, Backstop with { ProtectiveStopId = null }, 215m));
        Assert.IsNull(Hold(MarketPhase.Open, Backstop, 215m));
    }
}
