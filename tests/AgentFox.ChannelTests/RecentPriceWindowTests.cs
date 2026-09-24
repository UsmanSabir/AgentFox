using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// The rolling-window percent trigger: "sell if it drops 3% from its recent high".
///
/// <para>
/// Two things here are easy to get wrong silently. The window is counted in TRADING time, so an
/// overnight gap must contribute nothing and yesterday's late peak must still be inside a 15-minute
/// window at the next open — the owner's carry-over decision. And an empty window must refuse rather
/// than fall back to the arm-time reference, which after a restart may be days old.
/// </para>
/// </summary>
[TestClass]
public sealed class RecentPriceWindowTests
{
    private static readonly DateTime T0 = new(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(5);

    [TestMethod]
    public void TheExtremeCoversOnlyTheWindow()
    {
        var window = new RecentPriceWindow();
        window.Record("OGDC", 110m, T0, MaxGap);                  // t = 0
        for (var m = 1; m <= 20; m++)
            window.Record("OGDC", 100m, T0.AddMinutes(m), MaxGap); // t = 1..20 min

        Assert.AreEqual(100m, window.Extreme("OGDC", 15, highest: true),
            "the 110 peak is 20 trading minutes old and must have aged out of a 15-minute window");
        Assert.AreEqual(110m, window.Extreme("OGDC", 30, highest: true));
        Assert.AreEqual(100m, window.Extreme("OGDC", 30, highest: false));
    }

    [TestMethod]
    public void AnOvernightGapCountsAsNoTradingTime()
    {
        var window = new RecentPriceWindow();
        var close = T0;
        window.Record("OGDC", 100m, close.AddMinutes(-2), MaxGap);
        window.Record("OGDC", 100m, close, MaxGap);

        // Next morning, 17.5 hours later: only the minutes on either side of the close count.
        var open = close.AddHours(17.5);
        window.Record("OGDC", 96m, open, MaxGap);

        Assert.AreEqual(100m, window.Extreme("OGDC", 15, highest: true),
            "yesterday's close is inside a 15-minute trading window at the open");
    }

    [TestMethod]
    public void TicksInOneBucketKeepBothTheirHighAndTheirLow()
    {
        var window = new RecentPriceWindow();
        window.Record("OGDC", 100m, T0, MaxGap);
        window.Record("OGDC", 104m, T0.AddSeconds(2), MaxGap);
        window.Record("OGDC", 97m, T0.AddSeconds(4), MaxGap);

        Assert.AreEqual(104m, window.Extreme("OGDC", 1, highest: true));
        Assert.AreEqual(97m, window.Extreme("OGDC", 1, highest: false));
    }

    [TestMethod]
    public void RetainForgetsSymbolsNothingWatches()
    {
        var window = new RecentPriceWindow();
        window.Record("OGDC", 100m, T0, MaxGap);
        window.Record("PPL", 50m, T0, MaxGap);

        window.Retain(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ppl" });

        Assert.IsNull(window.Extreme("OGDC", 15, highest: true));
        Assert.AreEqual(50m, window.Extreme("PPL", 15, highest: true));
    }

    // ── The evaluator ─────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(98.0, false, "2% off a 100 high is not a 3% fall")]
    [DataRow(97.0, true, "exactly 3% below the recent high")]
    [DataRow(95.0, true, "through it")]
    public void AWindowedDrop_MeasuresFromTheRecentHigh(double last, bool expected, string because)
    {
        // Armed at 90; the stock has since run to 100. Measured from the arm-time price this would
        // need a fall to 87.30 — the defect the window exists to fix.
        var order = WindowedDrop(reference: 90m);

        var fired = ArmedOrderEvaluator.ShouldFire(
            order, (decimal)last, [], T0, out var reason, windowReference: 100m);

        Assert.AreEqual(expected, fired, $"{because} — {reason}");
    }

    [TestMethod]
    public void AnEmptyWindow_NeverFallsBackToTheArmTimeReference()
    {
        // After a restart the window is empty; the stored reference (120) may be days old, and
        // measuring from it would fire on a peak nobody watched.
        var order = WindowedDrop(reference: 120m);

        Assert.IsFalse(ArmedOrderEvaluator.ShouldFire(order, 100m, [], T0, out var reason));
        StringAssert.Contains(reason, "No prices observed");
    }

    [TestMethod]
    public void AWindowedOrderDoesNotTrail()
    {
        Assert.IsNull(ArmedOrderEvaluator.NextTrailReference(WindowedDrop(reference: 90m), 150m));
    }

    [TestMethod]
    public void TheDisplayedLevelFollowsTheWindowAndFallsBackOnlyForDisplay()
    {
        var order = WindowedDrop(reference: 90m);

        Assert.AreEqual(97m, order.EffectiveTriggerPriceFor(100m));
        Assert.AreEqual(87.30m, order.EffectiveTriggerPriceFor(null));
    }

    [TestMethod]
    [DataRow(102.0, false, "2% above a 100 low is not a 3% rise")]
    [DataRow(103.0, true, "exactly 3% above the recent low")]
    public void AWindowedRise_MeasuresFromTheRecentLow(double last, bool expected, string because)
    {
        // Armed at 110; the stock has since fallen to 100. From the arm-time price it would need 113.30.
        var order = WindowedDrop(reference: 110m) with { TriggerKind = ArmedTriggerKind.PercentRise };

        var fired = ArmedOrderEvaluator.ShouldFire(
            order, (decimal)last, [], T0, out var reason, windowReference: 100m);

        Assert.AreEqual(expected, fired, $"{because} — {reason}");
    }

    [TestMethod]
    public void AWindowedLimit_IsPricedAtTheLevelItFiredAt()
    {
        // Sell-into-strength armed at 110 with its limit quoted at 113.30; the window low is now 100,
        // so it fires at 103 and must ask 103 — not a price the stock never reached.
        var order = WindowedDrop(reference: 110m) with
        {
            TriggerKind = ArmedTriggerKind.PercentRise,
            OrderType = "LIMIT",
            Price = 113.30m
        };

        Assert.AreEqual(103m, order.PriceAtFire(100m));
        Assert.AreEqual(113.30m, order.PriceAtFire(null), "no level: keep the stored limit, never send none");
        Assert.AreEqual(113.30m, (order with { TriggerWindowMinutes = null }).PriceAtFire(100m),
            "a fixed-reference limit is untouched");
        Assert.IsNull((order with { OrderType = "MARKET", Price = null }).PriceAtFire(100m),
            "a market order carries no price");
    }

    private static ArmedOrder WindowedDrop(decimal reference) => new()
    {
        ArmedId = "w1",
        Symbol = "OGDC",
        TriggerKind = ArmedTriggerKind.PercentDrop,
        TriggerPercent = 3m,
        ReferencePrice = reference,
        TriggerWindowMinutes = 15,
        TriggerPrice = PercentTrigger.Level(ArmedTriggerKind.PercentDrop, reference, 3m),
        Action = "SELL",
        Quantity = 100,
        OrderType = "MARKET",
        ArmedUtc = T0.AddHours(-1)
    };
}
