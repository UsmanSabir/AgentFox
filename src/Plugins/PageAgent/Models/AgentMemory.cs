namespace PageAgent.Models;

/// <summary>
/// Accumulates knowledge across agent steps: visited URLs (to prevent loops)
/// and free-form notes extracted from pages.
/// </summary>
public sealed class AgentMemory
{
    /// <summary>URLs the agent has already navigated to (case-insensitive, trailing-slash normalised).</summary>
    public HashSet<string> VisitedUrls { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Short, extracted facts or summaries the agent has noted so far.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>Adds a note if it is non-empty and not already present (deduplication).</summary>
    public void AddNote(string note)
    {
        note = note.Trim();
        if (!string.IsNullOrEmpty(note) && !Notes.Contains(note))
            Notes.Add(note);
    }

    /// <summary>Returns true if the URL has been visited.</summary>
    public bool HasVisited(string url) =>
        VisitedUrls.Contains(Normalize(url));

    /// <summary>Records the URL as visited.</summary>
    public void MarkVisited(string url) =>
        VisitedUrls.Add(Normalize(url));

    private static string Normalize(string url) =>
        url.TrimEnd('/').ToLowerInvariant();
}
