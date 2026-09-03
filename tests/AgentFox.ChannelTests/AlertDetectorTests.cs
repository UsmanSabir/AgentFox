using TradingAgent.Analysis;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// The monitor's detection rules. These matter more than most tests here: a detector that cries wolf
/// gets muted, and a muted monitor is worth nothing. So the assertions are mostly about what must
/// NOT fire — standing conditions, wicks through a level, and the first sight of a symbol.
/// </summary>
[TestClass]
public sealed class AlertDetectorTests
{
    private static readonly MonitorThresholds Thresholds = new()
    {
        ConfirmPasses = 2,
        BreakBufferPercent = 0.5m,
        VolumeConfirmRatio = 1.3m,
        RsiOversold = 35m,
        RsiOverbought = 70m
    };

    // ── The three guards ─────────────────────────────────────────────────────

    [TestMethod]
    public void FirstSightOfASymbol_FiresNothing()
    {
        var seed = AlertDetector.Seed("OGDC");
        var snapshot = Snapshot(setup: TradeSetup.AvoidBreakdown, zone: PriceZone.AtSupport, rsi: 20m);

        var result = Pass(seed, snapshot, null, Thresholds);

        Assert.AreEqual(0, result.Fired.Count,
            "A cold start must record state silently; otherwise a restart alerts on every standing "
            + "condition at once.");
        Assert.IsFalse(result.NextState.IsNew);
        Assert.AreEqual(TradeSetup.AvoidBreakdown, result.NextState.Setup);
    }

    [TestMethod]
    public void ACondition_MustHoldForConfirmPasses_BeforeItFires()
    {
        var state = Established(zone: PriceZone.AtSupport, setup: TradeSetup.Wait);
        var bouncing = Snapshot(
            setup: TradeSetup.BuyAtSupport, zone: PriceZone.AtSupport, upDays: 1, support: 95m);

        var first = Pass(state, bouncing, null, Thresholds);
        Assert.AreEqual(0, first.Fired.Count(a => a.Kind == AlertKind.SupportBounce),
            "One pass is not confirmation — price oscillating on a level would fire every tick.");
        Assert.AreEqual(1, first.NextState.Streaks[AlertKind.SupportBounce]);

        var second = Pass(first.NextState, bouncing, null, Thresholds);
        Assert.AreEqual(1, second.Fired.Count(a => a.Kind == AlertKind.SupportBounce),
            "The second consecutive pass confirms it.");
    }

    [TestMethod]
    public void AStandingCondition_FiresOnceNotEveryPass()
    {
        var state = Established(zone: PriceZone.AtSupport, setup: TradeSetup.Wait);
        var bouncing = Snapshot(
            setup: TradeSetup.BuyAtSupport, zone: PriceZone.AtSupport, upDays: 1, support: 95m);

        var pass1 = Pass(state, bouncing, null, Thresholds);
        var pass2 = Pass(pass1.NextState, bouncing, null, Thresholds);
        var pass3 = Pass(pass2.NextState, bouncing, null, Thresholds);
        var pass4 = Pass(pass3.NextState, bouncing, null, Thresholds);

        Assert.AreEqual(1, pass2.Fired.Count(a => a.Kind == AlertKind.SupportBounce));
        Assert.AreEqual(0, pass3.Fired.Count(a => a.Kind == AlertKind.SupportBounce),
            "The situation has not changed, so there is nothing new to say.");
        Assert.AreEqual(0, pass4.Fired.Count(a => a.Kind == AlertKind.SupportBounce));
    }

    [TestMethod]
    public void ALapsedCondition_ResetsTheStreakAndCanFireAgainLater()
    {
        var state = Established(zone: PriceZone.AtSupport, setup: TradeSetup.Wait);
        var bouncing = Snapshot(
            setup: TradeSetup.BuyAtSupport, zone: PriceZone.AtSupport, upDays: 1, support: 95m);
        var quiet = Snapshot(setup: TradeSetup.Wait, zone: PriceZone.MidRange, support: 95m);

        var pass1 = Pass(state, bouncing, null, Thresholds);
        var lapsed = Pass(pass1.NextState, quiet, null, Thresholds);

        Assert.IsFalse(lapsed.NextState.Streaks.ContainsKey(AlertKind.SupportBounce),
            "A condition that stopped holding must reset, or a stale streak would fire on a "
            + "single later pass.");
    }

