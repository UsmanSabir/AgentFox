using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// Trigger evaluation for armed orders.
///
/// <para>
/// These are the rules that decide whether real money moves while nobody is watching, so the tests
/// lean hard on the cases where firing would be WRONG: a missing price, an expired trigger, one that
/// already fired, an event that did not happen. A false negative delays a trade; a false positive
/// places one nobody asked for.
/// </para>
/// </summary>
[TestClass]
public sealed class ArmedOrderTests
{
    private static readonly DateTime Now = new(2026, 8, 13, 6, 0, 0, DateTimeKind.Utc);

    // ── Price triggers ───────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(300.0, false, "above the level is not a sell-stop trigger")]
    [DataRow(287.03, true, "exactly at the level counts as reached")]
    [DataRow(280.0, true, "through the level")]
    public void PriceBelow_FiresAtOrUnderTheLevel(double last, bool expected, string because)
    {
        var order = Armed(ArmedTriggerKind.PriceBelow, trigger: 287.03m);

        var fired = ArmedOrderEvaluator.ShouldFire(order, (decimal)last, [], Now, out var reason);

        Assert.AreEqual(expected, fired, $"{because} — {reason}");
    }

    [TestMethod]
    [DataRow(700.0, false, "below the level is not a breakout")]
    [DataRow(707.95, true, "exactly at the level counts as reached")]
    [DataRow(720.0, true, "through the level")]
    public void PriceAbove_FiresAtOrOverTheLevel(double last, bool expected, string because)
    {
        var order = Armed(ArmedTriggerKind.PriceAbove, trigger: 707.95m);

        var fired = ArmedOrderEvaluator.ShouldFire(order, (decimal)last, [], Now, out var reason);

        Assert.AreEqual(expected, fired, $"{because} — {reason}");
    }

    [TestMethod]
    public void AMissingPrice_NeverFires()
    {
        var order = Armed(ArmedTriggerKind.PriceBelow, trigger: 287.03m);

        Assert.IsFalse(ArmedOrderEvaluator.ShouldFire(order, null, [], Now, out var reason),
            "A lapsed feed must not be read as the level being hit.");
        StringAssert.Contains(reason, "No live price");
    }

    [TestMethod]
    public void AZeroPrice_NeverFires()
    {
        // A zero from a bad parse would satisfy "at or below" for every sell stop simultaneously —
        // the single most damaging false positive available here.
        var order = Armed(ArmedTriggerKind.PriceBelow, trigger: 287.03m);
        Assert.IsFalse(ArmedOrderEvaluator.ShouldFire(order, 0m, [], Now, out _));
    }

    [TestMethod]
    public void APriceTriggerWithoutALevel_NeverFires()
    {
        var order = Armed(ArmedTriggerKind.PriceBelow, trigger: null);
        Assert.IsFalse(ArmedOrderEvaluator.ShouldFire(order, 100m, [], Now, out var reason));
        StringAssert.Contains(reason, "no usable level");
    }

    // ── Percent triggers ─────────────────────────────────────────────────────
    // "Sell if it drops 3%" rather than "sell at 287.03". The level is DERIVED from a reference and a
    // percentage, so the tests here are about that derivation staying the single source of truth.

    [TestMethod]
    [DataRow(100.0, 3.0, 97.0)]
    [DataRow(133.33, 3.0, 129.33)]   // 129.3301, rounded to the 2 decimals PSX quotes in
    [DataRow(100.10, 5.0, 95.10)]    // 95.095 — the midpoint that separates AwayFromZero from ToEven
    public void PercentDrop_ComputesTheLevelBelowTheReference(
        double reference, double percent, double expected)
    {
        Assert.AreEqual(
            (decimal)expected,
            PercentTrigger.Level(ArmedTriggerKind.PercentDrop, (decimal)reference, (decimal)percent),
            "The level quoted when arming must be the level that fires, to the last paisa.");
    }

    [TestMethod]
    public void PercentRise_ComputesTheLevelAboveTheReference() =>
        Assert.AreEqual(103m, PercentTrigger.Level(ArmedTriggerKind.PercentRise, 100m, 3m));

    [TestMethod]
    [DataRow(0.0, "a zero move has no level")]
    [DataRow(-3.0, "a negative move would invert the trigger's direction")]
    [DataRow(60.0, "past the cap, where a 'stop' is really a market order in disguise")]
    public void AnUnusablePercent_ProducesNoLevel(double percent, string because) =>
        Assert.IsNull(
            PercentTrigger.Level(ArmedTriggerKind.PercentDrop, 100m, (decimal)percent), because);

