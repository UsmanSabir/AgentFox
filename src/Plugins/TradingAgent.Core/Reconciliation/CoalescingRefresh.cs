using Microsoft.Extensions.Logging;

namespace TradingAgent.Reconciliation;

/// <summary>
/// Runs an expensive refresh in response to cheap, bursty events: at most one pass in flight, a short
/// window for a burst to settle, and never a lost request.
///
/// <para>
/// Extracted from <see cref="BrokerReconciliationWorker.RefreshSoon"/> rather than left as a lambda
/// inside it, because the thing that can go wrong is not the reading — it is the bookkeeping. A pass
/// that throws and leaves the queued flag set would stop every future refresh, permanently and
/// silently, and the deployment would quietly go back to a 21-minute-old account picture with nothing
/// in the log to say so. The reading itself needs a broker and a 74-member repository to exercise;
/// this needs a counter and a delegate.
/// </para>
///
/// <para>
/// The three properties worth stating, all of which <c>CoalescingRefreshTests</c> pins:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>A burst becomes one pass.</b> One order's life pushes several events within seconds and each
///     describes the same account, so paying the read per event would be paying for the same fact
///     repeatedly — five SOAP calls at a time, on an account with a single session.
///   </description></item>
///   <item><description>
///     <b>A request arriving DURING a pass is honoured by another pass,</b> not folded into the one
///     already reading. That read may have started before the change and cannot be assumed to include
///     it. This is why the flag is cleared before the work rather than after.
///   </description></item>
///   <item><description>
///     <b>A failed pass leaves the door open.</b> The next request schedules normally.
///   </description></item>
/// </list>
/// </summary>
public sealed class CoalescingRefresh
{
    private readonly Func<string, Task> _run;
    private readonly TimeSpan _burstWindow;
    private readonly ILogger _logger;
    private readonly string _what;

    private readonly object _gate = new();
    private bool _queued;
    private string? _reason;

    /// <param name="run">The refresh itself, given the reason of whichever request arrived last.</param>
    /// <param name="burstWindow">
    /// How long to let a burst settle before running. Zero runs as soon as the scheduling thread yields,
    /// which is what tests use; production passes a couple of seconds — long enough to collapse one
    /// order's events, short enough to be invisible to a person doing something and then reacting to it.
    /// </param>
    /// <param name="what">A label for the log line, e.g. "Reconciliation".</param>
    public CoalescingRefresh(
        Func<string, Task> run, TimeSpan burstWindow, ILogger logger, string what)
    {
        _run = run;
        _burstWindow = burstWindow;
        _logger = logger;
        _what = what;
    }

    /// <summary>
    /// Asks for a refresh. Returns immediately: the caller is an event handler on a socket pump and must
    /// never be blocked on a broker round trip.
    /// </summary>
    public void Request(string reason)
    {
        lock (_gate)
        {
            // Last reason wins. Whichever event arrived most recently is the most useful description of
            // why the account is being re-read, and they are all describing one account.
            _reason = reason;
            if (_queued) return;
            _queued = true;
        }

        _ = Task.Run(RunAsync);
    }

    /// <summary>Whether a pass is currently scheduled. For tests and diagnostics only.</summary>
    public bool Pending
    {
        get { lock (_gate) return _queued; }
    }

    private async Task RunAsync()
    {
        string reason;
        try
        {
            if (_burstWindow > TimeSpan.Zero) await Task.Delay(_burstWindow);

            lock (_gate)
            {
                reason = _reason ?? "an event";
                // Cleared BEFORE the work, not after. A request arriving while the refresh is reading
                // describes state that read may not include, and has to be able to schedule another.
                _queued = false;
                _reason = null;
            }
        }
        catch (Exception ex)
        {
            lock (_gate) _queued = false;
            _logger.LogWarning(ex, "[{What}] Could not schedule an event-driven refresh.", _what);
            return;
        }

        try
        {
            await _run(reason);
        }
        catch (Exception ex)
        {
            // Never rethrown: this runs on a fire-and-forget task, so an escaping exception would be an
            // unobserved one. The periodic pass remains the durable fallback.
            _logger.LogWarning(ex,
                "[{What}] Event-driven refresh failed; the periodic pass will pick it up.", _what);
        }
    }
}
