namespace TradingAgent.Feed;

/// <summary>
/// Decides when the feed subscription has to be re-sent. Pure state machine, no I/O — extracted from
/// <see cref="AhkFeedWorker"/> because this is the part that is easy to get subtly wrong and
/// impossible to notice when it is.
///
/// <para>
/// <b>The problem it solves.</b> A subscription can be lost without anything reporting it. The
/// portal answers <c>GetFeed</c> with HTTP 200 and an empty array whether nothing has traded or
/// nothing is subscribed, and it never distinguishes the two. So a lost subscription presents as a
/// market that has gone quiet — the most ordinary observation there is.
/// </para>
///
/// <para>
/// There are two known ways to lose it, and they need different handling:
/// </para>
/// <list type="number">
/// <item><b>The browser clobbers it.</b> The portal's own <c>site.js</c> re-subscribes <c>Page1</c>
/// from its market-watch table on every page load, and that table is almost always empty because the
/// portal does not persist it. So merely opening the trading screen to place an order replaces our
/// subscription with an empty one. This is deterministic and is caught directly, the moment the
/// browser releases the screen — no waiting, no guessing.</item>
/// <item><b>Everything else</b> — a session replaced server-side, a subscription dropped on a portal
/// restart. Undetectable at the point it happens, so it is caught by a silence watchdog instead.</item>
/// </list>
/// </summary>
public sealed class FeedSubscriptionGuard
{
    private bool _browserHeldScreen;
    private int _silentPolls;

    /// <summary>Polls of silence seen since the last update, for diagnostics.</summary>
    public int SilentPolls => _silentPolls;

    /// <summary>Records that the browser is currently on the trading screen and we are yielding to it.</summary>
    public void NoteBrowserHoldsScreen() => _browserHeldScreen = true;

    /// <summary>
    /// Called on a pass where the browser is NOT holding the screen. Returns true when the browser
    /// has just released it, meaning its page load has overwritten the subscription and it must be
    /// re-sent before the next poll is worth anything.
    /// </summary>
    public bool NoteBrowserReleasedScreen()
    {
        if (!_browserHeldScreen) return false;

        _browserHeldScreen = false;

        // Reset the watchdog too: the silence about to follow is explained, and letting it also trip
        // the counter would double-report one cause.
        _silentPolls = 0;
        return true;
    }

    /// <summary>
    /// Folds in the result of one poll and returns true when the subscription should be re-sent.
    ///
    /// <para>
    /// Silence only counts against the watchdog when the PORTAL says the market is open and we
    /// believe we hold a subscription. Outside those conditions an empty feed is the correct and
    /// expected answer, and counting it would re-subscribe all night for nothing.
    /// </para>
    /// </summary>
    /// <param name="appliedAnyQuotes">Whether this poll produced at least one usable quote.</param>
    /// <param name="portalMarketOpen">Whether the portal's own <c>marketStatus</c> reads OPEN.</param>
    /// <param name="hasSubscription">Whether a subscription is believed to be in place.</param>
    /// <param name="silentPollThreshold">Consecutive silent polls to tolerate; floored at 5.</param>
    public bool NotePollResult(
        bool appliedAnyQuotes, bool portalMarketOpen, bool hasSubscription, int silentPollThreshold)
    {
        if (appliedAnyQuotes)
        {
            _silentPolls = 0;
            return false;
        }

        if (!portalMarketOpen || !hasSubscription)
        {
            // Not evidence of anything. Deliberately does NOT reset the counter — a market that
            // flickers between states should not keep clearing a genuine run of silence.
            return false;
        }

        _silentPolls++;
        if (_silentPolls < Math.Max(5, silentPollThreshold)) return false;

        _silentPolls = 0;
        return true;
    }

    /// <summary>Clears all state, for a new trading session.</summary>
    public void Reset()
    {
        _browserHeldScreen = false;
        _silentPolls = 0;
    }
}
