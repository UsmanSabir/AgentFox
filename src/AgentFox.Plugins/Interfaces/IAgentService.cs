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
    Task<string> RunAsync(string input, string? conversationId = null, CancellationToken ct = default);

    /// <summary>
    /// Run <paramref name="input"/> through the agent, invoking <paramref name="onToken"/>
    /// for each text token as the LLM produces it (real-time streaming).
    /// Returns the full concatenated response once complete.
    /// </summary>
    /// <param name="input">The user message or task.</param>
    /// <param name="conversationId">Optional session/conversation key.</param>
    /// <param name="onToken">Callback invoked per token; must be non-blocking.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> StreamAsync(
        string input,
        string? conversationId,
        Func<string, Task> onToken,
        CancellationToken ct = default);
}