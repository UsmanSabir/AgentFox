using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace AgentFox.Memory;

public static class AgentSessionExtensions
{
    private const string MessagesKey = "messages";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IList<ChatMessage> GetMessages(this AgentSession session)
    {
        if (!session.StateBag.TryGetValue<IList<ChatMessage>>(MessagesKey, out var list, _jsonOptions)
            || list == null)
        {
            list = new List<ChatMessage>();
            session.StateBag.SetValue(MessagesKey, list, _jsonOptions);
        }

        return list;
    }

    public static void SetMessages(this AgentSession session, IList<ChatMessage> messages)
    {
        session.StateBag.SetValue(MessagesKey, messages, _jsonOptions);
    }
}