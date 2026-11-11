using TradingAgent.Manager;
using TradingAgent.Trading;

namespace TradingAgent.Watchlist;

/// <summary>What a triggered armed order becomes once the execution boundary has answered.</summary>
public enum ArmedFireDisposition
{
    /// <summary>The broker accepted the order. The armed order is spent.</summary>
    Fired,

    /// <summary>
    /// Nothing is resting and trying again can help: the order was refused before the broker saw it,
    /// or the broker refused it for a reason that can clear. Back to <c>armed</c>, with a backoff.
    /// </summary>
    Rearm,

    /// <summary>
    /// The order must not be retried by a timer: the broker's answer is unknown (it may hold the order),
    /// or it refused for a reason that never clears. Terminal <c>failed</c>; a person decides.
    /// </summary>
    Stop
}

/// <summary>
/// Reads a <see cref="TradingExecutionResult"/> for the armed-order monitor. Pure.
///
/// <para>
/// <b>Why it exists.</b> <c>TradingExecutionResult.Executed</c> means "the broker ANSWERED", not "the
/// broker accepted": a venue rejection comes back <c>Executed = true</c> with a failed order inside.
/// The monitor keyed on that flag alone, so a strategy's trailing MARKET exit the venue refused (a
/// price limit, a wash-trade refusal, a shut board) was recorded as <c>fired</c> and announced as
/// "armed SELL fired" with nothing resting and nothing retrying. The same flag read the other way was
/// also wrong: <c>Executed = false</c> covers both "refused before the broker" (safe to retry) and
/// "broker outcome unknown" (never safe to retry — the broker may hold the order), and both were
/// returned to <c>armed</c>.
/// </para>
///
/// <para>
/// The default for a broker rejection is <see cref="ArmedFireDisposition.Rearm"/>, for the reason
/// <see cref="OrderRejectionOutlook"/> gives: an unrecognised wording must cost a slower retry, never
/// a lost exit. Only a wording that outlook classifies as never clearing stops the order.
/// </para>
/// </summary>
public static class ArmedFireOutcome
{
    public static (ArmedFireDisposition Disposition, string Reason) For(TradingExecutionResult result)
    {
        var orders = result.Groups.SelectMany(group => group).ToList();

        if (result.Executed)
        {
            // Nothing to judge (a mode that records without per-order results) reads as accepted,
            // which is what this path did before.
            if (orders.Count == 0 || orders.All(order => order.Success))
                return (ArmedFireDisposition.Fired, result.Reason);

            var failed = orders.Where(order => !order.Success).ToList();
            var said = string.Join(" ", failed
                .Select(order => order.Message)
                .Where(message => !string.IsNullOrWhiteSpace(message)));
            if (said.Length == 0) said = result.Reason;

            // One order per armed trigger, so "any accepted" cannot hide a partial group in practice;
            // if it ever could, an accepted order is at the broker and must not be sent again.
            if (failed.Count < orders.Count)
                return (ArmedFireDisposition.Fired, result.Reason);

            var never = failed.All(order =>
                OrderRejectionOutlook.Classify(order.Message, order.TransientRejection)
                    == RejectionOutlook.Never);

            return never
                ? (ArmedFireDisposition.Stop,
                    $"The broker refused this order for a reason that will not clear: {said}")
                : (ArmedFireDisposition.Rearm, $"The broker refused this order: {said}");
        }

        // Refused before anything was claimed or sent: a gate said no. Safe to try again.
        if (string.IsNullOrEmpty(result.ExecutionId))
            return (ArmedFireDisposition.Rearm, result.Reason);

        // An execution exists and did not complete: the broker call failed part-way, or an identical
        // execution is still in flight. The broker may hold the order.
        return (ArmedFireDisposition.Stop,
            $"The broker's answer is unknown, so this order is not retried automatically: {result.Reason} "
            + "Check the broker's order book before arming it again.");
    }
}
