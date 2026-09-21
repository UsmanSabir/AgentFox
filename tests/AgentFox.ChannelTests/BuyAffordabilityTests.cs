using TradingAgent.Reconciliation;
using TradingAgent.Risk;

namespace AgentFox.ChannelTests;

/// <summary>
/// Pins the pre-flight BUY refusal on the dashboard order endpoint.
///
/// <para>
/// Written from the 2026-09-21 MWMP incident recorded on <see cref="BuyAffordabilityRule"/>: a BUY
/// worth PKR 4,967.56 went to the broker against PKR 760 of buying power and met
/// <c>Insufficient Exposure ( Amount Remaining = 760.00 )</c>, because nothing on this side of the
/// system had looked. The SELL side had had a confirmer since 2026-09-07 and the BUY side had none.
/// </para>
///
/// <para>
/// The asymmetries are what these pin, not the arithmetic: unknown never refuses, a MARKET buy is
/// skipped rather than guessed at, and a cached figure is only ever a reason to spend a read.
/// </para>
/// </summary>
[TestClass]
public sealed class BuyAffordabilityTests
{
    private static readonly DateTime Now = new(2026, 9, 21, 6, 50, 0, DateTimeKind.Utc);

    /// <summary>The incident's own numbers: 52 MWMP at 95.53 against the 760 the venue reported.</summary>
    private const decimal MwmpOrderValue = 52 * 95.53m;

    [TestMethod]
    public void TheMeasuredRefusal_IsCaughtBeforeItReachesTheBroker()
    {
        var cached = BuyAffordabilityRule.Available(Snapshot(760m), Now, TimeSpan.FromMinutes(30));

        Assert.IsTrue(cached.Known);
        Assert.IsTrue(BuyAffordabilityRule.NeedsBrokerConfirmation(cached, MwmpOrderValue),
            "A figure that cannot fund the order is worth one broker read: the path is about to fail.");
        Assert.IsTrue(BuyAffordabilityRule.MayRefuse(cached, MwmpOrderValue));

        var message = BuyAffordabilityRule.Compose("MWMP", 52, MwmpOrderValue, cached);
        StringAssert.Contains(message, "4,967.56", "The refusal states what the order is worth.");
        StringAssert.Contains(message, "760.00", "...and what the broker says is available.");
        StringAssert.Contains(message, "just now",
            "\"But I just refreshed\" is the first thing the operator thinks; the sentence answers it.");
        StringAssert.Contains(message, "charges",
            "Sizing to exactly the reported figure meets the same refusal, so the screen must say so.");
    }

    [TestMethod]
    public void AnAffordableOrder_CostsNoBrokerRead()
    {
        var cached = BuyAffordabilityRule.Available(Snapshot(50_000m), Now, TimeSpan.FromMinutes(30));

        Assert.IsFalse(BuyAffordabilityRule.NeedsBrokerConfirmation(cached, MwmpOrderValue),
            "Spending five SOAP calls on every affordable BUY is what makes a check like this "
            + "unshippable on an account that allows one session.");
        Assert.IsFalse(BuyAffordabilityRule.MayRefuse(cached, MwmpOrderValue));
    }

    [TestMethod]
    public void UnknownBuyingPower_NeverRefuses()
    {
        // Three separate ways to not know, and all three must run the same way. Invariant 4 says
        // unknown is never zero; here that means unknown never becomes a refusal.
        var noRow = BuyAffordabilityRule.Available(Snapshot(null), Now, TimeSpan.FromMinutes(30));
        var unhealthy = BuyAffordabilityRule.Available(
            new BrokerReconciliationSnapshot(true, false, "broker unreadable", Now)
            { AvailableCashPkr = 10m },
            Now, TimeSpan.FromMinutes(30));
        var stale = BuyAffordabilityRule.Available(
            Snapshot(10m), Now.AddHours(2), TimeSpan.FromMinutes(30));

        foreach (var answer in new[] { noRow, unhealthy, stale })
        {
            Assert.IsFalse(answer.Known);
            Assert.IsFalse(BuyAffordabilityRule.NeedsBrokerConfirmation(answer, MwmpOrderValue),
                "There is nothing to confirm, so there is no read to spend.");
            Assert.IsFalse(BuyAffordabilityRule.MayRefuse(answer, MwmpOrderValue),
                "Refusing on ignorance would block ordinary buying whenever the broker cannot be read.");
        }
    }

    [TestMethod]
    public void AZeroRowAndAMissingRow_AreNotTheSameThing()
    {
        var reportsNone = BuyAffordabilityRule.Available(Snapshot(0m), Now, TimeSpan.FromMinutes(30));
        var reportsNothing = BuyAffordabilityRule.Available(Snapshot(null), Now, TimeSpan.FromMinutes(30));

        Assert.IsTrue(reportsNone.Known, "A broker saying zero has told us something.");
        Assert.IsTrue(BuyAffordabilityRule.MayRefuse(reportsNone, MwmpOrderValue));

        Assert.IsFalse(reportsNothing.Known,
            "A broker that does not report buying power has not said it has none — reading it that "
            + "way would refuse every BUY on the account.");
        Assert.IsFalse(BuyAffordabilityRule.MayRefuse(reportsNothing, MwmpOrderValue));
    }

    [TestMethod]
    public void AMarketBuy_HasNoValueToCheckAndIsSkipped()
    {
        Assert.IsNull(BuyAffordabilityRule.OrderValue(entryPrice: null, quantity: 52),
            "A market buy's fill price is unknowable in advance. Refusing on a number nobody has "
            + "would block the order type least able to supply one.");
        Assert.IsNull(BuyAffordabilityRule.OrderValue(entryPrice: 95.53m, quantity: 0));
        Assert.AreEqual(MwmpOrderValue, BuyAffordabilityRule.OrderValue(95.53m, 52));

        // And a value of zero can never refuse, whatever the buying power says.
        var broke = BuyAffordabilityRule.Available(Snapshot(0m), Now, TimeSpan.FromMinutes(30));
        Assert.IsFalse(BuyAffordabilityRule.MayRefuse(broke, 0m));
    }

    [TestMethod]
    public void AnOrderWorthExactlyTheBuyingPower_IsNotRefusedHere()
    {
        // The broker funds traded value AND its own charges, so this order will very likely still be
        // refused at the venue. It is NOT refused here, because doing so would require core to model
        // a fee schedule -- which it deliberately does not (premium invariant 10a). The near-limit
        // case is a caution beside the cost estimate, which reads the edition's real cost projection.
        var exact = BuyAffordabilityRule.Available(
            Snapshot(MwmpOrderValue), Now, TimeSpan.FromMinutes(30));

        Assert.IsFalse(BuyAffordabilityRule.MayRefuse(exact, MwmpOrderValue));
    }

    private static BrokerReconciliationSnapshot Snapshot(decimal? buyingPower) =>
        new(true, true, "ok", Now) { AvailableCashPkr = buyingPower };
}