    // ── Break detection ──────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(99.6, false, "a close barely under the level is a wick, not a break")]
    [DataRow(99.0, true, "clearing the 0.5% buffer below the level is a break")]
    public void SupportBreak_RequiresClearingTheNoiseBuffer(double close, bool expected, string because)
    {
        // The broken level is read from the CURRENT snapshot as overhead resistance: TechnicalAnalyzer
        // reclassifies a support once price closes through it. Reading a remembered level instead
        // would work for exactly one pass and then drift.
        var state = Established(zone: PriceZone.AtSupport, setup: TradeSetup.Wait);
        var snapshot = Snapshot(
            setup: TradeSetup.AvoidBreakdown, zone: PriceZone.AtSupport,
            close: (decimal)close, resistance: 100m, downDays: 2,
            newRangeLow: true, volumeRatio: 2m);

        var pass1 = Pass(state, snapshot, null, Thresholds);
        var pass2 = Pass(pass1.NextState, snapshot, null, Thresholds);

        Assert.AreEqual(expected, pass2.Fired.Any(a => a.Kind == AlertKind.SupportBreak), because);
    }

    [TestMethod]
    public void SupportBreak_OnThinVolume_IsNotConfirmed()
    {
        var state = Established(zone: PriceZone.AtSupport, setup: TradeSetup.Wait);
        var thin = Snapshot(
            setup: TradeSetup.AvoidBreakdown, zone: PriceZone.AtSupport,
            close: 92m, resistance: 100m, downDays: 2, newRangeLow: true, volumeRatio: 0.4m);

        var pass1 = Pass(state, thin, null, Thresholds);
        var pass2 = Pass(pass1.NextState, thin, null, Thresholds);

        Assert.IsFalse(pass2.Fired.Any(a => a.Kind == AlertKind.SupportBreak),
            "A break on thin volume is frequently retraced; reporting it wastes the reader's attention.");
    }

    [TestMethod]
    public void SupportBreak_NeedsPriceStillFallingNotJustALowClose()
    {
        var state = Established(zone: PriceZone.AtSupport, setup: TradeSetup.Wait);
        var stabilising = Snapshot(
            setup: TradeSetup.AvoidBreakdown, zone: PriceZone.AtSupport,
            close: 99m, resistance: 100m, downDays: 0, newRangeLow: true, volumeRatio: 2m);

        var pass1 = Pass(state, stabilising, null, Thresholds);
        var pass2 = Pass(pass1.NextState, stabilising, null, Thresholds);

        Assert.IsFalse(pass2.Fired.Any(a => a.Kind == AlertKind.SupportBreak),
            "A fresh low that is no longer falling is a base, not a break in progress.");
    }

    [TestMethod]
    public void ResistanceBreakout_FiresAboveTheBuffer()
    {
        var state = Established(zone: PriceZone.AtResistance, setup: TradeSetup.Wait);
        // The cleared level sits BELOW price now — a broken resistance becomes support.
        var breakout = Snapshot(
            setup: TradeSetup.Wait, zone: PriceZone.UpperRange,
            close: 101m, support: 100m, upDays: 2, newRangeHigh: true, volumeRatio: 2m);

        var pass1 = Pass(state, breakout, null, Thresholds);
        var pass2 = Pass(pass1.NextState, breakout, null, Thresholds);

        var alert = pass2.Fired.Single(a => a.Kind == AlertKind.ResistanceBreakout);
        Assert.AreEqual(100m, alert.LevelPrice, "The reported level is the one just cleared.");
    }

    // ── Other transitions ────────────────────────────────────────────────────

    [TestMethod]
    public void ResistanceRejection_NeedsPriceTurningDownNotJustProximity()
    {
        var state = Established(zone: PriceZone.AtResistance, setup: TradeSetup.Wait);
        var pressing = Snapshot(
            setup: TradeSetup.SellAtResistance, zone: PriceZone.AtResistance,
            resistance: 105m, downDays: 0);

        var pass1 = Pass(state, pressing, null, Thresholds);
        var pass2 = Pass(pass1.NextState, pressing, null, Thresholds);
        Assert.IsFalse(pass2.Fired.Any(a => a.Kind == AlertKind.ResistanceRejection),
            "Sitting at resistance is not a rejection until price actually turns down.");

        var rejecting = pressing with { ConsecutiveDownDays = 1 };
        var pass3 = Pass(pass2.NextState, rejecting, null, Thresholds);
        var pass4 = Pass(pass3.NextState, rejecting, null, Thresholds);
        Assert.IsTrue(pass4.Fired.Any(a => a.Kind == AlertKind.ResistanceRejection));
    }

