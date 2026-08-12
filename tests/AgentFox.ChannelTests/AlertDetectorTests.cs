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

        var result = AlertDetector.Detect(seed, snapshot, null, Thresholds);

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

        var first = AlertDetector.Detect(state, bouncing, null, Thresholds);
        Assert.AreEqual(0, first.Fired.Count(a => a.Kind == AlertKind.SupportBounce),
            "One pass is not confirmation — price oscillating on a level would fire every tick.");
        Assert.AreEqual(1, first.NextState.Streaks[AlertKind.SupportBounce]);

        var second = AlertDetector.Detect(first.NextState, bouncing, null, Thresholds);
        Assert.AreEqual(1, second.Fired.Count(a => a.Kind == AlertKind.SupportBounce),
            "The second consecutive pass confirms it.");
    }

    [TestMethod]
    public void AStandingCondition_FiresOnceNotEveryPass()
    {
        var state = Established(zone: PriceZone.AtSupport, setup: TradeSetup.Wait);
        var bouncing = Snapshot(
            setup: TradeSetup.BuyAtSupport, zone: PriceZone.AtSupport, upDays: 1, support: 95m);

        var pass1 = AlertDetector.Detect(state, bouncing, null, Thresholds);
        var pass2 = AlertDetector.Detect(pass1.NextState, bouncing, null, Thresholds);
        var pass3 = AlertDetector.Detect(pass2.NextState, bouncing, null, Thresholds);
        var pass4 = AlertDetector.Detect(pass3.NextState, bouncing, null, Thresholds);

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

        var pass1 = AlertDetector.Detect(state, bouncing, null, Thresholds);
        var lapsed = AlertDetector.Detect(pass1.NextState, quiet, null, Thresholds);

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

        var pass1 = AlertDetector.Detect(state, snapshot, null, Thresholds);
        var pass2 = AlertDetector.Detect(pass1.NextState, snapshot, null, Thresholds);

        Assert.AreEqual(expected, pass2.Fired.Any(a => a.Kind == AlertKind.SupportBreak), because);
    }

    [TestMethod]
    public void SupportBreak_OnThinVolume_IsNotConfirmed()
    {
        var state = Established(zone: PriceZone.AtSupport, setup: TradeSetup.Wait);
        var thin = Snapshot(
            setup: TradeSetup.AvoidBreakdown, zone: PriceZone.AtSupport,
            close: 92m, resistance: 100m, downDays: 2, newRangeLow: true, volumeRatio: 0.4m);

        var pass1 = AlertDetector.Detect(state, thin, null, Thresholds);
        var pass2 = AlertDetector.Detect(pass1.NextState, thin, null, Thresholds);

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

        var pass1 = AlertDetector.Detect(state, stabilising, null, Thresholds);
        var pass2 = AlertDetector.Detect(pass1.NextState, stabilising, null, Thresholds);

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

        var pass1 = AlertDetector.Detect(state, breakout, null, Thresholds);
        var pass2 = AlertDetector.Detect(pass1.NextState, breakout, null, Thresholds);

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

        var pass1 = AlertDetector.Detect(state, pressing, null, Thresholds);
        var pass2 = AlertDetector.Detect(pass1.NextState, pressing, null, Thresholds);
        Assert.IsFalse(pass2.Fired.Any(a => a.Kind == AlertKind.ResistanceRejection),
            "Sitting at resistance is not a rejection until price actually turns down.");

        var rejecting = pressing with { ConsecutiveDownDays = 1 };
        var pass3 = AlertDetector.Detect(pass2.NextState, rejecting, null, Thresholds);
        var pass4 = AlertDetector.Detect(pass3.NextState, rejecting, null, Thresholds);
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

        var seeded = AlertDetector.Detect(AlertDetector.Seed("OGDC"), below, null, Thresholds);
        Assert.AreEqual("below", seeded.NextState.SmaRelation);

        var cross = AlertDetector.Detect(seeded.NextState, above, null, Thresholds);
        Assert.IsTrue(cross.Fired.Any(a => a.Kind == AlertKind.TrendFlip),
            "An SMA cross exists for one pass; requiring two would mean it never fires at all.");

        // Still above: the cross already happened, so there is nothing new to say.
        var after = AlertDetector.Detect(cross.NextState, above, null, Thresholds);
        Assert.IsFalse(after.Fired.Any(a => a.Kind == AlertKind.TrendFlip));
    }

    [TestMethod]
    public void RsiBandCrossing_FiresOnEntryToTheBandOnly()
    {
        var neutral = Snapshot(rsi: 50m);
        var oversold = Snapshot(rsi: 28m);

        var seeded = AlertDetector.Detect(AlertDetector.Seed("OGDC"), neutral, null, Thresholds);
        var enter = AlertDetector.Detect(seeded.NextState, oversold, null, Thresholds);
        Assert.IsTrue(enter.Fired.Any(a => a.Kind == AlertKind.RsiOversold));

        var stay = AlertDetector.Detect(enter.NextState, oversold, null, Thresholds);
        Assert.IsFalse(stay.Fired.Any(a => a.Kind == AlertKind.RsiOversold),
            "Remaining oversold is a condition, not an event.");
    }

    [TestMethod]
    public void SetupChangingIntoABreakdown_IsHighSeverity()
    {
        var state = Established(zone: PriceZone.LowerRange, setup: TradeSetup.BuyAtSupport);
        var breaking = Snapshot(setup: TradeSetup.AvoidBreakdown, zone: PriceZone.AtSupport);

        var alert = AlertDetector.Detect(state, breaking, null, Thresholds)
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

        var first = AlertDetector.Detect(state, snapshot, breakdown, Thresholds);
        var alert = first.Fired.Single(a => a.Kind == AlertKind.WeeklyBreakdown);
        Assert.AreEqual(AlertSeverity.Critical, alert.Severity);

        var second = AlertDetector.Detect(first.NextState, snapshot, breakdown, Thresholds);
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

        var pass1 = AlertDetector.Detect(state, bouncing, null, Thresholds);
        var alert = AlertDetector.Detect(pass1.NextState, bouncing, null, Thresholds)
            .Fired.Single(a => a.Kind == AlertKind.SupportBounce);

        Assert.AreEqual(95m, alert.LevelPrice);
        Assert.IsTrue(alert.FromLiveBar,
            "An alert off a forming bar must say so — the trigger can still un-happen by the close.");
        Assert.AreEqual(1, alert.Reasons.Count,
            "Evidence is snapshotted so the alert stays explicable after the market moves on.");
        StringAssert.Contains(alert.Summary, "95");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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
