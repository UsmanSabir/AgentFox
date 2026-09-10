using TradingAgent.Market;
using TradingAgent.Trading;

namespace AgentFox.ChannelTests;

/// <summary>
/// When a stop attached to an IMMEDIATE entry may conclude the entry never filled.
///
/// <para>
/// <b>This is a regression test for a protection gap, not a hypothetical.</b> Measured 2026-09-09,
/// THCCL: an operator bought 57 shares immediately at 84.60 with a stop attached at 82.91. The order
/// filled in SEVENTY MILLISECONDS — <c>ORDER_EXE ... Bought Order 0010TL2EYU00ZIZ5 57 THCCL REG Market
/// filled at 84.49 - Remaining 00</c> — so it never rested, and no fill had reached the ledger yet.
/// Eighty seconds later the worker read "not in the outstanding book, no fill recorded" and closed the
/// stop as never filled.
/// </para>
///
/// <para>
/// An instantly-filled order is absent from the outstanding book for exactly the same reason a lapsed
/// one is, so the book cannot tell them apart. That made "no fill recorded" ignorance rather than a
/// measurement — invariant 4 — and it ran in the UNPROTECTED direction: real shares, no stop. Only a
/// second stop from an unrelated armed entry happened to cover the position, at a level 7.5% wider
/// than the operator asked for. Without that coincidence the position would have been bare.
/// </para>
/// </summary>
[TestClass]
public sealed class ImmediateEntryStopFillTests
{
    private static readonly DateTime PktNow = new(2026, 9, 9, 14, 34, 7, DateTimeKind.Unspecified);

    /// <summary>The stop from the incident: created 14:32:47 PKT, i.e. 09:32:47 UTC.</summary>
    private static readonly DateTime CreatedUtc = new(2026, 9, 9, 9, 32, 47, DateTimeKind.Utc);

    [TestMethod]
    public void TheEntrysSessionIsNotOverWhileTheMarketIsStillOpen()
    {
        // The incident, exactly. Open at 14:34 with the entry placed at 14:32, so nothing may be
        // concluded from the absence of a fill record.
        Assert.IsFalse(ProtectiveStopWorker.EntrySessionIsOver(
            new MarketStatus(IsOpen: true, PktNow, "open"), CreatedUtc));
    }

    [TestMethod]
    public void TheEntrysSessionIsOverOnceTodaysSessionHasClosed()
    {
        // After the close, with the next opening on a LATER date: the DAY order is genuinely gone.
        Assert.IsTrue(ProtectiveStopWorker.EntrySessionIsOver(
            new MarketStatus(
                IsOpen: false,
                new DateTime(2026, 9, 9, 16, 0, 0),
                "closed",
                NextOpenPkt: new DateTime(2026, 9, 10, 9, 32, 0)),
            CreatedUtc));
    }

    [TestMethod]
    public void AMidSessionBreakIsNotTheEndOfTheSession()
    {
        // Friday's lunch break: closed right now, but the next opening is the SAME date, so the order
        // may still fill after it. Closing here would retire a stop over a position still being bought.
        Assert.IsFalse(ProtectiveStopWorker.EntrySessionIsOver(
            new MarketStatus(
                IsOpen: false,
                new DateTime(2026, 9, 9, 13, 0, 0),
                "break",
                NextOpenPkt: new DateTime(2026, 9, 9, 14, 30, 0)),
            CreatedUtc));
    }

    [TestMethod]
    public void APriorDatesEntryIsOverWhateverTodaysSessionIsDoing()
    {
        // A stop still pending_fill from yesterday: the venue cleared that book at the close, so it
        // cannot fill now even mid-session today.
        var yesterday = CreatedUtc.AddDays(-1);

        Assert.IsTrue(ProtectiveStopWorker.EntrySessionIsOver(
            new MarketStatus(IsOpen: true, PktNow, "open"), yesterday));
        Assert.IsTrue(ProtectiveStopWorker.EntrySessionIsOver(
            new MarketStatus(IsOpen: false, PktNow, "closed"), yesterday));
    }

    [TestMethod]
    public void AClosedMarketWithNoForwardScheduleCountsAsOver()
    {
        // An unreadable forward schedule must not keep a dead stop alive for ever. Note this only
        // applies while the market is CLOSED -- an open market is never "over" regardless.
        Assert.IsTrue(ProtectiveStopWorker.EntrySessionIsOver(
            new MarketStatus(IsOpen: false, PktNow, "closed", NextOpenPkt: null), CreatedUtc));
        Assert.IsFalse(ProtectiveStopWorker.EntrySessionIsOver(
            new MarketStatus(IsOpen: true, PktNow, "open", NextOpenPkt: null), CreatedUtc));
    }

    [TestMethod]
    public void TheEntryDatePktIsTakenFromUtcPlusFive()
    {
        // PKT is UTC+5 with no daylight saving. An entry placed at 20:00 UTC belongs to the NEXT PKT
        // date, so reading its date off the UTC stamp would call it a prior day's order and close it.
        var lateUtc = new DateTime(2026, 9, 9, 20, 0, 0, DateTimeKind.Utc);   // 2026-09-10 01:00 PKT
        var nextMorningPkt = new DateTime(2026, 9, 10, 10, 0, 0);

        Assert.IsFalse(ProtectiveStopWorker.EntrySessionIsOver(
            new MarketStatus(IsOpen: true, nextMorningPkt, "open"), lateUtc));
    }
}