    [TestMethod]
    public void APercentTriggerWithoutAReference_NeverFires()
    {
        // An order armed while the feed was down has nothing to measure from. Refusing to evaluate is
        // the only safe reading: any default reference invents a level nobody chose.
        var order = Percent(ArmedTriggerKind.PercentDrop, 3m, reference: null);

        Assert.IsFalse(ArmedOrderEvaluator.ShouldFire(order, 1m, [], Now, out var reason));
        StringAssert.Contains(reason, "no usable reference");
    }

    [TestMethod]
    [DataRow(98.0, false, "a 2% fall has not reached the 3% trigger")]
    [DataRow(97.0, true, "exactly at the level counts as reached")]
    [DataRow(90.0, true, "through the level")]
    public void PercentDrop_FiresOnceTheMoveIsComplete(double last, bool expected, string because)
    {
        var order = Percent(ArmedTriggerKind.PercentDrop, 3m, reference: 100m);

        var fired = ArmedOrderEvaluator.ShouldFire(order, (decimal)last, [], Now, out var reason);

        Assert.AreEqual(expected, fired, $"{because} — {reason}");
    }

    [TestMethod]
    public void AStaleStoredLevel_CannotFireAPercentTriggerEarly()
    {
        // trigger_price is a projection of the reference and the percentage, kept only so readers that
        // know nothing about percentages still see a number. If the evaluator trusted it, a trail
        // whose last ratchet failed to persist would fire at a level the operator never set.
        var order = Percent(ArmedTriggerKind.PercentDrop, 3m, reference: 100m) with
        {
            TriggerPrice = 99.99m
        };

        Assert.IsFalse(ArmedOrderEvaluator.ShouldFire(order, 99m, [], Now, out var reason),
            "The reference and the percentage are the truth; the stored level is not.");
        StringAssert.Contains(reason, "97");
    }

    // ── Trailing ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void ANonTrailingOrder_NeverRatchets() =>
        Assert.IsNull(
            ArmedOrderEvaluator.NextTrailReference(
                Percent(ArmedTriggerKind.PercentDrop, 3m, reference: 100m), 120m),
            "A fixed percent trigger measures from where it was armed, whatever happens next.");

    [TestMethod]
    [DataRow(120.0, 120.0, "a new high moves the reference up with it")]
    [DataRow(100.0, null, "an equal price is not a new high")]
    [DataRow(98.0, null, "a lower price must never loosen the trail")]
    public void ATrailingDrop_FollowsTheHighOnly(double last, double? expected, string because)
    {
        var order = Percent(ArmedTriggerKind.PercentDrop, 3m, reference: 100m) with { Trailing = true };

        Assert.AreEqual(
            (decimal?)expected,
            ArmedOrderEvaluator.NextTrailReference(order, (decimal)last),
            because);
    }

    [TestMethod]
    public void ATrailingRise_FollowsTheLowInstead()
    {
        var order = Percent(ArmedTriggerKind.PercentRise, 3m, reference: 100m) with { Trailing = true };

        Assert.AreEqual(90m, ArmedOrderEvaluator.NextTrailReference(order, 90m));
        Assert.IsNull(ArmedOrderEvaluator.NextTrailReference(order, 101m),
            "A breakout entry chases the market DOWN; a higher price is not an improvement.");
    }

    [TestMethod]
    public void ATrailWithNoReference_AdoptsTheFirstPriceItSees()
    {
        var order = Percent(ArmedTriggerKind.PercentDrop, 3m, reference: null) with { Trailing = true };

        Assert.AreEqual(55m, ArmedOrderEvaluator.NextTrailReference(order, 55m),
            "An order armed while the feed was down has to anchor somewhere, and the first real price "
            + "is the only honest choice.");
    }

    [TestMethod]
    public void ATrailOnANonArmedOrder_DoesNotRatchet() =>
        Assert.IsNull(
            ArmedOrderEvaluator.NextTrailReference(
                Percent(ArmedTriggerKind.PercentDrop, 3m, reference: 100m)
                    with { Trailing = true, State = "fired" },
                120m),
            "Ratcheting an order that already fired would rewrite the history of why it did.");

