using TradingAgent.Trading;

namespace AgentFox.ChannelTests;

/// <summary>
/// <see cref="OrderRejectionOutlook"/> — whether re-sending a refused order could ever work.
///
/// <para>
/// Every wording asserted here is one this integration has measured against the live broker. The
/// DEFAULT is the safety argument: an unrecognised refusal keeps retrying, so a new or misspelled
/// wording costs the old backoff behaviour rather than a wrongly abandoned order.
/// </para>
/// </summary>
[TestClass]
public sealed class OrderRejectionOutlookTests
{
    [TestMethod]
    public void AFundsRefusalKeepsRetryingToday()
    {
        // Measured 2026-09-09. A sale's proceeds are buying power immediately, so an order refused at
        // 09:32 can genuinely be affordable at 11:00 — this is the case backoff exists for.
        Assert.AreEqual(RejectionOutlook.RetryToday, OrderRejectionOutlook.Classify(
            "Rejected before reaching the market: Order[2]:  Insufficient Exposure "
            + "( Amount Remaining = 4902.00 )"));
    }

    [TestMethod]
    public void ASellAvailabilityRefusalKeepsRetryingToday()
    {
        // A stop standing down, or a resting sell cancelling, frees the shares within the session.
        Assert.AreEqual(RejectionOutlook.RetryToday, OrderRejectionOutlook.Classify(
            "Rejected before reaching the market: You cannot sell more than 0 shares of SYS"));
    }

    [TestMethod]
    public void APriceOutsideTodaysBandWaitsForTheNextTradingDay()
    {
        // Measured 2026-09-09, twice: SELL 175 NML at 172 refused Over_Price_Limit. PSX locks the day's
        // price to within 10% of the previous close, so the same price is refused all session.
        Assert.AreEqual(RejectionOutlook.RetryTomorrow, OrderRejectionOutlook.Classify(
            "The exchange rejected this order (Over_Price_Limit). The price 172 is above NML's cap "
            + "of 171.72."));
    }

    [TestMethod]
    public void ADuplicateOrTheAccountLackingTheOrderTypeStopsAltogether()
    {
        // A duplicate is already AT the exchange; re-sending asks to hold it twice. A capability the
        // account does not have is not a condition that clears.
        Assert.AreEqual(RejectionOutlook.Never, OrderRejectionOutlook.Classify(
            "The exchange rejected this order: Duplicated_Order"));
        Assert.AreEqual(RejectionOutlook.Never, OrderRejectionOutlook.Classify(
            "Rejected before reaching the market: Order[7]: MIT Order is not allowed"));
        Assert.AreEqual(RejectionOutlook.Never, OrderRejectionOutlook.Classify(
            "AHL order socket does not recognise order type 'ICEBERG'"));
    }

    [TestMethod]
    public void ASessionLevelNotAllowedStillRetries()
    {
        // The reason the capability match is "Order is not allowed" and not a bare "not allowed": a
        // refusal about the SESSION clears on its own, and stopping on it would abandon an order for
        // the rest of the day over a board that reopens in minutes.
        Assert.AreEqual(RejectionOutlook.RetryToday, OrderRejectionOutlook.Classify(
            "Rejected before reaching the market: Trading is not allowed at this time"));
    }

    [TestMethod]
    public void AnUnrecognisedRefusalKeepsRetrying()
    {
        // The load-bearing default. This broker's undocumented wordings surface in production first,
        // and the safe direction is the one that cannot lose a fill.
        Assert.AreEqual(RejectionOutlook.RetryToday, OrderRejectionOutlook.Classify(
            "Rejected before reaching the market: Some_Wording_Nobody_Has_Seen"));
        Assert.AreEqual(RejectionOutlook.RetryToday, OrderRejectionOutlook.Classify(null));
        Assert.AreEqual(RejectionOutlook.RetryToday, OrderRejectionOutlook.Classify("   "));
    }

    [TestMethod]
    public void ATransientRefusalIsAlwaysRetryableWhateverItsTextSays()
    {
        // A shut board, or the broker serialising placements, says nothing about THIS order. The flag
        // outranks the text so a coincidental substring cannot strand a perfectly good order.
        Assert.AreEqual(RejectionOutlook.RetryToday, OrderRejectionOutlook.Classify(
            "Order Rej: Last order request not Complete", transient: true));
        Assert.AreEqual(RejectionOutlook.RetryToday, OrderRejectionOutlook.Classify(
            "Duplicated_Order", transient: true));
    }

    [TestMethod]
    public void OnlyAStoppedOutlookIsExplained_AndItSaysWhatToDo()
    {
        // RetryToday is explained by the backoff's own reason; a second message there would just
        // contradict it.
        Assert.IsNull(OrderRejectionOutlook.Explain(RejectionOutlook.RetryToday, "anything"));

        var never = OrderRejectionOutlook.Explain(RejectionOutlook.Never, "Duplicated_Order")!;
        StringAssert.Contains(never, "Duplicated_Order", "the broker's own words must survive");
        StringAssert.Contains(never, "cannot clear by waiting");
        StringAssert.Contains(never, "Retry", "stopping the loop never restricts the operator");

        var tomorrow = OrderRejectionOutlook.Explain(
            RejectionOutlook.RetryTomorrow, "Over_Price_Limit")!;
        StringAssert.Contains(tomorrow, "Over_Price_Limit");
        StringAssert.Contains(tomorrow, "next trading date");
        StringAssert.Contains(tomorrow, "Retry");
    }

    [TestMethod]
    public void AnExplanationIsBoundedBecauseItIsWrittenOntoTheIntent()
    {
        // A refusal can carry a whole wire frame, and this string is re-written on every poll and
        // rendered in the dashboard.
        var explained = OrderRejectionOutlook.Explain(
            RejectionOutlook.Never, "Duplicated_Order " + new string('x', 5_000))!;

        Assert.IsTrue(explained.Length < 1_000, $"unbounded reason text: {explained.Length} chars");
        StringAssert.Contains(explained, "…");
    }

    [TestMethod]
    public void EveryOutlookHasAShortLabelForTheActivityLog()
    {
        foreach (var outlook in Enum.GetValues<RejectionOutlook>())
        {
            var label = OrderRejectionOutlook.Label(outlook);
            Assert.IsFalse(string.IsNullOrWhiteSpace(label));
            Assert.IsTrue(label.Length <= 80, $"{outlook} label is too long for an activity row");
        }
    }
}
