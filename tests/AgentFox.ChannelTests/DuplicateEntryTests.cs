using TradingAgent.Trading;

namespace AgentFox.ChannelTests;

/// <summary>
/// Pins the duplicate-entry warning: what counts as a live instruction, what deliberately does not,
/// and that the match is on symbol and side alone.
///
/// <para>
/// The incident is on <see cref="DuplicateEntryRule"/>. In short: a keep-working BUY of 52 MWMP was
/// refused twice for insufficient exposure and — correctly — backed off rather than giving up. The
/// operator, reading two failures, placed a replacement for 56 shares at a different price. Exposure
/// was freed by an unrelated cancellation, and both filled 42 seconds apart.
/// </para>
/// </summary>
[TestClass]
public sealed class DuplicateEntryTests
{
    [TestMethod]
    public void TheMwmpPair_IsRecognisedDespiteDifferentSizeAndPrice()
    {
        // The live one: 52 at 95.53, backing off after a broker refusal.
        var live = Intent("MWMP", "BUY", quantity: 52, price: 95.53m, state: "active",
            reason: "Insufficient Exposure ( Amount Remaining = 760.00 ); retrying in 2 minute(s).");

        // The replacement the operator typed: a different size at a different price.
        var conflicts = DuplicateEntryRule.FindLiveIntents([live], "MWMP", "BUY");

        Assert.AreEqual(1, conflicts.Count,
            "Matching on price or quantity would have found nothing here and said nothing — which "
            + "is exactly what happened on the day.");

        var message = DuplicateEntryRule.Compose("MWMP", "BUY", conflicts);
        StringAssert.Contains(message, "SECOND entry");
        StringAssert.Contains(message, "on its own",
            "The fact the operator did not have is that the live order keeps placing unattended.");
        StringAssert.Contains(message, "Insufficient Exposure",
            "The broker's own last words are the most useful sentence available here.");
    }

    [TestMethod]
    public void TheOppositeSideAndOtherSymbols_AreNotConflicts()
    {
        PersistentOrderIntent[] intents =
        [
            Intent("MWMP", "SELL", 52, 95.53m),
            Intent("THCCL", "BUY", 57, 84.49m)
        ];

        Assert.AreEqual(0, DuplicateEntryRule.FindLiveIntents(intents, "MWMP", "BUY").Count,
            "Selling a name while buying it is a position being managed, not a duplicate entry.");
    }

    [TestMethod]
    public void TerminalIntents_AreNotConflicts()
    {
        foreach (var state in new[] { "fulfilled", "expired", "cancelled" })
        {
            var done = Intent("MWMP", "BUY", 52, 95.53m, state: state);
            Assert.AreEqual(0, DuplicateEntryRule.FindLiveIntents([done], "MWMP", "BUY").Count,
                $"An intent in '{state}' will never place again, so warning about it is noise.");
        }
    }

    [TestMethod]
    public void AnIntentHeldForAPerson_IsNotAConflict()
    {
        var held = Intent("MWMP", "BUY", 52, 95.53m, state: "attention");

        Assert.AreEqual(0, DuplicateEntryRule.FindLiveIntents([held], "MWMP", "BUY").Count,
            "'attention' places nothing until a person resolves it. Warning about a queue that is "
            + "not moving teaches the operator to dismiss the warning that matters.");
    }

    [TestMethod]
    public void AFullyFilledIntent_IsNotAConflictWhateverItsState()
    {
        // Nothing left to buy, so it cannot add to the position however its state happens to read.
        var spent = Intent("MWMP", "BUY", 52, 95.53m, state: "resting") with { FilledQuantity = 52 };

        Assert.AreEqual(0, DuplicateEntryRule.FindLiveIntents([spent], "MWMP", "BUY").Count);
    }

    [TestMethod]
    public void APartlyFilledIntent_WarnsAboutTheREMAINDER()
    {
        var partial = Intent("MWMP", "BUY", 52, 95.53m, state: "partial") with { FilledQuantity = 20 };

        var conflicts = DuplicateEntryRule.FindLiveIntents([partial], "MWMP", "BUY");
        Assert.AreEqual(1, conflicts.Count);
        Assert.AreEqual(32, conflicts[0].RemainingQuantity,
            "The exposure the operator is about to add to is what is still unplaced, not the "
            + "original size.");
        StringAssert.Contains(conflicts[0].Describe, "20 of 52 already filled");
    }

    private static PersistentOrderIntent Intent(
        string symbol, string action, int quantity, decimal? price,
        string state = "active", string? reason = null) =>
        new()
        {
            IntentId = Guid.NewGuid().ToString("N"),
            Symbol = symbol,
            Action = action,
            Quantity = quantity,
            OrderType = "LIMIT",
            Price = price,
            ExpiresUtc = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc),
            State = state,
            StateReason = reason
        };
}
