using System.Collections.Concurrent;

namespace AgentFox.Plugins.Channels;

/// <summary>One publishable topic, as advertised to operators and to the model.</summary>
/// <param name="Name">The literal topic, e.g. <c>trading.order.accepted</c>.</param>
/// <param name="Description">What publishing on it means.</param>
/// <param name="Mandatory">
/// When true the topic is never filtered down to nothing: if no channel subscribes to it, delivery
/// falls back to every connected channel. Reserved for traffic the user must be able to answer.
/// </param>
public sealed record NotificationTopic(string Name, string Description, bool Mandatory = false);

/// <summary>
/// The topics this host publishes on, plus whatever plugins declare at startup.
///
/// <para>
/// The registry exists because the failure mode of subject-based routing is silence. A filter of
/// <c>trading.orders.&gt;</c> against a topic of <c>trading.order.accepted</c> produces no error
/// anywhere — order fills simply stop arriving. Publishing from constants and showing operators the
/// real list is what keeps a subscription from being written against a topic that does not exist.
/// </para>
/// </summary>
public static class NotificationTopics
{
    // ── Agent / host ─────────────────────────────────────────────────────────

    /// <summary>Default subject of the <c>notify_user</c> tool.</summary>
    public const string AgentNotify = "agent.notify";

    /// <summary>A background sub-agent result that no web client polled in time.</summary>
    public const string AgentSubAgentExpired = "agent.subagent.expired";

    // ── Human-in-the-loop ────────────────────────────────────────────────────
    // Mandatory: these are questions, and the reply comes back through the same channel. Filtering
    // them to zero recipients does not drop a message, it deadlocks the turn that asked — the agent
    // waits on an approval nobody was told about, until it times out.

    /// <summary>A tool call waiting on <c>/approve</c> or <c>/reject</c>.</summary>
    public const string HitlApproval = "hitl.approval";

    /// <summary>A plan submitted for approval.</summary>
    public const string HitlPlan = "hitl.plan";

    /// <summary>A free-form question from <c>request_human_input</c>.</summary>
    public const string HitlInput = "hitl.input";

    /// <summary>Filters whose topics are never narrowed to nothing. See <see cref="IsMandatory"/>.</summary>
    private static readonly string[] MandatoryFilters = ["hitl.>"];

    private static readonly ConcurrentDictionary<string, NotificationTopic> Registry = new(StringComparer.OrdinalIgnoreCase);

    static NotificationTopics()
    {
        Register(AgentNotify, "Messages the agent sends with the notify_user tool.");
        Register(AgentSubAgentExpired, "A background sub-agent result that was never collected by a web client.");
        Register(HitlApproval, "A tool call waiting for approval.", mandatory: true);
        Register(HitlPlan, "A plan waiting for approval.", mandatory: true);
        Register(HitlInput, "A free-form question awaiting a human answer.", mandatory: true);
    }

    /// <summary>
    /// Declares a topic so it shows up in the channels UI and in the <c>notify_user</c> tool
    /// description. Idempotent — re-registering the same name replaces the description, so a plugin
    /// that reloads does not accumulate duplicates. Invalid names are ignored rather than thrown on:
    /// a bad topic constant in a plugin should not take the host down at startup.
    /// </summary>
    public static void Register(string name, string description, bool mandatory = false)
    {
        var normalized = TopicFilter.Normalize(name);
        if (!TopicFilter.IsValidTopic(normalized, out _))
            return;

        Registry[normalized] = new NotificationTopic(normalized, description, mandatory);
    }

    /// <summary>Every declared topic, ordered by name.</summary>
    public static IReadOnlyList<NotificationTopic> Known =>
        Registry.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

    /// <summary>
    /// True when a topic must reach someone. A mandatory topic that matches no subscription is
    /// broadcast to every connected channel instead of being dropped.
    /// </summary>
    public static bool IsMandatory(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return false;

        var normalized = TopicFilter.Normalize(topic);

        if (Registry.TryGetValue(normalized, out var known) && known.Mandatory)
            return true;

        foreach (var filter in MandatoryFilters)
        {
            if (TopicFilter.Matches(filter, normalized))
                return true;
        }

        return false;
    }

    /// <summary>True when the topic has been declared via <see cref="Register"/>.</summary>
    public static bool IsKnown(string? topic) =>
        !string.IsNullOrWhiteSpace(topic) && Registry.ContainsKey(TopicFilter.Normalize(topic));
}
