using AgentFox.Plugins.Models;

namespace AgentFox.Plugins.Interfaces;

/// <summary>
/// Minimal contract for running a task through the agent.
/// Resolved from DI in web/API modules to process incoming requests.
/// </summary>
public interface IAgentService
{
    /// <summary>
    /// Run <paramref name="input"/> through the agent and return the text response.
    /// </summary>
    /// <param name="input">The user message or task.</param>
    /// <param name="conversationId">
    /// Optional session/conversation key. Pass null to use the agent's default session.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<AgentReply> RunAsync(string input, string? conversationId = null, CancellationToken ct = default);

    /// <summary>
    /// Run <paramref name="input"/> through the agent, invoking <paramref name="onToken"/>
    /// for each text token as the LLM produces it (real-time streaming).
    /// Returns the full concatenated response once complete.
    /// </summary>
    /// <param name="input">The user message or task.</param>
    /// <param name="conversationId">Optional session/conversation key.</param>
    /// <param name="onToken">Callback invoked per token; must be non-blocking.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AgentReply> StreamAsync(
        string input,
        string? conversationId,
        Func<string, Task> onToken,
        Func<string, Task>? onReasoning = null,
        Func<string, Task>? onStatus = null,
        Func<AgentToolActivity, Task>? onToolActivity = null,
        CancellationToken ct = default);

    /// <summary>
    /// As <see cref="RunAsync(string, string?, CancellationToken)"/>, with files attached to
    /// the turn. Attachments must already have passed the capability checks for the configured
    /// model — this contract carries them, it does not police them.
    /// </summary>
    /// <remarks>
    /// Defaults to dropping the attachments so existing implementations keep compiling and
    /// keep answering text-only requests correctly.
    /// </remarks>
    Task<AgentReply> RunAsync(
        string input,
        IReadOnlyList<ChatAttachment>? attachments,
        string? conversationId = null,
        CancellationToken ct = default)
        => RunAsync(input, conversationId, ct);

    /// <summary>
    /// As <see cref="StreamAsync"/>, with files attached to the turn.
    /// </summary>
    Task<AgentReply> StreamAsync(
        string input,
        IReadOnlyList<ChatAttachment>? attachments,
        string? conversationId,
        Func<string, Task> onToken,
        Func<string, Task>? onReasoning = null,
        Func<string, Task>? onStatus = null,
        Func<AgentToolActivity, Task>? onToolActivity = null,
        CancellationToken ct = default)
        => StreamAsync(input, conversationId, onToken, onReasoning, onStatus, onToolActivity, ct);
}
