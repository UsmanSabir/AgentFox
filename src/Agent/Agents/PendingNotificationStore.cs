using System.Collections.Concurrent;

namespace AgentFox.Agents;

/// <summary>
/// Stores background sub-agent results that completed after the originating HTTP
/// request had already returned. Web clients poll GET /chat/pending/{conversationId}
/// to drain and display these notifications.
///
/// Entries that are not polled within <see cref="Retention"/> are returned by
/// <see cref="DrainExpired"/> so the caller can broadcast them via channels and
/// then discard them, preventing unbounded memory growth.
/// </summary>
public class PendingNotificationStore
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<PendingNotification>> _store = new();

    /// <summary>
    /// How long a notification may sit un-polled before it is considered expired.
    /// Defaults to 10 minutes. The AgentOrchestrator checks periodically and
    /// broadcasts expired entries via channels, then discards them.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Add a notification for the given conversation/session.
    /// </summary>
    public void Add(string conversationId, string message, string? subAgentRunId = null)
    {
        var queue = _store.GetOrAdd(conversationId, _ => new ConcurrentQueue<PendingNotification>());
        queue.Enqueue(new PendingNotification(message, DateTime.UtcNow, subAgentRunId));
    }

    /// <summary>
    /// Drain all pending notifications for a conversation. Returns them in order and
    /// removes them from the store (each notification is delivered exactly once).
    /// </summary>
    public IReadOnlyList<PendingNotification> Drain(string conversationId)
    {
        if (!_store.TryGetValue(conversationId, out var queue))
            return Array.Empty<PendingNotification>();

        var results = new List<PendingNotification>();
        while (queue.TryDequeue(out var item))
            results.Add(item);
        return results;
    }

    /// <summary>
    /// Returns true if there are pending notifications for the given conversation.
    /// </summary>
    public bool HasPending(string conversationId) =>
        _store.TryGetValue(conversationId, out var q) && !q.IsEmpty;

    /// <summary>
    /// Drains all entries older than <see cref="Retention"/>, grouped by conversationId.
    /// Non-expired entries for the same conversation are preserved in the queue.
    ///
    /// Thread-safety note: there is a brief moment per conversation where the queue
    /// is empty while fresh entries are being re-enqueued. A concurrent <see cref="Drain"/>
    /// call during that window returns nothing for that conversation; the fresh entries
    /// become visible again once re-enqueuing completes. This is acceptable for a
    /// notification store where at-most-once delivery is desired.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PendingNotification>> DrainExpired()
    {
        var result = new Dictionary<string, IReadOnlyList<PendingNotification>>();
        var cutoff = DateTime.UtcNow - Retention;

        foreach (var (convId, queue) in _store)
        {
            // Snapshot the queue by draining everything.
            var all = new List<PendingNotification>();
            while (queue.TryDequeue(out var item))
                all.Add(item);

            if (all.Count == 0) continue;

            var expired = all.Where(x => x.Timestamp < cutoff).ToList();
            var fresh   = all.Where(x => x.Timestamp >= cutoff).ToList();

            // Re-enqueue non-expired items so the client can still poll them.
            foreach (var item in fresh)
                queue.Enqueue(item);

            if (expired.Count > 0)
                result[convId] = expired;
        }

        return result;
    }
}

/// <summary>A single queued notification from a completed background sub-agent.</summary>
public record PendingNotification(
    string Message,
    DateTime Timestamp,
    string? SubAgentRunId);
