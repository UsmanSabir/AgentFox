using System.Collections.Concurrent;
using Microsoft.Agents.AI;

namespace AgentFox.Memory;

/// <summary>
/// Manages AgentSession lifecycle — creation, caching, and restoration across restarts.
/// Implementations that also extend ChatHistoryProvider handle message persistence directly.
/// </summary>
public interface IConversationStore
{
    /// <summary>Returns the cached AgentSession for the given id, or null if not in memory.</summary>
    AgentSession? GetSession(string conversationId);

    /// <summary>Caches the session and persists any metadata needed for restart recovery.</summary>
    void SaveSession(string conversationId, AgentSession session);

    /// <summary>
    /// Returns true if the session is known — either cached in memory or recorded on disk.
    /// Use this to decide whether to restore an existing session vs. create a brand-new one.
    /// </summary>
    bool SessionExists(string conversationId);

    /// <summary>All known session IDs (in-memory cache plus any persisted records).</summary>
    IEnumerable<string> GetAllSessionIds();

    /// <summary>Removes the session from the cache and any persisted storage.</summary>
    void DeleteSession(string conversationId);

    /// <summary>
    /// Hydrates a newly-created AgentSession with messages from persisted storage
    /// (if any). Must be called once after CreateSessionAsync and before RunAsync
    /// so the ChatHistoryProvider sees the full history from prior turns.
    /// No-op for stores with no disk backing.
    /// </summary>
    Task RestoreAsync(string conversationId, AgentSession session);
}

/// <summary>
/// Pure in-memory store. Sessions are lost on restart; message history is preserved
/// across turns within the same process via the ChatHistoryProvider.
/// </summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();

    public AgentSession? GetSession(string conversationId)
    {
        _sessions.TryGetValue(conversationId, out var session);
        return session;
    }

    public void SaveSession(string conversationId, AgentSession session)
        => _sessions[conversationId] = session;

    public bool SessionExists(string conversationId)
        => _sessions.ContainsKey(conversationId);

    public IEnumerable<string> GetAllSessionIds()
        => _sessions.Keys;

    public void DeleteSession(string conversationId)
        => _sessions.TryRemove(conversationId, out _);

    public Task RestoreAsync(string conversationId, AgentSession session)
        => Task.CompletedTask; // nothing on disk to restore
}
