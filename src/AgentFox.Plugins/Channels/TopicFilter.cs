namespace AgentFox.Plugins.Channels;

/// <summary>
/// Subject matching for channel notification subscriptions.
///
/// <para>
/// A <b>topic</b> is what a publisher addresses a notification to — dot-separated segments,
/// no wildcards: <c>trading.order.accepted</c>, <c>hitl.approval</c>.
/// </para>
/// <para>
/// A <b>filter</b> is what a channel subscribes with. It may contain two wildcards, with
/// NATS semantics:
/// <list type="bullet">
///   <item><c>*</c> matches <b>exactly one</b> segment — <c>trading.*</c> matches
///         <c>trading.order</c> but not <c>trading.order.accepted</c>.</item>
///   <item><c>&gt;</c> matches <b>one or more</b> trailing segments and must be the final
///         segment — <c>trading.&gt;</c> matches <c>trading.order</c> and
///         <c>trading.order.accepted</c>, but not the bare <c>trading</c>.</item>
/// </list>
/// A bare <c>&gt;</c> is therefore the catch-all, and is the default every channel gets.
/// </para>
/// <para>
/// The alternative — letting <c>*</c> mean "everything below this point" — is only
/// unambiguous while topics are exactly two segments deep. It stops being decidable the
/// moment a third segment appears, and by then every subscription in every config has been
/// written against the old reading. The single-segment/trailing split costs one extra
/// character and stays correct at any depth.
/// </para>
/// </summary>
public static class TopicFilter
{
    public const char Separator = '.';

    /// <summary>Matches exactly one segment.</summary>
    public const string MatchOne = "*";

    /// <summary>Matches one or more trailing segments. Legal only as the final segment.</summary>
    public const string MatchRest = ">";

    /// <summary>The filter every channel subscribes with unless told otherwise.</summary>
    public const string CatchAll = MatchRest;

    /// <summary>
    /// True when <paramref name="topic"/> is addressed by <paramref name="filter"/>. Comparison is
    /// case-insensitive; both sides are matched segment by segment. A null or blank argument on
    /// either side never matches — callers decide what an unaddressed notification means, and for
    /// the manager that decision is "reaches everyone", not "matches every filter".
    /// </summary>
    public static bool Matches(string? filter, string? topic)
    {
        if (string.IsNullOrWhiteSpace(filter) || string.IsNullOrWhiteSpace(topic))
            return false;

        var f = Split(filter);
        var t = Split(topic);
        if (f.Length == 0 || t.Length == 0)
            return false;

        for (var i = 0; i < f.Length; i++)
        {
            if (f[i] == MatchRest)
            {
                // '>' stands for at least one segment, so it only matches when the topic still has
                // something left at this position. That is what keeps 'trading.>' from matching the
                // bare 'trading' — a channel watching the subtree has not asked for the root event.
                return i == f.Length - 1 && t.Length > i;
            }

            if (i >= t.Length)
                return false;

            if (f[i] == MatchOne)
                continue;

            if (!string.Equals(f[i], t[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // No '>' was hit, so every remaining topic segment is unaccounted for.
        return f.Length == t.Length;
    }

    /// <summary>Splits on <see cref="Separator"/> and trims each segment.</summary>
    public static string[] Split(string value)
    {
        var parts = value.Split(Separator);
        for (var i = 0; i < parts.Length; i++)
            parts[i] = parts[i].Trim();
        return parts;
    }

    /// <summary>Lowercases and strips surrounding whitespace, the canonical form for storage.</summary>
    public static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(Separator, Split(value)).ToLowerInvariant();

    /// <summary>
    /// Validates a published topic: at least one segment, no blanks, no wildcards. Publishers use
    /// literal subjects — a wildcard on the publishing side would silently widen delivery.
    /// </summary>
    public static bool IsValidTopic(string? topic, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(topic))
        {
            error = "Topic is empty.";
            return false;
        }

        var segments = Split(topic);
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                error = $"'{topic}' has an empty segment — segments cannot be blank or doubled up.";
                return false;
            }

            if (segment is MatchOne or MatchRest)
            {
                error = $"'{topic}' contains the wildcard '{segment}'. Topics are literal; only subscriptions use wildcards.";
                return false;
            }

            if (!IsLegalSegment(segment))
            {
                error = $"'{topic}' has an invalid segment '{segment}'. Use letters, digits, '-' and '_'.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates a subscription filter: at least one segment, no blanks, and <c>&gt;</c> only as
    /// the final segment.
    /// </summary>
    public static bool IsValidFilter(string? filter, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(filter))
        {
            error = "Filter is empty.";
            return false;
        }

        var segments = Split(filter);
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            if (segment.Length == 0)
            {
                error = $"'{filter}' has an empty segment — segments cannot be blank or doubled up.";
                return false;
            }

            if (segment == MatchRest && i != segments.Length - 1)
            {
                error = $"'{filter}' puts '{MatchRest}' before the end. It matches all trailing segments, so it can only be last — did you mean '{MatchOne}'?";
                return false;
            }

            if (segment is MatchOne or MatchRest)
                continue;

            if (!IsLegalSegment(segment))
            {
                error = $"'{filter}' has an invalid segment '{segment}'. Use letters, digits, '-' and '_', or the wildcards '{MatchOne}' and '{MatchRest}'.";
                return false;
            }
        }

        return true;
    }

    private static bool IsLegalSegment(string segment)
    {
        foreach (var c in segment)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        }
        return true;
    }
}
