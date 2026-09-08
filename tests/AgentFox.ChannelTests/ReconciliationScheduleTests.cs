using TradingAgent.Market;
using TradingAgent.Reconciliation;

namespace AgentFox.ChannelTests;

/// <summary>
/// Pins when the periodic account read is worth taking. It used to run around the clock, so on the
/// deployed 1260s interval roughly two thirds of ~340 daily SOAP calls asked a shut venue what had
/// changed.
///
/// <para>
/// Two of these guard against saving calls in the wrong place: the in-session pass must survive,
/// because it is the floor under the pushed order events, and the post-close pass must not be spent
/// before the open, because that is where the day's fills are captured.
/// </para>
/// </summary>
[TestClass]
public sealed class ReconciliationScheduleTests
{
    private static readonly DateOnly Monday = new(2026, 9, 7);
    private static readonly DateOnly Sunday = new(2026, 9, 6);

    [TestMethod]
    public void EveryInSessionPassSurvives()
    {
        // The economy this change is NOT making. A push only arrives if the socket is up — it can be
        // closed, handed to the desktop client, or in Opportunistic mode — so trading a whole session
        // on an unrefreshed snapshot would be far worse than some wasted calls.
        Assert.AreEqual(ReconciliationTrigger.InSession,
            ReconciliationSchedule.Decide(
                startup: false, Open(Monday, 11, 00), tradingDay: true, postCloseDoneFor: Monday));
    }

    [TestMethod]
    public void TheDaysSingleSlotIsNotSpentBeforeTheOpen()
    {
        // THE trap. Before the open IsOpen is false too, so a naive "not open" test would take the
        // day's only closed-market pass at 08:00 and never capture the fills.
        Assert.AreEqual(ReconciliationTrigger.None,
            ReconciliationSchedule.Decide(
                startup: false, BeforeOpen(Monday, 8, 00), tradingDay: true, postCloseDoneFor: null));

        Assert.AreEqual(ReconciliationTrigger.PostClose,
            ReconciliationSchedule.Decide(
                startup: false, AfterClose(Monday, 15, 45), tradingDay: true, postCloseDoneFor: null),
            "The same day, after the close, is the pass that captures it.");
    }

    [TestMethod]
    public void ThePostClosePassRunsOncePerDay()
    {
        Assert.AreEqual(ReconciliationTrigger.PostClose,
            ReconciliationSchedule.Decide(
                startup: false, AfterClose(Monday, 15, 45), tradingDay: true, postCloseDoneFor: null));

        Assert.AreEqual(ReconciliationTrigger.None,
            ReconciliationSchedule.Decide(
                startup: false, AfterClose(Monday, 18, 00), tradingDay: true, postCloseDoneFor: Monday),
            "Custody cannot move again today: the venue cleared its book at the close.");

        Assert.AreEqual(ReconciliationTrigger.PostClose,
            ReconciliationSchedule.Decide(
                startup: false, AfterClose(new DateOnly(2026, 9, 8), 15, 45),
                tradingDay: true, postCloseDoneFor: Monday),
            "Yesterday's pass does not account for today.");
    }

    [TestMethod]
    public void ANonTradingDayIsSkippedEntirely()
    {
        // 2026-09-06 is the Sunday §6a's own probe was taken on — the one where REG answered OHO. The
        // venue will happily talk to us; there is simply nothing it can tell us that has changed.
        Assert.AreEqual(ReconciliationTrigger.None,
            ReconciliationSchedule.Decide(
                startup: false, AfterClose(Sunday, 10, 59), tradingDay: false, postCloseDoneFor: null));
        Assert.AreEqual(ReconciliationTrigger.None,
            ReconciliationSchedule.Decide(
                startup: false, BeforeOpen(Sunday, 8, 00), tradingDay: false, postCloseDoneFor: null));
    }

    [TestMethod]
    public void StartupAlwaysReads_EvenAtTheWeekend()
    {
        // A restart is exactly when nothing in the process knows what the account holds, and an
        // operator who has just restarted expects an answer rather than "reconciliation has not run"
        // until Monday.
        Assert.AreEqual(ReconciliationTrigger.Startup,
            ReconciliationSchedule.Decide(
                startup: true, AfterClose(Sunday, 10, 59), tradingDay: false, postCloseDoneFor: null));
    }

    /// <summary>Open: the calendar names the session it is inside and no next open.</summary>
    private static MarketStatus Open(DateOnly day, int hour, int minute) =>
        new(true, At(day, hour, minute), "open",
            SessionOpenPkt: At(day, 9, 32));

    /// <summary>
    /// Closed before today's session — the calendar names TODAY's open as the next one, which is the
    /// only thing separating this from a post-close reading.
    /// </summary>
    private static MarketStatus BeforeOpen(DateOnly day, int hour, int minute) =>
        new(false, At(day, hour, minute), "closed", NextOpenPkt: At(day, 9, 32));

    /// <summary>Closed after today's session — the next open is a later date.</summary>
    private static MarketStatus AfterClose(DateOnly day, int hour, int minute) =>
        new(false, At(day, hour, minute), "closed",
            NextOpenPkt: At(day.AddDays(1), 9, 32));

    private static DateTime At(DateOnly day, int hour, int minute) =>
        day.ToDateTime(new TimeOnly(hour, minute));
}
