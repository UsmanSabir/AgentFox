using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFox.Memory;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentFox.Runtime;

/// <summary>
/// High-level service that persists conversation state as checkpoints after
/// each successful agent turn, and supports restoring the session to any
/// prior checkpoint.
///
/// Architecture
/// ────────────
/// • Wraps <see cref="SqliteJsonCheckpointStore"/> (implements
///   <c>ICheckpointStore&lt;JsonElement&gt;</c>) and creates a
///   <see cref="CheckpointManager"/> via <c>CheckpointManager.CreateJson</c>.
/// • The manager is exposed via <see cref="Manager"/> for future use with
///   <c>InProcessExecution</c> when workflow-based executors are added.
/// • For the current <c>ChatClientAgent</c>-based execution path, checkpoints
///   are saved explicitly at turn boundaries via <see cref="SaveTurnCheckpointAsync"/>.
///
/// Checkpoint format
/// ─────────────────
/// Each checkpoint stores the full <c>List&lt;ChatMessage&gt;</c> for a
/// conversation, serialised as a JSON array.  On restore, the message list
/// is loaded back into <see cref="MarkdownSessionStore"/> so the next LLM
/// call sees the history at that exact point.
/// </summary>
public sealed class ConversationCheckpointService : IDisposable
{
    private readonly SqliteJsonCheckpointStore _store;
    private readonly MarkdownSessionStore _sessionStore;
    private readonly ILogger<ConversationCheckpointService>? _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The underlying <see cref="CheckpointManager"/> (created with the SQLite
    /// store).  Pass this to <c>InProcessExecution.RunStreamingAsync</c> when
    /// workflow-based execution is used.
    /// </summary>
    public CheckpointManager Manager { get; }

    public ConversationCheckpointService(
        SqliteJsonCheckpointStore store,
        MarkdownSessionStore sessionStore,
        ILogger<ConversationCheckpointService>? logger = null)
    {
        _store = store;
        _sessionStore = sessionStore;
        _logger = logger;
        Manager = CheckpointManager.CreateJson(store, JsonOpts);
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves the current in-memory conversation messages as a new checkpoint.
    /// Called by <c>FoxAgent.ProcessAsync</c> after each successful turn.
    /// Returns null if there are no messages to save.
    /// </summary>
    public async Task<CheckpointEntry?> SaveTurnCheckpointAsync(
        string conversationId, CancellationToken ct = default)
    {
        var messages = _sessionStore.GetMessages(conversationId);
        if (messages is null || messages.Count == 0)
            return null;

        // Fetch the most recent checkpoint to use as the parent (chain)
        var existing = await _store.RetrieveIndexAsync(conversationId, null).ConfigureAwait(false);
        var parent = existing.LastOrDefault();

        var json = JsonSerializer.SerializeToElement(messages, JsonOpts);
        var info = await _store.CreateCheckpointAsync(conversationId, json, parent).ConfigureAwait(false);

        var entries = await _store.ListEntriesAsync(conversationId).ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e => e.Info.CheckpointId == info.CheckpointId)
                    ?? new CheckpointEntry(info, DateTime.UtcNow);

        _logger?.LogDebug(
            "Checkpoint saved: session={Session} id={Id} messages={Count}",
            conversationId, info.CheckpointId, messages.Count);

        return entry;
    }

    // ── List ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all checkpoints for a session, newest first.
    /// </summary>
    public Task<IReadOnlyList<CheckpointEntry>> ListCheckpointsAsync(string conversationId)
        => _store.ListEntriesAsync(conversationId);

    // ── Restore ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores a session's message history from the specified checkpoint.
    /// After calling this, the session cache is evicted so the next
    /// <c>ProcessAsync</c> call will reload from the restored state.
    ///
    /// The markdown session file is rewritten to match the checkpoint so the
    /// restored state survives a process restart.
    /// </summary>
    public async Task<bool> RestoreCheckpointAsync(
        string conversationId,
        CheckpointEntry entry,
        CancellationToken ct = default)
    {
        List<ChatMessage>? messages;
        try
        {
            var element = await _store
                .RetrieveCheckpointAsync(conversationId, entry.Info)
                .ConfigureAwait(false);

            messages = JsonSerializer.Deserialize<List<ChatMessage>>(
                element.GetRawText(), JsonOpts);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Failed to retrieve checkpoint {Id} for session {Session}",
                entry.Info.CheckpointId, conversationId);
            return false;
        }

        if (messages is null)
            return false;

        await _sessionStore.RestoreFromCheckpointAsync(conversationId, messages)
            .ConfigureAwait(false);

        _logger?.LogInformation(
            "Restored session {Session} to checkpoint {Id} ({Count} messages)",
            conversationId, entry.Info.CheckpointId, messages.Count);

        return true;
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes all checkpoints for a session.
    /// Called when a session is archived or reset.
    /// </summary>
    public Task PruneCheckpointsAsync(string conversationId)
    {
        _logger?.LogDebug("Pruning checkpoints for session {Session}", conversationId);
        return _store.DeleteSessionCheckpointsAsync(conversationId);
    }

    public void Dispose() => _store.Dispose();
}
