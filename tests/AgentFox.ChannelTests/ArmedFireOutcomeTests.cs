using TradingAgent.Broker;
using TradingAgent.Manager;
using TradingAgent.Models;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// Pins what a triggered armed order becomes once the execution boundary has answered, the one-way
/// price-band clamp, and the arm-time refusal of a trigger that sits on its own reference.
///
/// <para>
/// From the trailing-order review of 2026-11-10. <c>TradingExecutionResult.Executed</c> means the
/// broker ANSWERED: a venue rejection is <c>Executed = true</c> with a failed order inside, and the
/// monitor recorded it as "fired" — a strategy's trailing MARKET exit refused by the venue was spent
/// with nothing resting. And <c>Executed = false</c> covers both a gate's refusal (safe to retry) and
/// an unknown broker outcome (never safe), which were both re-armed.
/// </para>
/// </summary>
[TestClass]
public sealed class ArmedFireOutcomeTests
{
    [TestMethod]
    public void An_accepted_order_is_fired()
    {
        var (disposition, _) = ArmedFireOutcome.For(Answered(Order(success: true)));
        Assert.AreEqual(ArmedFireDisposition.Fired, disposition);
    }

    [TestMethod]
    public void A_broker_rejection_is_not_fired_and_stays_armed()
    {
        var (disposition, reason) = ArmedFireOutcome.For(Answered(
            Order(success: false, "Order Rejected! Chances of Wash Trade against this Order")));

        Assert.AreEqual(ArmedFireDisposition.Rearm, disposition,
            "An exit the venue refused must survive to be tried again; an unrecognised wording costs "
            + "a slower retry, never the exit.");
        StringAssert.Contains(reason, "Wash Trade", "The broker's own words reach the operator.");
    }

    [TestMethod]
    public void A_transient_rejection_stays_armed_whatever_its_text_says()
    {
        var order = Order(success: false, "Duplicated_Order");
        order.TransientRejection = true;

        Assert.AreEqual(ArmedFireDisposition.Rearm, ArmedFireOutcome.For(Answered(order)).Disposition);
    }

    [TestMethod]
    public void A_rejection_that_never_clears_stops_the_order()
    {
        var (disposition, _) = ArmedFireOutcome.For(Answered(Order(success: false, "Duplicated_Order")));
        Assert.AreEqual(ArmedFireDisposition.Stop, disposition);
    }

    [TestMethod]
    public void A_refusal_before_the_broker_stays_armed()
    {
        var refused = TradingExecutionResult.Rejected("v1", "Pre-trade risk validation failed.");
        Assert.AreEqual(ArmedFireDisposition.Rearm, ArmedFireOutcome.For(refused).Disposition);
    }

    [TestMethod]
    public void An_unknown_broker_outcome_is_never_retried()
    {
        // TradingManager's own shape for a broker call that threw: an execution exists, nothing is
        // known about it, and the broker may hold the order.
        var unknown = new TradingExecutionResult(false, false, "exec-1", "v1",
            "Broker outcome unknown; manual reconciliation required: socket closed",
            Array.Empty<IReadOnlyList<OrderResult>>());

        var (disposition, reason) = ArmedFireOutcome.For(unknown);

        Assert.AreEqual(ArmedFireDisposition.Stop, disposition,
            "Re-arming here lets a timer send an order that may already be resting.");
        StringAssert.Contains(reason, "Check the broker");
    }

    [TestMethod]
    public void An_execution_still_in_flight_is_never_retried()
    {
        var inFlight = new TradingExecutionResult(false, true, "exec-1", "v1",
            "An execution with this idempotency key already exists in state 'submitting'.",
            Array.Empty<IReadOnlyList<OrderResult>>());

        Assert.AreEqual(ArmedFireDisposition.Stop, ArmedFireOutcome.For(inFlight).Disposition);
    }

    [TestMethod]
    public void A_replayed_rejection_stays_armed_so_the_next_attempt_is_a_new_submission()
    {
        var replay = Answered(Order(success: false, "Over_Price_Limit")) with { IsReplay = true };
        Assert.AreEqual(ArmedFireDisposition.Rearm, ArmedFireOutcome.For(replay).Disposition);
    }

    // ── The price band never moves a price against the operator ──────────────

    [DataTestMethod]
    [DataRow(true, 105.0, 90.0, 100.0, 100.0, true, "a BUY above the cap pays less at the cap")]
    [DataRow(false, 85.0, 90.0, 100.0, 90.0, true, "a SELL below the lock receives more at the lock")]
    [DataRow(false, 105.0, 90.0, 100.0, 105.0, false, "a SELL above the cap is NOT lowered to it")]
    [DataRow(true, 85.0, 90.0, 100.0, 85.0, false, "a BUY below the lock is NOT raised to it")]
    [DataRow(true, 95.0, 90.0, 100.0, 95.0, false, "inside the band nothing moves")]
    [DataRow(false, 95.0, 90.0, 100.0, 95.0, false, "inside the band nothing moves")]
    public void The_band_clamp_only_moves_a_price_in_the_operators_favour(
        bool isBuy, double requested, double lowerLock, double upperCap, double expected, bool adjusted,
        string why)
    {
        var (price, note) = PriceBandClamp.Apply(
            isBuy, (decimal)requested, (decimal)lowerLock, (decimal)upperCap);

        Assert.AreEqual((decimal)expected, price, why);
        Assert.AreEqual(adjusted, note is not null, why);
        if (isBuy) Assert.IsTrue(price <= (decimal)requested, "A BUY never pays above its own price.");
        else Assert.IsTrue(price >= (decimal)requested, "A SELL never receives below its own price.");
    }

    [TestMethod]
    public void An_unreadable_band_leaves_the_price_alone()
    {
        Assert.AreEqual(105m, PriceBandClamp.Apply(true, 105m, null, null).Price);
        Assert.AreEqual(85m, PriceBandClamp.Apply(false, 85m, 0m, 0m).Price);
    }

    // ── A trigger that sits on its own reference ─────────────────────────────

    [DataTestMethod]
    [DataRow(ArmedTriggerKind.PercentDrop, 12.04, 0.04, true)]
    [DataRow(ArmedTriggerKind.PercentRise, 12.04, 0.04, true)]
    [DataRow(ArmedTriggerKind.PercentDrop, 12.04, 1.0, false)]
    [DataRow(ArmedTriggerKind.PercentRise, 12.04, 1.0, false)]
    [DataRow(ArmedTriggerKind.PercentDrop, 448.75, 0.1, false)]
    public void A_move_smaller_than_one_paisa_is_refused_at_arming(
        ArmedTriggerKind kind, double reference, double percent, bool refused)
    {
        var level = PercentTrigger.Level(kind, (decimal)reference, (decimal)percent)!.Value;
        Assert.AreEqual(refused, PercentTriggerRule.LevelIsAtReference(kind, (decimal)reference, level));
    }

    private static OrderResult Order(bool success, string message = "") => new()
    {
        Success = success, Action = "SELL", Symbol = "CNERGY", Quantity = 100, Message = message
    };

    private static TradingExecutionResult Answered(OrderResult order) => new(
        true, false, "exec-1", "v1",
        order.Success ? "Broker accepted all attempted orders." : "One or more broker orders failed.",
        new[] { (IReadOnlyList<OrderResult>)new[] { order } });
}
