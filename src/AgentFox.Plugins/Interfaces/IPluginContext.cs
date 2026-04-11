namespace AgentFox.Plugins.Interfaces;

/// <summary>
/// Gives plugins full access to the live agent after it has been initialized:
/// tool registration, dynamic system-prompt injection, lifecycle hooks, and
/// read access to conversation history.
/// <para>
/// Provided via <see cref="IAgentAwareModule.OnAgentReadyAsync"/> once the agent is built.
/// </para>
/// </summary>
public interface IPluginContext
{
    // ── Tool registration ────────────────────────────────────────────────────

    /// <summary>Register a tool into the agent's tool registry at runtime.</summary>
    void RegisterTool(ITool tool);

    // ── Channel registration ─────────────────────────────────────────────────

    /// <summary>
    /// Register a channel provider. Channels the provider creates will be routed
    /// through the live ChannelManager.
    /// </summary>
    void RegisterChannelProvider(IChannelProvider provider);

    /// <summary>
    /// Returns the live ChannelManager so a plugin can add/remove channels directly.
    /// </summary>
    IChannelManager ChannelManager { get; }

    // ── Dynamic system-prompt injection ─────────────────────────────────────

    /// <summary>
    /// Contribute a dynamic fragment to the agent's system prompt.
    /// <paramref name="fragmentProvider"/> is called before every LLM turn;
    /// returning <c>null</c> omits the fragment for that turn.
    /// </summary>
    /// <param name="contributorId">Stable ID — used to identify or remove this contributor.</param>
    /// <param name="fragmentProvider">Returns the fragment string, or null if nothing to add.</param>
    void ContributeToSystemPrompt(string contributorId, Func<string?> fragmentProvider);

    /// <summary>Remove a previously registered prompt contributor by its ID.</summary>
    void RemoveSystemPromptContributor(string contributorId);

    // ── Tool lifecycle hooks ─────────────────────────────────────────────────

    /// <summary>Subscribe to the before-tool-execution event.</summary>
    /// <param name="handler">(toolName, arguments, executionId)</param>
    void OnToolPreExecute(Func<string, IDictionary<string, object?>, string, Task> handler);

    /// <summary>Subscribe to the after-tool-execution event (success only).</summary>
    /// <param name="handler">(toolName, resultText, executionTimeMs, executionId)</param>
    void OnToolPostExecute(Func<string, string, long, string, Task> handler);

    /// <summary>Subscribe to the tool-execution error event.</summary>
    /// <param name="handler">(toolName, errorMessage, executionTimeMs, executionId)</param>
    void OnToolError(Func<string, string, long, string, Task> handler);

    // ── Skill lifecycle hooks ────────────────────────────────────────────────

    /// <summary>Subscribe to the skill-enabled event.</summary>
    /// <param name="handler">(skillName, toolCount)</param>
    void OnSkillEnabled(Func<string, int, Task> handler);

    /// <summary>Subscribe to the skill-disabled event.</summary>
    /// <param name="handler">(skillName)</param>
    void OnSkillDisabled(Func<string, Task> handler);

    // ── Conversation access ──────────────────────────────────────────────────

    /// <summary>Read-only access to stored conversation sessions.</summary>
    IPluginConversationAccess Conversations { get; }
}

/// <summary>Read-only view into conversation history stored by the agent.</summary>
public interface IPluginConversationAccess
{
    /// <summary>Returns all known session IDs.</summary>
    IEnumerable<string> GetSessionIds();

    /// <summary>
    /// Returns persisted messages for the given session, ordered oldest-first.
    /// Returns an empty list if the session does not exist or has no persisted messages.
    /// </summary>
    Task<IReadOnlyList<IPluginMessage>> GetMessagesAsync(string sessionId);
}

/// <summary>A single message in a conversation.</summary>
public interface IPluginMessage
{
    /// <summary>"user", "assistant", or "tool".</summary>
    string Role { get; }

    /// <summary>Text content of the message.</summary>
    string Content { get; }

    /// <summary>When the message was recorded (UTC).</summary>
    DateTimeOffset Timestamp { get; }
}
