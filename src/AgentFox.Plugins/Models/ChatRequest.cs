namespace AgentFox.Plugins.Models;

/// <summary>Incoming chat request from the HTTP /chat endpoint.</summary>
public class ChatRequest
{
    /// <summary>The user message to send to the agent.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Optional conversation/session ID for multi-turn continuity.
    /// If omitted the agent starts or continues the default session.
    /// </summary>
    public string? ConversationId { get; set; }
}

/// <summary>Response from the HTTP /chat endpoint.</summary>
public class ChatResponse
{
    /// <summary>The agent's reply.</summary>
    public string Response { get; set; } = string.Empty;

    /// <summary>The conversation ID that was used (for follow-up turns).</summary>
    public string? ConversationId { get; set; }

    /// <summary>Whether the request succeeded.</summary>
    public bool Success { get; set; } = true;

    /// <summary>Error message if <see cref="Success"/> is false.</summary>
    public string? Error { get; set; }
}