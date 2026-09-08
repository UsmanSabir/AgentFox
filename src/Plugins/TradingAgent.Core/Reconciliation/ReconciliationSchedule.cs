using TradingAgent.Market;

namespace TradingAgent.Reconciliation;

/// <summary>Why a periodic reconciliation pass is running, or that it is not.</summary>
public enum ReconciliationTrigger
{
    /// <summary>Nothing to ask the broker about: the venue is shut and the day is already accounted for.</summary>
    None,

    /// <summary>The process has just started and nothing knows what the account holds.</summary>
    Startup,

    /// <summary>The session is open, so custody and the resting book can change at any moment.</summary>
    InSession,

    /// <summary>The first pass after today's close, which is where the day's fills are captured.</summary>
    PostClose
}

/// <summary>
/// When the periodic account read is worth taking. Pure, so the schedule can be argued about in tests
/// rather than by watching a live agent for a day.
///
/// <para>
/// <b>Why this exists.</b> <see cref="BrokerReconciliationWorker"/>'s loop had no calendar at all: it
/// read the account every <c>ReconciliationIntervalSeconds</c> around the clock, so on the deployed
/// 1260s interval roughly two thirds of ~340 daily SOAP calls were asking a CLOSED venue what had
/// changed. Custody cannot move while the venue is shut — the exchange clears the resting book at the
/// close (§6a) and nothing trades until the next open — so those reads could not learn anything.
/// </para>
///
/// <para>
/// <b>What is deliberately KEPT, and why cutting it would have been the wrong economy.</b> The
/// in-session passes stay exactly as they were. They are the FLOOR under the pushed order events, and
/// §6a's conclusion is that pushes should be the data source with polling as the floor — not that the
/// floor should be removed. A push only arrives if the socket is up: it can be closed, handed to the
/// desktop client for 30 minutes, or in <c>Opportunistic</c> mode. Trading blind through a session
/// because the socket was quiet is a far worse outcome than some wasted calls.
/// </para>
///
/// <para>
/// <b>The startup pass is unconditional, including at the weekend.</b> A restart is precisely when
/// nothing in the process knows what the account holds, and an operator who has just restarted the
/// agent expects the dashboard to tell them rather than to say "reconciliation has not run" until
/// Monday. One read, once per process.
/// </para>
///
/// <para>
/// <b>The post-close pass is once per trading day and must not be spent early.</b> Before the open,
/// <c>IsOpen</c> is also false, so a naive "not open" test would burn the day's single slot at 08:00
/// and never capture the fills. The discriminator comes from how <c>PsxMarketCalendar</c> builds its
/// status: before the open <see cref="MarketStatus.NextOpenPkt"/> is TODAY's open, and after the close
/// it is a later date (or not yet computed). Nothing here infers a time of day.
/// </para>
/// </summary>
public static class ReconciliationSchedule
{
    /// <param name="startup">Whether this is the first pass of the process.</param>
    /// <param name="status">The calendar's reading, which also supplies the clock.</param>
    /// <param name="tradingDay">Whether today is a session at all — holidays included, so this must
    /// come from <see cref="IMarketCalendar.IsTradingDay"/> rather than from the day of the week.</param>
    /// <param name="postCloseDoneFor">The PKT date whose post-close pass has already run, if any.</param>
    public static ReconciliationTrigger Decide(
        bool startup,
        MarketStatus status,
        bool tradingDay,
        DateOnly? postCloseDoneFor)
    {
        if (startup) return ReconciliationTrigger.Startup;
        if (status.IsOpen) return ReconciliationTrigger.InSession;
        if (!tradingDay) return ReconciliationTrigger.None;

        var today = DateOnly.FromDateTime(status.PktNow);
        if (postCloseDoneFor == today) return ReconciliationTrigger.None;

        return AfterTodaysClose(status)
            ? ReconciliationTrigger.PostClose
            : ReconciliationTrigger.None;
    }

    /// <summary>
    /// Whether a closed reading is AFTER today's session rather than before it. See the type's remarks:
    /// a pre-open reading names today's open as the next one, a post-close reading names a later day.
    /// </summary>
    private static bool AfterTodaysClose(MarketStatus status) =>
        status.NextOpenPkt is not { } next
        || next.Date > status.PktNow.Date;

    public static string Describe(ReconciliationTrigger trigger) => trigger switch
    {
        ReconciliationTrigger.Startup   => "the agent has just started",
        ReconciliationTrigger.InSession => "the session is open",
        ReconciliationTrigger.PostClose => "the session has closed, so this pass captures the day",
        _                               => "no reason"
    };
}
