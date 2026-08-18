namespace AgentFox.Plugins.Channels;

/// <summary>
/// The set of topic filters one channel listens on. Immutable; swap the whole object to change a
/// channel's subscriptions rather than mutating a list other threads may be iterating.
///
/// <para>
/// The default is <see cref="All"/> — a bare <c>&gt;</c>. That matters more than it looks: every
/// notification in this system was an unconditional broadcast before subscriptions existed, so a
/// channel with nothing configured has to keep receiving everything. Defaulting to "empty" would
/// turn an upgrade into a silent, total outage of user notifications.
/// </para>
/// </summary>
public sealed class TopicSubscription
{
    /// <summary>Catch-all — receives every topic. The default for an unconfigured channel.</summary>
    public static readonly TopicSubscription All = new([TopicFilter.CatchAll]);

    private readonly string[] _filters;

    private TopicSubscription(string[] filters) => _filters = filters;

    /// <summary>The filters as stored, already normalized to lowercase.</summary>
    public IReadOnlyList<string> Filters => _filters;

    /// <summary>True when this subscription is the plain catch-all and filters nothing out.</summary>
    public bool IsCatchAll =>
        _filters.Length == 1 && _filters[0] == TopicFilter.CatchAll;

    /// <summary>
    /// True when <paramref name="topic"/> matches any filter. A null or blank topic — an
    /// unaddressed notification, which is every caller that predates topics — matches
    /// unconditionally, so nothing that used to be delivered stops being delivered.
    /// </summary>
    public bool Matches(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return true;

        foreach (var filter in _filters)
        {
            if (TopicFilter.Matches(filter, topic))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Builds a subscription from a list of filters, dropping invalid ones. An empty or
    /// all-invalid list yields <see cref="All"/> rather than a channel that receives nothing —
    /// a mistyped filter should degrade to noisy, not to silent.
    /// </summary>
    public static TopicSubscription FromFilters(IEnumerable<string>? filters) =>
        TryCreate(filters, out var subscription, out _) ? subscription : All;

    /// <summary>
    /// As <see cref="FromFilters(IEnumerable{string})"/>, but reports the filters that were
    /// rejected so a caller with somewhere to put a warning can surface them.
    /// </summary>
    public static bool TryCreate(
        IEnumerable<string>? filters,
        out TopicSubscription subscription,
        out IReadOnlyList<string> errors)
    {
        var accepted = new List<string>();
        var rejected = new List<string>();

        foreach (var raw in filters ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var normalized = TopicFilter.Normalize(raw);
            if (!TopicFilter.IsValidFilter(normalized, out var error))
            {
                rejected.Add(error!);
                continue;
            }

            if (!accepted.Contains(normalized, StringComparer.Ordinal))
                accepted.Add(normalized);
        }

        errors = rejected;

        if (accepted.Count == 0)
        {
            subscription = All;
            return rejected.Count == 0;
        }

        // A list that already contains the catch-all is exactly the catch-all; the rest are
        // redundant and only make the UI harder to read.
        subscription = accepted.Contains(TopicFilter.CatchAll, StringComparer.Ordinal)
            ? All
            : new TopicSubscription([.. accepted]);

        return rejected.Count == 0;
    }

    /// <summary>
    /// Parses the config form: filters separated by commas, semicolons or whitespace
    /// (<c>"trading.&gt;, hitl.&gt;"</c>). Blank input means "unset", which is the catch-all.
    /// </summary>
    public static TopicSubscription Parse(string? spec) =>
        TryParse(spec, out var subscription, out _) ? subscription : All;

    /// <inheritdoc cref="Parse"/>
    public static bool TryParse(
        string? spec,
        out TopicSubscription subscription,
        out IReadOnlyList<string> errors)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            subscription = All;
            errors = [];
            return true;
        }

        var parts = spec.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return TryCreate(parts, out subscription, out errors);
    }

    /// <summary>The config form — round-trips through <see cref="Parse"/>.</summary>
    public override string ToString() => string.Join(", ", _filters);
}