    /// <summary>
    /// Edges are visible for exactly one pass — the state they are compared against is rewritten at
    /// the end of every pass. Gating them behind a confirmation streak would not delay them, it would
    /// silence them permanently, which is the bug these three tests exist to prevent regressing.
    /// </summary>
    [TestMethod]
    public void TrendFlip_FiresOnTheCrossAndOnlyOnce()
    {
        var below = Snapshot(sma20: 90m, sma50: 100m);
        var above = Snapshot(sma20: 105m, sma50: 100m);

        var seeded = Pass(AlertDetector.Seed("OGDC"), below, null, Thresholds);
        Assert.AreEqual("below", seeded.NextState.SmaRelation);

        var cross = Pass(seeded.NextState, above, null, Thresholds);
        Assert.IsTrue(cross.Fired.Any(a => a.Kind == AlertKind.TrendFlip),
            "An SMA cross exists for one pass; requiring two would mean it never fires at all.");

        // Still above: the cross already happened, so there is nothing new to say.
        var after = Pass(cross.NextState, above, null, Thresholds);
        Assert.IsFalse(after.Fired.Any(a => a.Kind == AlertKind.TrendFlip));
    }

    [TestMethod]
    public void RsiBandCrossing_FiresOnEntryToTheBandOnly()
    {
        var neutral = Snapshot(rsi: 50m);
        var oversold = Snapshot(rsi: 28m);

        var seeded = Pass(AlertDetector.Seed("OGDC"), neutral, null, Thresholds);
        var enter = Pass(seeded.NextState, oversold, null, Thresholds);
        Assert.IsTrue(enter.Fired.Any(a => a.Kind == AlertKind.RsiOversold));

        var stay = Pass(enter.NextState, oversold, null, Thresholds);
        Assert.IsFalse(stay.Fired.Any(a => a.Kind == AlertKind.RsiOversold),
            "Remaining oversold is a condition, not an event.");
    }

    [TestMethod]
    public void SetupChangingIntoABreakdown_IsHighSeverity()
    {
        var state = Established(zone: PriceZone.LowerRange, setup: TradeSetup.BuyAtSupport);
        var breaking = Snapshot(setup: TradeSetup.AvoidBreakdown, zone: PriceZone.AtSupport);

        var alert = Pass(state, breaking, null, Thresholds)
            .Fired.Single(a => a.Kind == AlertKind.SetupChanged);

        Assert.AreEqual(AlertSeverity.High, alert.Severity,
            "Falling into a breakdown is the setup change that actually needs attention.");
    }

    [TestMethod]
    public void WeeklyBreakdown_FiresOnceOnEntry()
    {
        var state = Established(zone: PriceZone.LowerRange, setup: TradeSetup.BuyAtSupport);
        var snapshot = Snapshot(setup: TradeSetup.BuyAtSupport, zone: PriceZone.AtSupport);
        var breakdown = MultiTimeframeAnalyzer.Analyze(
            "OGDC", FallingSeries(), new TechnicalOptions(), 2m);

        Assert.IsTrue(breakdown.WeeklyBreakdown,
            "Test fixture must actually produce a weekly breakdown for this to mean anything.");

        var first = Pass(state, snapshot, breakdown, Thresholds);
        var alert = first.Fired.Single(a => a.Kind == AlertKind.WeeklyBreakdown);
        Assert.AreEqual(AlertSeverity.Critical, alert.Severity);

        var second = Pass(first.NextState, snapshot, breakdown, Thresholds);
        Assert.IsFalse(second.Fired.Any(a => a.Kind == AlertKind.WeeklyBreakdown),
            "Still broken down is not news; entering the breakdown was.");
    }

    /// <summary>
    /// 40 weeks of a relentless decline — the same shape MultiTimeframeTests uses for its weekly
    /// breakdown case. Dates must be Mon–Fri only: bars landing on a weekend do not resample into
    /// weekly bars, and the analyzer then reports no weekly structure at all.
    /// </summary>
    private static List<TradingAgent.Research.PsxCandle> FallingSeries()
    {
        var firstMonday = new DateOnly(2025, 1, 6);
        var bars = new List<TradingAgent.Research.PsxCandle>();
        for (var i = 0; i < 40 * 5; i++)
        {
            var close = 400m - i * 1.5m;
            bars.Add(new TradingAgent.Research.PsxCandle
            {
                Symbol = "OGDC",
                Date = firstMonday.AddDays(i / 5 * 7 + i % 5),
                Open = close,
                High = close + 1.5m,
                Low = close - 1.5m,
                Close = close,
                Volume = 100_000
            });
        }
        return bars;
    }

