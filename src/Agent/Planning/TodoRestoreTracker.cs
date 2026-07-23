using System.Collections.Concurrent;

namespace AgentFox.Planning;

/// <summary>
/// Records that a session's todo list was rehydrated from disk after a restart, and how old it
/// was. <see cref="TodoPlannerContributor"/> reads this to warn the model that outstanding work
/// predates the current process, so the decision to resume or discard goes to the human rather
/// than being made silently by the model.
///
/// Entries are one-shot: the first turn after a restore consumes the record via
/// <see cref="Consume"/>, so the "you were interrupted" prompt appears once rather than on
/// every turn for the rest of the conversation.
/// </summary>
public sealed class TodoRestoreTracker
{
    private readonly ConcurrentDictionary<string, RestoredTodoState> _bySession = new();

    /// <summary>Note that <paramref name="sessionKey"/> came back with unfinished todos.</summary>
    public void Record(string sessionKey, DateTimeOffset savedAt, int outstandingCount)
        => _bySession[sessionKey ?? string.Empty] =
            new RestoredTodoState(savedAt, outstandingCount);

    /// <summary>
    /// Reads and removes the record for a session. Returns null when the session was not
    /// restored, or when its restore notice has already been surfaced.
    /// </summary>
    public RestoredTodoState? Consume(string? sessionKey)
        => sessionKey != null && _bySession.TryRemove(sessionKey, out var state) ? state : null;

    /// <summary>Drop a session's record without surfacing it (e.g. on /new or /reset).</summary>
    public void Clear(string sessionKey) => _bySession.TryRemove(sessionKey ?? string.Empty, out _);
}

/// <summary>A todo list recovered from a previous process, and when it was last written.</summary>
public sealed record RestoredTodoState(DateTimeOffset SavedAt, int OutstandingCount)
{
    public TimeSpan Age => DateTimeOffset.UtcNow - SavedAt;

    /// <summary>Human-readable age used in the prompt ("3 hours", "2 days").</summary>
    public string DescribeAge()
    {
        var age = Age;
        if (age < TimeSpan.FromMinutes(1)) return "less than a minute";
        if (age < TimeSpan.FromHours(1)) return Plural((int)age.TotalMinutes, "minute");
        if (age < TimeSpan.FromDays(1)) return Plural((int)age.TotalHours, "hour");
        return Plural((int)age.TotalDays, "day");
    }

    private static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
}
