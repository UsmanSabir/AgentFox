using System.Collections.Concurrent;
using Microsoft.Agents.AI;

namespace AgentFox.Memory;

public interface IConversationStore
{
    [Obsolete]
    AgentSession? GetSession(string conversationId);
    [Obsolete]
    void SaveSession(string conversationId, AgentSession thread);
}

public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, AgentSession> _threads = new();

    public AgentSession? GetSession(string conversationId)
    {
        _threads.TryGetValue(conversationId, out var thread);
        return thread;
    }

    public void SaveSession(string conversationId, AgentSession thread)
    {
        _threads[conversationId] = thread;
    }
}