    [TestMethod]
    public void AlertsCarryTheEvidenceAndTheLiveBarCaveat()
    {
        var state = Established(zone: PriceZone.AtSupport, setup: TradeSetup.Wait);
        var bouncing = Snapshot(
            setup: TradeSetup.BuyAtSupport, zone: PriceZone.AtSupport, upDays: 2,
            support: 95m, usesLiveBar: true) with
        {
            Reasons = ["Last 96 is 1.05% above nearest support 95."]
        };

        var pass1 = Pass(state, bouncing, null, Thresholds);
        var alert = Pass(pass1.NextState, bouncing, null, Thresholds)
            .Fired.Single(a => a.Kind == AlertKind.SupportBounce);

        Assert.AreEqual(95m, alert.LevelPrice);
        Assert.IsTrue(alert.FromLiveBar,
            "An alert off a forming bar must say so — the trigger can still un-happen by the close.");
        Assert.AreEqual(1, alert.Reasons.Count,
            "Evidence is snapshotted so the alert stays explicable after the market moves on.");
        StringAssert.Contains(alert.Summary, "95");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // ── Confirmation is a DURATION, not a number of passes ───────────────────
    //
    // A live price tick can ask for a pass immediately (premium's PriceTriggerWatcher, min gap 5s, up
    // to 12/min). While confirmation counted passes, that quietly cut the evidence required from ~30
    // seconds to ~5 — and an armed order keyed on the resulting alert places a real order. These pin
    // the property that makes reacting to live data safe: looking more often changes only how SOON a
    // condition is confirmed, never WHETHER it is.

    [TestMethod]
    public void ANudgedPassCannotConfirmFasterThanTheConfirmWindow()
    {
        var state = Established(zone: PriceZone.AtSupport, setup: TradeSetup.Wait);
        var bouncing = Snapshot(
            setup: TradeSetup.BuyAtSupport, zone: PriceZone.AtSupport, upDays: 1, support: 95m);

        var first = PassAt(state, bouncing, T0);
        var nudged = PassAt(first.NextState, bouncing, T0.AddSeconds(5));

        Assert.AreEqual(0, nudged.Fired.Count(a => a.Kind == AlertKind.SupportBounce),
            "five seconds of evidence is not thirty, however promptly it was observed");

        var afterWindow = PassAt(nudged.NextState, bouncing, T0.AddSeconds(30));
        Assert.AreEqual(1, afterWindow.Fired.Count(a => a.Kind == AlertKind.SupportBounce),
            "once the condition really has held for the window, it confirms");
    }

    [TestMethod]
    public void ABurstOfNudgesInsideTheWindowConfirmsNothingAndThenFiresExactlyOnce()
    {
        var state = Established(zone: PriceZone.AtSupport, setup: TradeSetup.Wait);
        var bouncing = Snapshot(
            setup: TradeSetup.BuyAtSupport, zone: PriceZone.AtSupport, upDays: 1, support: 95m);

        var current = PassAt(state, bouncing, T0);
        var firedInsideWindow = 0;
        foreach (var second in new[] { 5, 10, 15, 20, 25 })
        {
            current = PassAt(current.NextState, bouncing, T0.AddSeconds(second));
            firedInsideWindow += current.Fired.Count(a => a.Kind == AlertKind.SupportBounce);
        }

        Assert.AreEqual(0, firedInsideWindow,
            "the rate cap allows 12 nudges a minute; none of them may buy confirmation");

        var crossing = PassAt(current.NextState, bouncing, T0.AddSeconds(31));
        Assert.AreEqual(1, crossing.Fired.Count(a => a.Kind == AlertKind.SupportBounce));

        var after = PassAt(crossing.NextState, bouncing, T0.AddSeconds(36));
        Assert.AreEqual(0, after.Fired.Count(a => a.Kind == AlertKind.SupportBounce),
            "and it stays fired once, exactly as pass-counting behaved");
    }

    [TestMethod]
    public void ALiveTickConfirmsSoonerThanTheNextScheduledPassWould()
    {
        // The point of the whole exercise. On a 30s schedule the second pass lands at T+60 and the
        // alert waits for it; a tick arriving at T+31 confirms then. Same evidence, half the latency.
        var state = Established(zone: PriceZone.AtSupport, setup: TradeSetup.Wait);
        var bouncing = Snapshot(
            setup: TradeSetup.BuyAtSupport, zone: PriceZone.AtSupport, upDays: 1, support: 95m);

        var first = PassAt(state, bouncing, T0);
        var onTick = PassAt(first.NextState, bouncing, T0.AddSeconds(31));

        Assert.AreEqual(1, onTick.Fired.Count(a => a.Kind == AlertKind.SupportBounce));
    }

    [TestMethod]
    public void ALapsedConditionRestartsItsClockRatherThanKeepingCredit()
    {
        // A condition that held 25s, lapsed, then returned must serve the full window again — otherwise
        // a flickering level accumulates credit across gaps and confirms on no continuous evidence.
        var state = Established(zone: PriceZone.AtSupport, setup: TradeSetup.Wait);
        var bouncing = Snapshot(
            setup: TradeSetup.BuyAtSupport, zone: PriceZone.AtSupport, upDays: 1, support: 95m);
        var quiet = Snapshot(setup: TradeSetup.Wait, zone: PriceZone.MidRange, support: 95m);

        var held = PassAt(state, bouncing, T0);
        held = PassAt(held.NextState, bouncing, T0.AddSeconds(25));
        var lapsed = PassAt(held.NextState, quiet, T0.AddSeconds(30));
        var returned = PassAt(lapsed.NextState, bouncing, T0.AddSeconds(35));

        Assert.IsFalse(lapsed.NextState.HeldSince.ContainsKey(AlertKind.SupportBounce),
            "the lapse must drop the clock, which is what re-arms the condition");
        Assert.AreEqual(0, returned.Fired.Count(a => a.Kind == AlertKind.SupportBounce),
            "returning after a lapse starts the window again");

        var confirmed = PassAt(returned.NextState, bouncing, T0.AddSeconds(66));
        Assert.AreEqual(1, confirmed.Fired.Count(a => a.Kind == AlertKind.SupportBounce),
            "and confirms a full window after it came back, not after the earlier partial hold");
    }

    /// <summary>
    /// One monitoring pass, thirty seconds after the state it is handed — the production
    /// <c>Monitor.IntervalSeconds</c>. Confirmation is measured in TIME now, so a test that ran every
    /// pass at the same instant would prove nothing; chaining the real cadence keeps each existing case
    /// meaning what it did when a pass was the unit.
    /// </summary>
    private static AlertDetection Pass(
        SymbolMonitorState previous,
        TechnicalSnapshot snapshot,
        MultiTimeframeView? multi,
        MonitorThresholds thresholds) =>
        AlertDetector.Detect(previous, snapshot, multi, thresholds, NextPassAt(previous));

    private static DateTime NextPassAt(SymbolMonitorState previous) =>
        (previous.UpdatedUtc == default ? T0 : previous.UpdatedUtc).AddSeconds(30);

    private static readonly DateTime T0 = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>A pass at an EXPLICIT moment, for the cases that are about timing itself.</summary>
    private static AlertDetection PassAt(
        SymbolMonitorState previous, TechnicalSnapshot snapshot, DateTime at) =>
        AlertDetector.Detect(previous, snapshot, null, Thresholds, at);

    private static SymbolMonitorState Established(
        PriceZone zone,
        TradeSetup setup,
        decimal? support = null,
        decimal? resistance = null) => new()
        {
            Symbol = "OGDC",
            Zone = zone,
            Setup = setup,
            Support = support,
            Resistance = resistance,
            SmaRelation = "above",
            RsiBand = "neutral",
            IsNew = false,
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-2)
        };

