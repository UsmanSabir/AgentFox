using AgentFox.Plugins.Channels;

namespace TradingAgent;

/// <summary>
/// The notification subjects this plugin publishes on, so an operator can point one channel at
/// order flow and another at everything else:
/// <code>
///   "Subscribe": "trading.order.>"      // fills and failures only
///   "Subscribe": "trading.>"            // everything the trading agent emits
///   "Subscribe": "*.order.unknown"      // every unreconciled order, whatever the source
/// </code>
///
/// <para>
/// Constants rather than literals at the call sites: the whole failure mode of subject routing is
/// that a mistyped topic matches no subscription and the message evaporates with nothing logged
/// against the sender. One definition per subject is what makes that mistake compile-time.
/// </para>
/// </summary>
public static class TradingTopics
{
    /// <summary>Root of every subject below. Subscribe with <c>trading.&gt;</c> for all of it.</summary>
    public const string Root = "trading";

    /// <summary>Order execution outcomes: <c>trading.order.{accepted|failed|unknown|simulated}</c>.</summary>
    public const string OrderPrefix = "trading.order";

    /// <summary>Protective-stop coverage problems: <c>trading.stop.{reason}</c>.</summary>
    public const string StopPrefix = "trading.stop";

    /// <summary>The subject for an execution in <paramref name="state"/>, as the ledger records it.</summary>
    public static string Order(string state) =>
        $"{OrderPrefix}.{Segment(state, "other")}";

    /// <summary>The subject for a position left without a working stop, keyed by why.</summary>
    public static string Stop(string reasonKey) =>
        $"{StopPrefix}.{Segment(reasonKey, "other")}";

    /// <summary>
    /// Declares the subjects with the host so they appear in the channels UI and in the
    /// <c>notify_user</c> tool description. Safe to call more than once.
    /// </summary>
    public static void RegisterAll()
    {
        NotificationTopics.Register(Order("accepted"), "Orders were accepted by the broker.");
        NotificationTopics.Register(Order("failed"), "One or more orders failed at the broker.");
        NotificationTopics.Register(Order("unknown"), "Broker outcome is unknown — needs manual reconciliation.");
        NotificationTopics.Register(Order("simulated"), "A simulated execution; nothing was submitted.");

        NotificationTopics.Register(Stop("no-baseline"), "A protective stop has no usable price baseline.");
        NotificationTopics.Register(Stop("closed"), "A protective stop could not be placed while the market is closed.");
        NotificationTopics.Register(Stop("unauthorised"), "A protective stop is blocked by policy or the kill switch.");
        NotificationTopics.Register(Stop("placement-failed"), "A protective stop failed to reach the broker.");
    }

    /// <summary>
    /// Coerces a runtime value into a legal single segment. States and reason keys come from
    /// ledger rows and broker responses, so an unexpected one is possible; it lands on
    /// <paramref name="fallback"/> rather than producing an unroutable subject.
    /// </summary>
    private static string Segment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var cleaned = new string([.. value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-')]);

        return cleaned.Trim('-').Length == 0 ? fallback : cleaned.Trim('-');
    }
}