    [TestMethod]
    public void ATrailedLevel_OnlyEverRises()
    {
        // The property that makes a trailing stop a stop. Walked over a realistic session rather than
        // asserted directly, because the failure this guards against is a sequence-dependent one: a
        // pullback between two highs must not drag the level back down with it.
        var order = Percent(ArmedTriggerKind.PercentDrop, 5m, reference: 100m) with { Trailing = true };
        var levels = new List<decimal>();

        foreach (var tick in new[] { 100m, 104m, 103m, 112m, 108m, 130m })
        {
            Assert.IsFalse(ArmedOrderEvaluator.ShouldFire(order, tick, [], Now, out _),
                $"A price of {tick} is above the trailing level and must not fire.");

            if (ArmedOrderEvaluator.NextTrailReference(order, tick) is { } next)
                order = order with { ReferencePrice = next };

            levels.Add(order.EffectiveTriggerPrice!.Value);
        }

        CollectionAssert.AreEqual(
            new[] { 95.00m, 98.80m, 98.80m, 106.40m, 106.40m, 123.50m }, levels,
            "The level tracks each new high and holds through the pullbacks between them.");
    }

    [TestMethod]
    public void ANewHigh_NeverFiresTheTrailItRaises() =>
        Assert.IsFalse(
            ArmedOrderEvaluator.ShouldFire(
                Percent(ArmedTriggerKind.PercentDrop, 3m, reference: 100m) with { Trailing = true },
                200m, [], Now, out _),
            "A price making a new high is by construction on the far side of the level it implies.");

    [TestMethod]
    public void AFallFromTheTrailedHigh_Fires()
    {
        var trailed = Percent(ArmedTriggerKind.PercentDrop, 5m, reference: 130m) with { Trailing = true };

        Assert.IsTrue(ArmedOrderEvaluator.ShouldFire(trailed, 123m, [], Now, out var reason),
            "5% below the trailed high of 130 is 123.50 — the whole point of the trail.");
        StringAssert.Contains(reason, "123.50");

        Assert.IsFalse(ArmedOrderEvaluator.ShouldFire(trailed, 125m, [], Now, out _),
            "A shallower pullback from the high is what the trail exists to sit through.");
    }

    // ── Direction, as the risk engine reads it ───────────────────────────────

    [TestMethod]
    public void PercentTriggers_ReportTheirDirectionForTheRiskEngine()
    {
        // The risk engine judges a stop-limit's geometry from the TRIGGER's direction, not the side —
        // getting this wrong is what refused a legitimate dip-buy live. See StopLimitRule.
        Assert.IsFalse(PercentTrigger.FiresOnRisingPrice(ArmedTriggerKind.PercentDrop));
        Assert.IsTrue(PercentTrigger.FiresOnRisingPrice(ArmedTriggerKind.PercentRise));
        Assert.IsNull(PercentTrigger.FiresOnRisingPrice(ArmedTriggerKind.Event),
            "An event has no price direction to infer.");

        Assert.IsFalse(
            Percent(ArmedTriggerKind.PercentDrop, 3m, reference: 100m).ToSignal().FiresOnRisingPrice,
            "The direction has to survive the projection onto the executable signal.");
    }

    // ── Event triggers ───────────────────────────────────────────────────────

    [TestMethod]
    public void EventTrigger_FiresOnlyOnItsOwnAlertKind()
    {
        var order = Armed(ArmedTriggerKind.Event, alert: AlertKind.SupportBounce);

        Assert.IsTrue(ArmedOrderEvaluator.ShouldFire(
            order, 100m, [AlertKind.SupportBounce], Now, out _));

        Assert.IsFalse(ArmedOrderEvaluator.ShouldFire(
            order, 100m, [AlertKind.TrendFlip, AlertKind.RsiOversold], Now, out var reason),
            "A different alert firing is not this trigger's condition.");
        StringAssert.Contains(reason, "has not been raised");
    }

    [TestMethod]
    public void EventTrigger_DoesNotNeedAPrice()
    {
        // The condition is an event, so a lapsed price feed must not block it.
        var order = Armed(ArmedTriggerKind.Event, alert: AlertKind.SupportBreak);
        Assert.IsTrue(ArmedOrderEvaluator.ShouldFire(
            order, null, [AlertKind.SupportBreak], Now, out _));
    }

