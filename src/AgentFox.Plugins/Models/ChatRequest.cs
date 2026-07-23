using AgentFox.Plugins.Research;

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

    /// <summary>
    /// Files attached to this turn. Only accepted when the configured model advertises
    /// support for them (see the <c>/capabilities</c> endpoint); otherwise the request
    /// is rejected rather than silently dropping the file.
    /// </summary>
    public List<ChatAttachment> Attachments { get; set; } = new();
}

/// <summary>
/// One file attached to a chat turn, carried inline as base64 so a single JSON POST
/// stays the whole transport (no separate upload round-trip, no server-side blob store).
/// </summary>
public class ChatAttachment
{
    /// <summary>Original file name, used for display and for media-type inference.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// MIME type as reported by the browser. Often empty or <c>application/octet-stream</c>
    /// for source files, so the server re-infers it from <see cref="Name"/> when unhelpful.
    /// </summary>
    public string? MediaType { get; set; }

    /// <summary>Base64-encoded file bytes, without a <c>data:</c> URI prefix.</summary>
    public string Data { get; set; } = string.Empty;
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

    /// <summary>Web sources consulted during the turn, for display as citations.</summary>
    public List<ResearchReference> References { get; set; } = new();

    /// <summary>
    /// Zero-based persisted assistant reply index. Web clients use this as a stable fork point.
    /// </summary>
    public int? AssistantIndex { get; set; }
}
