using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// <see cref="AttachedStopRule"/> — the one place that decides whether a protective stop may be
/// attached to an entry, shared by the waiting-order and immediate-order paths so they cannot drift.
/// </summary>
[TestClass]
public sealed class AttachedStopRuleTests
{
    [TestMethod]
    public void AQualifyingStopIsAcceptedAndKeepsTheOperatorsOwnLevels()
    {
        var plan = AttachedStopRule.Validate("BUY", stopTrigger: 95m, stopLimit: 93m, entryPrice: 100m);

        Assert.IsTrue(plan.Ok);
        Assert.AreEqual(95m, plan.StopTrigger);
        Assert.AreEqual(93m, plan.StopLimit);
    }

    [TestMethod]
    public void AnOmittedLimitDefaultsJustBelowTheTrigger()
    {
        // A limit set exactly AT its trigger routinely misses the move that triggered it, so the
        // default has to be strictly below rather than equal.
        var plan = AttachedStopRule.Validate("BUY", 95m, stopLimit: null, entryPrice: 100m);

        Assert.IsTrue(plan.Ok);
        Assert.AreEqual(94.05m, plan.StopLimit);
        Assert.IsTrue(plan.StopLimit < plan.StopTrigger);
    }

    [TestMethod]
    public void OnlyABuyMayCarryAStop()
    {
        // A stop on a SELL would arm a second sell of stock the first is already disposing of.
        Assert.AreEqual("stop_requires_buy",
            AttachedStopRule.Validate("SELL", 95m, 93m, 100m).ErrorCode);
        Assert.AreEqual("stop_requires_buy",
            AttachedStopRule.Validate(null, 95m, 93m, 100m).ErrorCode);
    }

    [TestMethod]
    public void AStopAtOrAboveAKnownEntryIsRefused()
    {
        // It would fire the moment it activated rather than protect anything.
        Assert.AreEqual("stop_above_entry",
            AttachedStopRule.Validate("BUY", 100m, 99m, entryPrice: 100m).ErrorCode);
        Assert.AreEqual("stop_above_entry",
            AttachedStopRule.Validate("BUY", 101m, 99m, entryPrice: 100m).ErrorCode);
    }

    [TestMethod]
    public void AnUnknownEntryPriceSkipsThatCheckRatherThanGuessingAtOne()
    {
        // A MARKET buy's fill price is unknowable in advance. Refusing on a number nobody has would
        // block the order type most likely to need protecting; the dialog says so instead.
        var plan = AttachedStopRule.Validate("BUY", 500m, 495m, entryPrice: null);

        Assert.IsTrue(plan.Ok);
        Assert.AreEqual(500m, plan.StopTrigger);
    }

    [TestMethod]
    public void AMissingOrInvertedLevelIsRefusedWithItsOwnCode()
    {
        Assert.AreEqual("invalid_stop_trigger",
            AttachedStopRule.Validate("BUY", null, 93m, 100m).ErrorCode);
        Assert.AreEqual("invalid_stop_trigger",
            AttachedStopRule.Validate("BUY", 0m, 93m, 100m).ErrorCode);
        Assert.AreEqual("invalid_stop_limit",
            AttachedStopRule.Validate("BUY", 95m, stopLimit: 96m, entryPrice: 100m).ErrorCode);
        Assert.AreEqual("invalid_stop_limit",
            AttachedStopRule.Validate("BUY", 95m, stopLimit: 0m, entryPrice: 100m).ErrorCode);
    }

    [TestMethod]
    public void ARefusalAlwaysCarriesAMessageAndNeverPartialLevels()
    {
        // The endpoints return ErrorCode and Message straight to the operator, and must never read
        // levels off a refused plan.
        foreach (var refused in new[]
                 {
                     AttachedStopRule.Validate("SELL", 95m, 93m, 100m),
                     AttachedStopRule.Validate("BUY", null, 93m, 100m),
                     AttachedStopRule.Validate("BUY", 100m, 99m, 100m),
                     AttachedStopRule.Validate("BUY", 95m, 96m, 100m)
                 })
        {
            Assert.IsFalse(refused.Ok);
            Assert.IsFalse(string.IsNullOrWhiteSpace(refused.Message));
            Assert.AreEqual(0m, refused.StopTrigger);
            Assert.AreEqual(0m, refused.StopLimit);
        }
    }

    [TestMethod]
    public void ATakeProfitMustBelongToABuyWithAStopAndSitAboveTheEntry()
    {
        var accepted = AttachedTakeProfitRule.Validate(
            "BUY", targetPrice: 110m, entryPrice: 100m, hasAttachedStop: true);

        Assert.IsNull(accepted.ErrorCode);
        Assert.AreEqual(110m, accepted.Price);
        Assert.AreEqual("target_requires_buy",
            AttachedTakeProfitRule.Validate("SELL", 110m, 100m, true).ErrorCode);
        Assert.AreEqual("target_requires_stop",
            AttachedTakeProfitRule.Validate("BUY", 110m, 100m, false).ErrorCode);
        Assert.AreEqual("invalid_take_profit",
            AttachedTakeProfitRule.Validate("BUY", 0m, 100m, true).ErrorCode);
        Assert.AreEqual("target_not_above_entry",
            AttachedTakeProfitRule.Validate("BUY", 100m, 100m, true).ErrorCode);
    }
}
