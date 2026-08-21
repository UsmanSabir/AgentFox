namespace TradingAgent.Feed;

/// <summary>
/// Works out which symbols go on which of the portal's subscription page slots.
///
/// <para>
/// Pure and separate from <see cref="AhkFeedWorker"/> because of the bug that produced it. A page
/// list that contains the SAME slot twice is catastrophic rather than merely wasteful: the slots are
/// overwritten in order, so a duplicate later in the list re-sends that slot with whatever slice
/// falls at its index — and past the end of the symbol list, that slice is EMPTY. The duplicate
/// silently wipes the subscription the first occurrence just made, and the only symptom is a feed
/// that returns nothing, which is indistinguishable from a quiet market.
/// </para>
///
/// <para>
/// That is not hypothetical. <c>AhkFeedConfig.Pages</c> initially defaulted to the four page names as
/// a property initializer, and .NET's <c>ConfigurationBinder</c> <b>appends</b> to a pre-populated
/// collection property instead of replacing it — so the four names in appsettings produced a list of
/// eight, every slot duplicated, and the live run subscribed 30 symbols and then immediately
/// unsubscribed them.
/// </para>
/// </summary>
public static class FeedPagePlanner
{
    /// <summary>The portal's own market-watch page slots, used when configuration supplies none.</summary>
    public static readonly IReadOnlyList<string> DefaultPages = ["Page1", "Page2", "Page3", "Page4"];

    /// <summary>
    /// Cleans a configured page list: trims, drops blanks, removes duplicates case-insensitively
    /// while preserving order, and falls back to <see cref="DefaultPages"/> when nothing usable
    /// remains.
    /// </summary>
    public static IReadOnlyList<string> NormalizePages(IEnumerable<string>? configured)
    {
        if (configured is null) return DefaultPages;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = new List<string>();

        foreach (var raw in configured)
        {
            var page = raw?.Trim();
            if (string.IsNullOrEmpty(page)) continue;
            if (!seen.Add(page)) continue;
            pages.Add(page);
        }

        return pages.Count > 0 ? pages : DefaultPages;
    }

    /// <summary>
    /// Assigns <paramref name="symbols"/> across <paramref name="pages"/>, at most
    /// <paramref name="pageSize"/> per page, and reports any symbols that did not fit.
    ///
    /// <para>
    /// Every page gets an entry, including the empty ones. That is deliberate: a slot holds whatever
    /// was last put in it, so shrinking the universe without explicitly clearing the tail would keep
    /// the portal streaming symbols that are no longer watched.
    /// </para>
    /// </summary>
    public static (IReadOnlyList<(string Page, IReadOnlyList<string> Symbols)> Assignments,
                   IReadOnlyList<string> Dropped)
        Plan(IReadOnlyList<string> symbols, int pageSize, IReadOnlyList<string> pages)
    {
        var size = Math.Clamp(pageSize, 1, 200);
        var capacity = size * pages.Count;

        var fitted = symbols.Count > capacity ? symbols.Take(capacity).ToList() : symbols;
        var dropped = symbols.Count > capacity ? symbols.Skip(capacity).ToList() : [];

        var assignments = new List<(string, IReadOnlyList<string>)>(pages.Count);
        for (var i = 0; i < pages.Count; i++)
            assignments.Add((pages[i], fitted.Skip(i * size).Take(size).ToList()));

        return (assignments, dropped);
    }
}
