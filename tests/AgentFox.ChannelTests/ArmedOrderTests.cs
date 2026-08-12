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
}