    [TestMethod]
    public void NoAlertsAtAll_DoesNotFireAnEventTrigger()
    {
        var order = Armed(ArmedTriggerKind.Event, alert: AlertKind.SupportBounce);
        Assert.IsFalse(ArmedOrderEvaluator.ShouldFire(order, 100m, [], Now, out _));
    }

    // ── State and expiry ─────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("fired")]
    [DataRow("cancelled")]
    [DataRow("expired")]
    [DataRow("failed")]
    [DataRow("firing")]
    public void OnlyAnArmedOrder_CanFire(string state)
    {
        var order = Armed(ArmedTriggerKind.PriceBelow, trigger: 287.03m) with { State = state };

        Assert.IsFalse(ArmedOrderEvaluator.ShouldFire(order, 200m, [], Now, out var reason),
            $"A '{state}' order must never fire — that is what stops a double submission.");
        StringAssert.Contains(reason, "Not armed");
    }

    [TestMethod]
    public void AnExpiredTrigger_DoesNotFireEvenWhenTheConditionIsMet()
    {
        var order = Armed(ArmedTriggerKind.PriceBelow, trigger: 287.03m) with
        {
            ExpiresUtc = Now.AddMinutes(-1)
        };

        Assert.IsFalse(ArmedOrderEvaluator.ShouldFire(order, 200m, [], Now, out var reason),
            "Expiry outranks the condition, or a stale thesis trades on a late tick.");
        StringAssert.Contains(reason, "Expired");
    }

    [TestMethod]
    public void AnUnexpiredTrigger_StillFires()
    {
        var order = Armed(ArmedTriggerKind.PriceBelow, trigger: 287.03m) with
        {
            ExpiresUtc = Now.AddMinutes(1)
        };
        Assert.IsTrue(ArmedOrderEvaluator.ShouldFire(order, 200m, [], Now, out _));
    }

    [TestMethod]
    public void NoExpiry_MeansItStaysArmed()
    {
        var order = Armed(ArmedTriggerKind.PriceBelow, trigger: 287.03m) with { ExpiresUtc = null };
        Assert.IsTrue(ArmedOrderEvaluator.ShouldFire(order, 200m, [], Now.AddYears(1), out _));
    }

    // ── Projection onto an executable order ──────────────────────────────────

    [TestMethod]
    public void ToSignal_CarriesTheStopsTriggerAndLimitSeparately()
    {
        var order = Armed(ArmedTriggerKind.PriceBelow, trigger: 290m) with
        {
            Action = "SELL", Quantity = 500, OrderType = "STOPLOSS",
            Price = 287.03m, LimitPrice = 284.16m
        };

        var signal = order.ToSignal();

        Assert.AreEqual("SELL", signal.Action);
        Assert.AreEqual(500, signal.Quantity);
        Assert.AreEqual("STOPLOSS", signal.OrderType);
        Assert.AreEqual(287.03m, signal.EntryPrice, "The stop's own trigger.");
        Assert.AreEqual(284.16m, signal.LimitPrice, "The limit it works at once triggered.");
        StringAssert.Contains(signal.RawMessage, order.ArmedId,
            "The signal must be traceable back to the armed order that produced it.");
    }

    private static ArmedOrder Armed(
        ArmedTriggerKind kind, decimal? trigger = null, AlertKind? alert = null) => new()
        {
            ArmedId = "a1",
            Symbol = "OGDC",
            TriggerKind = kind,
            TriggerPrice = trigger,
            TriggerAlertKind = alert,
            Action = "SELL",
            Quantity = 100,
            OrderType = "LIMIT",
            Price = 287.03m,
            ArmedUtc = Now.AddHours(-1)
        };

    /// <summary>
    /// A percent-triggered order with its level materialised the way the arm endpoint does it, so a
    /// test that reads <c>TriggerPrice</c> sees what a stored order would.
    /// </summary>
    private static ArmedOrder Percent(
        ArmedTriggerKind kind, decimal percent, decimal? reference) => new()
        {
            ArmedId = "a1",
            Symbol = "OGDC",
            TriggerKind = kind,
            TriggerPercent = percent,
            ReferencePrice = reference,
            TriggerPrice = PercentTrigger.Level(kind, reference, percent),
            Action = kind == ArmedTriggerKind.PercentDrop ? "SELL" : "BUY",
            Quantity = 100,
            OrderType = "MARKET",
            ArmedUtc = Now.AddHours(-1)
        };
}