    private static TechnicalSnapshot Snapshot(
        TradeSetup setup = TradeSetup.Wait,
        PriceZone zone = PriceZone.MidRange,
        decimal close = 96m,
        decimal? support = null,
        decimal? resistance = null,
        int upDays = 0,
        int downDays = 0,
        decimal? rsi = 50m,
        decimal? sma20 = 100m,
        decimal? sma50 = 98m,
        decimal? volumeRatio = 2m,
        bool usesLiveBar = false,
        bool newRangeLow = false,
        bool newRangeHigh = false) => new()
        {
            Symbol = "OGDC",
            Interval = "1D",
            Bars = 60,
            MakesNewRangeLow = newRangeLow,
            MakesNewRangeHigh = newRangeHigh,
            Close = close,
            Setup = setup,
            Zone = zone,
            NearestSupport = support,
            NearestResistance = resistance,
            PercentAboveSupport = support is > 0 ? Math.Round((close - support.Value) / support.Value * 100m, 2) : null,
            PercentBelowResistance = resistance is > 0 ? Math.Round((resistance.Value - close) / close * 100m, 2) : null,
            ConsecutiveUpDays = upDays,
            ConsecutiveDownDays = downDays,
            Rsi14 = rsi,
            Sma20 = sma20,
            Sma50 = sma50,
            VolumeRatio = volumeRatio,
            UsesLiveBar = usesLiveBar
        };
}
