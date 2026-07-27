using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentFox.Sessions;
using AgentFox.Plugins.Research;

namespace AgentFox.Memory;

// ---------------------------------------------------------------------------
// MarkdownSessionHistoryProvider
//
// Responsibility: bridge the Microsoft.Agents.AI ChatHistoryProvider contract.
// - ProvideChatHistoryAsync  → return the in-memory message list for this session
//   (pre-populated by MarkdownSessionStore.RestoreAsync on session start).
// - StoreChatHistoryAsync    → append new request + response messages (including
//   tool calls and tool results) to the shared list after each RunAsync turn.
//
// State is owned by MarkdownSessionStore and passed in; this class holds no
// state of its own.
// ---------------------------------------------------------------------------

public sealed class MarkdownSessionHistoryProvider : ChatHistoryProvider
{
    private readonly ConditionalWeakTable<AgentSession, StrongBox<string>> _sessionIds;
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _messages;

    //TODO: evaluate whether we need reducer to prevent unbounded memory growth for long-running sessions with many messages.
    internal MarkdownSessionHistoryProvider(
        ConditionalWeakTable<AgentSession, StrongBox<string>> sessionIds,
        ConcurrentDictionary<string, List<ChatMessage>> messages)
    {
        _sessionIds = sessionIds;
        _messages = messages;
    }

    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context, CancellationToken cancellationToken)
    {
        if (!TryGetId(context.Session, out var id))
            return ValueTask.FromResult(Enumerable.Empty<ChatMessage>());

        if (!_messages.TryGetValue(id, out var list))
            return ValueTask.FromResult(Enumerable.Empty<ChatMessage>());

        // Hand back a snapshot, not the live list. Two turns can run against the same
        // conversationId concurrently (a web turn and a sub-agent result-announcement
        // turn), and List<T> throws "Collection was modified" if one appends while the
        // framework is still enumerating for the other's request.
        lock (list)
            return ValueTask.FromResult(list.ToList() as IEnumerable<ChatMessage>);
    }

    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context, CancellationToken cancellationToken)
    {
        if (!TryGetId(context.Session, out var id))
            return ValueTask.CompletedTask;

        var list = _messages.GetOrAdd(id, _ => []);

        lock (list)
        {
            foreach (var msg in context.RequestMessages)
                if (msg.Role != ChatRole.System)
                    list.Add(msg);

            foreach (var msg in context.ResponseMessages)
                list.Add(msg);
        }

        return ValueTask.CompletedTask;
    }

    private bool TryGetId(AgentSession session, out string id)
    {
        if (_sessionIds.TryGetValue(session, out var box) && box.Value != null)
        {
            id = box.Value;
            return true;
        }
        id = string.Empty;
        return false;
    }

    //private static async Task ReduceMessagesAsync(IChatReducer reducer, State state, CancellationToken cancellationToken = default)
    //{
    //    state.Messages = [.. await reducer.ReduceAsync(state.Messages, cancellationToken).ConfigureAwait(false)];
    //}
}

// ---------------------------------------------------------------------------
// MarkdownSessionStore
//
// Responsibility: session lifecycle (cache, restore, persist) and append-only
// markdown serialisation of conversation messages including tool calls.
//
// File format  — one .md per conversationId:
//
//   ---
//   sessionId: main
//   createdAt: 2025-01-01T00:00:00Z
//   ---
//
//   # Chat Log
//
//   ### user
//
//   Hello!
//
//   ### assistant
//
//   [tool_call] {"callId":"c1","name":"calculator","arguments":{"expression":"2+2"}}
//
//   ### tool
//
//   [tool_result] {"callId":"c1","result":"4"}
//
//   ### assistant
//
//   The answer is 4.
//
// Usage:
//   var store = new MarkdownSessionStore(dir);
//   builder.WithConversationStore(store)
//          .WithHistoryProvider(store.HistoryProvider);
// ---------------------------------------------------------------------------
// Enhance based on https://github.com/microsoft/agent-framework/blob/main/dotnet/src/Microsoft.Agents.AI.CosmosNoSql/CosmosChatHistoryProvider.cs
public sealed class MarkdownSessionStore : IConversationStore
{
    // Shared with MarkdownSessionHistoryProvider
    private readonly ConditionalWeakTable<AgentSession, StrongBox<string>> _sessionIds = new();
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _messages = new();

    // Owned by this class only
    private readonly ConcurrentDictionary<string, int> _writtenCounts = new();
    private readonly ConcurrentDictionary<string, AgentSession> _cache = new();
    private readonly string _directory;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private sealed record ReferenceLine(int I, List<ResearchReference> Items);

    public MarkdownSessionStore(string directory)
    {
        _directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        HistoryProvider = new MarkdownSessionHistoryProvider(_sessionIds, _messages);
    }

    /// <summary>
    /// Pass to AgentBuilder.WithHistoryProvider() so the framework routes
    /// message reads and writes through this store's shared state.
    /// </summary>
    public MarkdownSessionHistoryProvider HistoryProvider { get; }

    // ------------------------------------------------------------------
    // IConversationStore
    // ------------------------------------------------------------------

    public AgentSession? GetSession(string conversationId)
    {
        _cache.TryGetValue(conversationId, out var session);
        return session;
    }

    /// <summary>
    /// Caches the session and appends any messages produced since the last save
    /// (or since RestoreAsync set the baseline written count) to the .md file.
    /// </summary>
    public void SaveSession(string conversationId, AgentSession session)
    {
        _cache[conversationId] = session;
        Register(session, conversationId);

        if (!_messages.TryGetValue(conversationId, out var messages))
            return;

        // Read the watermark, compute the delta, append, and advance the watermark as one
        // atomic step. Unlocked, two concurrent saves for the same conversation can both
        // read the same `written`, and the loser's advance then hides the winner's messages
        // behind a watermark that was never actually flushed — silently dropping them from
        // the .md transcript with no exception and no log line.
        lock (messages)
        {
            int written = _writtenCounts.GetOrAdd(conversationId, 0);
            if (messages.Count <= written)
                return;

            var delta = messages.Skip(written).ToList();
            bool isNewFile = written == 0 && !File.Exists(FilePath(conversationId));
            AppendToFile(conversationId, delta, isNewFile);
            _writtenCounts[conversationId] = messages.Count;
        }
    }

    /// <summary>
    /// Test-only: append a fully-formed message directly to the in-memory list and flush it to
    /// disk, bypassing the AI framework's history provider (which needs a live AgentSession).
    /// Mirrors the same delta-tracking logic as SaveSession. Not used in production code paths.
    /// </summary>
    internal void AppendForTest(string conversationId, ChatMessage message)
    {
        var list = _messages.GetOrAdd(conversationId, _ => []);

        lock (list)
        {
            list.Add(message);

            int written = _writtenCounts.GetOrAdd(conversationId, 0);
            var delta = list.Skip(written).ToList();
            bool isNewFile = written == 0 && !File.Exists(FilePath(conversationId));
            AppendToFile(conversationId, delta, isNewFile);
            _writtenCounts[conversationId] = list.Count;
        }
    }

    /// <summary>
    /// Records the research references collected during the most recent assistant turn.
    /// No-op when <paramref name="references"/> is empty. The references are keyed by the
    /// assistant reply's position among user/assistant non-empty-text messages, so they can be
    /// re-attached to the correct snapshot on reload even when other turns have no references.
    /// </summary>
    public void PersistAssistantReferences(string conversationId, IReadOnlyList<ResearchReference> references)
    {
        if (references is null || references.Count == 0) return;
        SessionManager.EnsureSafeSessionId(conversationId);

        if (!_messages.TryGetValue(conversationId, out var live)) return;

        List<ChatMessage> messages;
        lock (live)
            messages = live.ToList();

        int assistantIndex = ProjectSnapshots(messages).Count(s => s.Role == "assistant") - 1;
        if (assistantIndex < 0) return;

        var line = JsonSerializer.Serialize(new ReferenceLine(assistantIndex, references.ToList()), _jsonOpts);
        var refsPath = ReferencesFilePath(conversationId);
        EnsureParentDirectory(refsPath);
        File.AppendAllText(refsPath, line + "\n", Encoding.UTF8);
    }

    /// <summary>
    /// Reads persisted messages from the .md file into the shared message list
    /// and registers the session → conversationId mapping so the history provider
    /// can serve them on the first ProvideChatHistoryAsync call.
    /// Call once after CreateSessionAsync, before RunAsync.
    /// </summary>
    public Task RestoreAsync(string conversationId, AgentSession session)
    {
        Register(session, conversationId);

        var path = FilePath(conversationId);
        if (!File.Exists(path))
            return Task.CompletedTask;

        var loaded = ParseFile(path);
        _messages[conversationId] = loaded;
        _writtenCounts[conversationId] = loaded.Count;
        return Task.CompletedTask;
    }

    public bool SessionExists(string conversationId)
        => _cache.ContainsKey(conversationId) || File.Exists(ResolveFilePath(conversationId));

    public IEnumerable<string> GetAllSessionIds()
    {
        var fromFiles = System.IO.Directory.EnumerateFiles(_directory, "*.md", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_directory, f))
            .Select(rel => rel[..^3]) // strip .md
            .Select(rel => rel.Replace(Path.DirectorySeparatorChar, '/'));
        return _cache.Keys.Union(fromFiles).Distinct();
    }

    /// <summary>
    /// Returns a stable copy of a conversation's messages — from memory when the session is
    /// live, otherwise parsed from the .md file. Copying under the per-conversation lock stops
    /// web-facing readers from enumerating the list while a turn is appending to it.
    /// </summary>
    private List<ChatMessage> SnapshotMessages(string conversationId)
    {
        if (!_messages.TryGetValue(conversationId, out var messages))
        {
            var path = ResolveFilePath(conversationId);
            return File.Exists(path) ? ParseFile(path) : [];
        }

        lock (messages)
            return messages.ToList();
    }

    /// <summary>Returns user-visible text messages for a persisted conversation, with any
    /// research references attached to the corresponding assistant messages.</summary>
    public IReadOnlyList<ConversationMessageSnapshot> GetConversationMessages(string conversationId)
    {
        SessionManager.EnsureSafeSessionId(conversationId);
        var messages = SnapshotMessages(conversationId);

        var snapshots = ProjectSnapshots(messages);
        var refs = LoadReferences(conversationId);

        List<ConversationMessageSnapshot> result;
        if (refs.Count == 0)
        {
            result = snapshots;
        }
        else
        {
            result = new List<ConversationMessageSnapshot>(snapshots.Count);
            foreach (var s in snapshots)
            {
                if (s.AssistantIndex is int assistantIndex &&
                    refs.TryGetValue(assistantIndex, out var items))
                    result.Add(s with { References = items });
                else
                    result.Add(s);
            }
        }

        // Surface an interrupted turn: a .pending sidecar means the last user message was
        // received but never answered (the turn threw, hung, or the process was killed before
        // persisting). Show it so a reloaded conversation is not blank — unless it is already
        // the trailing persisted message.
        var pending = GetLastUnrespondedUserMessage(conversationId);
        if (!string.IsNullOrWhiteSpace(pending))
        {
            var trimmed = pending.Trim();
            bool alreadyLast = result.Count > 0 && result[^1].Role == "user" && result[^1].Content == trimmed;
            if (!alreadyLast)
                result.Add(new ConversationMessageSnapshot("user", trimmed));
        }

        return result;
    }

    /// <summary>
    /// Returns the zero-based index of the latest persisted, user-visible assistant reply.
    /// Null means the conversation has no completed assistant response yet.
    /// </summary>
    public int? GetLatestAssistantIndex(string conversationId)
    {
        SessionManager.EnsureSafeSessionId(conversationId);
        var messages = SnapshotMessages(conversationId);

        return ProjectSnapshots(messages)
            .Where(message => message.AssistantIndex.HasValue)
            .Select(message => message.AssistantIndex)
            .LastOrDefault();
    }

    /// <summary>
    /// Returns persisted tool activity without exposing it through the normal chat projection.
    /// Callers choose whether to request details; the web layer applies redaction before returning
    /// those details to a browser.
    /// </summary>
    public IReadOnlyList<ConversationToolActivitySnapshot> GetConversationToolActivities(
        string conversationId)
    {
        SessionManager.EnsureSafeSessionId(conversationId);
        var messages = SnapshotMessages(conversationId);

        var calls = new List<ConversationToolActivitySnapshot>();
        foreach (var message in messages)
        {
            foreach (var call in message.Contents.OfType<FunctionCallContent>())
            {
                calls.Add(new ConversationToolActivitySnapshot(
                    call.CallId,
                    call.Name,
                    "running",
                    null,
                    call.Arguments,
                    null));
            }

            foreach (var result in message.Contents.OfType<FunctionResultContent>())
            {
                var index = calls.FindIndex(item => item.CallId == result.CallId);
                if (index >= 0)
                {
                    var prior = calls[index];
                    calls[index] = prior with
                    {
                        Status = "completed",
                        Result = result.Result?.ToString()
                    };
                }
                else
                {
                    calls.Add(new ConversationToolActivitySnapshot(
                        result.CallId,
                        string.Empty,
                        "completed",
                        null,
                        null,
                        result.Result?.ToString()));
                }
            }
        }

        return calls;
    }

    // Projects the raw message list to user/assistant non-empty-text snapshots. Shared by the
    // read path and PersistAssistantReferences so the assistant-index definition never drifts.
    private static List<ConversationMessageSnapshot> ProjectSnapshots(List<ChatMessage> messages)
    {
        var snapshots = new List<ConversationMessageSnapshot>();
        var assistantIndex = 0;

        foreach (var message in messages)
        {
            if (message.Role != ChatRole.User && message.Role != ChatRole.Assistant)
                continue;

            var content = message.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(content))
                continue;

            if (message.Role == ChatRole.Assistant)
            {
                snapshots.Add(new ConversationMessageSnapshot(
                    "assistant", content, assistantIndex));
                assistantIndex++;
            }
            else
            {
                var (userContent, agentAddition) = SplitAgentAddition(content);
                snapshots.Add(new ConversationMessageSnapshot("user", userContent)
                {
                    AgentAddition = agentAddition
                });
            }
        }

        return snapshots;
    }

    // Older transcripts contain recall/learning preambles inline in the user message because
    // that is how the augmented prompt is sent to the model. Keep the persisted prompt intact,
    // but project those known AgentFox additions separately for user-facing clients.
    private static (string UserContent, string? AgentAddition) SplitAgentAddition(string content)
    {
        const string memoryHeader = "[Relevant context from long-term memory:]";
        const string baselineHeader = "[Shared learned baseline from previously verified attempts:]";
        const string baselineFooter =
            "Use this only when current preconditions match, and verify the result again.";

        var remainder = content;
        var additions = new List<string>();

        if (remainder.StartsWith(memoryHeader, StringComparison.Ordinal))
        {
            var boundary = FindBlankLine(remainder);
            if (boundary >= 0)
            {
                additions.Add(remainder[..boundary].Trim());
                remainder = remainder[SkipBlankLine(remainder, boundary)..];
            }
        }

        if (remainder.StartsWith(baselineHeader, StringComparison.Ordinal))
        {
            var footer = remainder.IndexOf(baselineFooter, StringComparison.Ordinal);
            if (footer >= 0)
            {
                var end = footer + baselineFooter.Length;
                additions.Add(remainder[..end].Trim());
                remainder = remainder[end..].TrimStart('\r', '\n');
            }
        }

        return additions.Count == 0
            ? (content, null)
            : (remainder.Trim(), string.Join(Environment.NewLine + Environment.NewLine, additions));
    }

    private static int FindBlankLine(string value)
    {
        var unix = value.IndexOf("\n\n", StringComparison.Ordinal);
        var windows = value.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (unix < 0) return windows;
        if (windows < 0) return unix;
        return Math.Min(unix, windows);
    }

    private static int SkipBlankLine(string value, int index) =>
        value.AsSpan(index).StartsWith("\r\n\r\n", StringComparison.Ordinal) ? index + 4 : index + 2;

    // Reads the sidecar into a map of assistantIndex → references. Empty when absent/unreadable.
    private Dictionary<int, List<ResearchReference>> LoadReferences(string conversationId)
    {
        var map = new Dictionary<int, List<ResearchReference>>();
        var path = ReferencesFilePath(conversationId);
        if (!File.Exists(path)) return map;

        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var line = JsonSerializer.Deserialize<ReferenceLine>(raw, _jsonOpts);
                if (line?.Items is { Count: > 0 })
                {
                    var valid = line.Items
                        .Where(item => ResearchReferenceScope.IsSafeHttpUrl(item.Url, out _))
                        .ToList();
                    if (valid.Count > 0) map[line.I] = valid;
                }
            }
            catch { /* malformed line — skip */ }
        }
        return map;
    }

    public void DeleteSession(string conversationId)
    {
        _cache.TryRemove(conversationId, out _);
        _messages.TryRemove(conversationId, out _);
        _writtenCounts.TryRemove(conversationId, out _);
        var path = FilePath(conversationId);
        if (File.Exists(path))
            File.Delete(path);

        var refsPath = ReferencesFilePath(conversationId);
        if (File.Exists(refsPath))
            File.Delete(refsPath);

        ClearSessionState(conversationId);
    }

    // ------------------------------------------------------------------
    // Crash-safe pending message (written before RunAsync, deleted after)
    // ------------------------------------------------------------------

    /// <summary>
    /// Atomically records the user message that is about to be processed.
    /// Must be called immediately before <c>agent.RunAsync</c> so that if the
    /// process crashes mid-LLM-call, the message is recoverable on the next startup.
    /// The file is removed by <see cref="ClearPendingUserMessage"/> once the
    /// turn completes and the session has been saved successfully.
    /// </summary>
    public void PersistIncomingUserMessage(string conversationId, string message)
    {
        var path = PendingFilePath(conversationId);
        EnsureParentDirectory(path);
        File.WriteAllText(path, message, Encoding.UTF8);
    }

    /// <summary>
    /// Removes the pending-message marker after a successful agent turn.
    /// No-op when no pending file exists.
    /// </summary>
    public void ClearPendingUserMessage(string conversationId)
    {
        var path = PendingFilePath(conversationId);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>
    /// Makes an interrupted user turn visible in the transcript before a queued
    /// follow-up can replace the crash-recovery marker. The history provider may
    /// already have stored the request, so this is deliberately idempotent for a
    /// trailing user message with the same text.
    /// </summary>
    public void PersistInterruptedUserMessage(string conversationId, string message)
    {
        SessionManager.EnsureSafeSessionId(conversationId);
        var text = message.Trim();
        if (text.Length == 0) return;

        var list = _messages.GetOrAdd(conversationId, _ => []);
        lock (list)
        {
            var snapshots = ProjectSnapshots(list);
            var last = snapshots.LastOrDefault();
            if (last?.Role == "user" && string.Equals(last.Content, text, StringComparison.Ordinal))
                return;

            list.Add(new ChatMessage(ChatRole.User, text));
        }
    }

    /// <summary>
    /// Returns the original user message that was being processed when the
    /// previous process terminated (detected via the <c>.pending</c> sidecar file),
    /// or <c>null</c> if no interrupted turn is detected.
    /// </summary>
    public string? GetLastUnrespondedUserMessage(string conversationId)
    {
        var path = PendingFilePath(conversationId);
        if (!File.Exists(path)) return null;

        var text = File.ReadAllText(path, Encoding.UTF8).Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    // ------------------------------------------------------------------
    // Provider state sidecar ({session}.md.state.json)
    // ------------------------------------------------------------------

    private sealed record PersistedSessionState(
        [property: JsonPropertyName("savedAt")]  DateTimeOffset SavedAt,
        [property: JsonPropertyName("stateBag")] JsonElement StateBag);

    /// <summary>
    /// Persists a slice of the session's AIContextProvider state (currently the todo list) beside
    /// the transcript, so outstanding work survives a process restart.
    ///
    /// IMPORTANT: callers must pass a filtered slice, never a whole serialized session. A full
    /// session blob also carries the ChatHistoryProvider's messages, and this store already owns
    /// message persistence through the .md file — writing both would restore every message twice.
    /// </summary>
    public void PersistSessionState(string conversationId, JsonElement stateBag)
    {
        SessionManager.EnsureSafeSessionId(conversationId);
        var path = StateFilePath(conversationId);
        EnsureParentDirectory(path);
        File.WriteAllText(path, SerializeStateSidecar(stateBag, DateTimeOffset.UtcNow), Encoding.UTF8);
    }

    /// <summary>
    /// Renders the sidecar payload. Shared with session import so the on-disk schema has a single
    /// definition. <paramref name="savedAt"/> is passed in rather than taken as "now" because an
    /// imported bundle must keep its ORIGINAL save time — that is what lets the agent recognise
    /// the restored work as stale and ask before resuming it.
    /// </summary>
    public static string SerializeStateSidecar(JsonElement stateBag, DateTimeOffset savedAt)
        => JsonSerializer.Serialize(new PersistedSessionState(savedAt, stateBag), _jsonOpts);

    /// <summary>File name suffix of the provider-state sidecar.</summary>
    public const string StateSidecarSuffix = ".state.json";

    /// <summary>File name suffix of the research-references sidecar (newline-delimited JSON).</summary>
    public const string ReferencesSidecarSuffix = ".refs.jsonl";

    /// <summary>
    /// Reads the persisted provider state for a conversation, or null when absent or unreadable.
    /// A corrupt sidecar is treated as absent: stale planner state must never block a session.
    /// </summary>
    public SessionStateSnapshot? ReadSessionState(string conversationId)
    {
        SessionManager.EnsureSafeSessionId(conversationId);
        var path = StateFilePath(conversationId);
        if (!File.Exists(path)) return null;

        try
        {
            var payload = JsonSerializer.Deserialize<PersistedSessionState>(
                File.ReadAllText(path, Encoding.UTF8), _jsonOpts);
            if (payload is null || payload.StateBag.ValueKind != JsonValueKind.Object)
                return null;

            return new SessionStateSnapshot(payload.StateBag.Clone(), payload.SavedAt);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Removes the persisted provider state (e.g. once its todo list is finished).</summary>
    public void ClearSessionState(string conversationId)
    {
        var path = StateFilePath(conversationId);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Provider-state sidecar path (<c>{session}.md.state.json</c>). Non-creating.</summary>
    private string StateFilePath(string conversationId) => ResolveFilePath(conversationId) + StateSidecarSuffix;

    /// <summary>Pending-message sidecar path (<c>{session}.md.pending</c>). Non-creating —
    /// write callers must ensure the directory exists first.</summary>
    private string PendingFilePath(string conversationId) => ResolveFilePath(conversationId) + ".pending";

    /// <summary>References sidecar path (<c>{session}.md.refs.jsonl</c>). Non-creating —
    /// write callers must ensure the directory exists first.</summary>
    private string ReferencesFilePath(string conversationId) => ResolveFilePath(conversationId) + ReferencesSidecarSuffix;

    /// <summary>Creates the parent directory for a sidecar/transcript file if it is missing.</summary>
    private static void EnsureParentDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    // ------------------------------------------------------------------
    // Session → conversationId registration
    // ------------------------------------------------------------------

    private void Register(AgentSession session, string conversationId)
        => _sessionIds.AddOrUpdate(session, new StrongBox<string>(conversationId));

    // ------------------------------------------------------------------
    // Write
    // ------------------------------------------------------------------

    private void AppendToFile(string conversationId, List<ChatMessage> messages, bool isNewFile)
    {
        var path = FilePath(conversationId);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, Encoding.UTF8);

        if (isNewFile)
            WriteTranscriptHeader(writer, conversationId);

        foreach (var msg in messages)
            WriteMessage(writer, msg);

        writer.Flush();
    }

    private static void WriteTranscriptHeader(TextWriter writer, string conversationId)
    {
        writer.WriteLine("---");
        writer.WriteLine($"sessionId: {conversationId}");
        writer.WriteLine($"createdAt: {DateTime.UtcNow:O}");
        writer.WriteLine("---");
        writer.WriteLine();
        writer.WriteLine("# Chat Log");
        writer.WriteLine();
    }

    private static void WriteMessage(TextWriter writer, ChatMessage msg)
    {
        writer.WriteLine($"### {msg.Role.Value}");
        writer.WriteLine();

        foreach (var content in msg.Contents)
        {
            switch (content)
            {
                case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                    writer.WriteLine(tc.Text);
                    break;

                // Reasoning is intentionally ephemeral: it may be streamed to the active web/CLI
                // surface, but must never enter transcripts, exports, or imported sessions.
                case TextReasoningContent:
                    break;

                // Attached file bytes stay out of the transcript — base64 would bloat it by
                // megabytes per image while telling a human reader nothing. The preceding
                // <attachment .../> label written by AttachmentSupport.ConvertForPrompt is
                // what records that a file was sent, and what it was.
                case DataContent:
                    break;

                case FunctionCallContent fc:
                    var call = new FunctionCallRecord(
                        fc.CallId, fc.Name,
                        fc.Arguments as Dictionary<string, object?> ?? fc.Arguments?.ToDictionary());
                    writer.WriteLine($"[tool_call] {JsonSerializer.Serialize(call, _jsonOpts)}");
                    break;

                case FunctionResultContent fr:
                    var res = new FunctionResultRecord(fr.CallId, fr.Result?.ToString());
                    writer.WriteLine($"[tool_result] {JsonSerializer.Serialize(res, _jsonOpts)}");
                    break;

                default:
                    var fallback = msg.Text;
                    if (!string.IsNullOrEmpty(fallback))
                        writer.WriteLine(fallback);
                    break;
            }
        }

        writer.WriteLine();
    }

    // ------------------------------------------------------------------
    // Parse
    // ------------------------------------------------------------------

    private static List<ChatMessage> ParseFile(string path)
    {
        var allText = File.ReadAllText(path, Encoding.UTF8);
        return ParseMarkdown(allText);
    }

    private static List<ChatMessage> ParseMarkdown(string allText)
    {
        var messages = new List<ChatMessage>();

        // Skip YAML frontmatter
        var bodyStart = 0;
        if (allText.StartsWith("---"))
        {
            var closeIdx = allText.IndexOf("\n---", 3);
            if (closeIdx >= 0)
                bodyStart = closeIdx + 4;
        }

        ChatRole? currentRole = null;
        var contentLines = new List<string>();

        foreach (var rawLine in allText[bodyStart..].Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("### ") && !line.StartsWith("#### "))
            {
                if (currentRole.HasValue)
                    FlushMessage(messages, currentRole.Value, contentLines);

                contentLines.Clear();
                currentRole = line[4..].Trim().ToLower() switch
                {
                    "user"      => ChatRole.User,
                    "assistant" => ChatRole.Assistant,
                    "system"    => ChatRole.System,
                    "tool"      => ChatRole.Tool,
                    var other   => new ChatRole(other)
                };
                continue;
            }

            if (currentRole.HasValue)
                contentLines.Add(line);
        }

        if (currentRole.HasValue)
            FlushMessage(messages, currentRole.Value, contentLines);

        return messages;
    }

    /// <summary>
    /// Builds a fresh Markdown transcript containing the raw conversation through the selected
    /// user-visible assistant reply. Tool calls and results before that reply remain intact.
    /// </summary>
    internal static string BuildForkTranscript(
        string transcriptMarkdown,
        int assistantIndex,
        string destinationConversationId)
    {
        if (assistantIndex < 0)
            throw new ArgumentOutOfRangeException(
                nameof(assistantIndex), "Assistant index must be non-negative.");

        SessionManager.EnsureSafeSessionId(destinationConversationId);
        var messages = ParseMarkdown(transcriptMarkdown);
        var currentAssistantIndex = -1;
        var rawCutoff = -1;

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role != ChatRole.Assistant ||
                string.IsNullOrWhiteSpace(message.Text))
                continue;

            currentAssistantIndex++;
            if (currentAssistantIndex == assistantIndex)
            {
                rawCutoff = i;
                break;
            }
        }

        if (rawCutoff < 0)
            throw new ArgumentOutOfRangeException(
                nameof(assistantIndex), "The selected assistant reply does not exist.");

        var builder = new StringBuilder();
        using var writer = new StringWriter(builder) { NewLine = "\n" };
        WriteTranscriptHeader(writer, destinationConversationId);
        foreach (var message in messages.Take(rawCutoff + 1))
            WriteMessage(writer, message);

        return builder.ToString();
    }

    private static void FlushMessage(List<ChatMessage> messages, ChatRole role, List<string> lines)
    {
        var contents = new List<AIContent>();
        var textBuf = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.StartsWith("[tool_call] "))
            {
                FlushText(contents, textBuf);
                try
                {
                    var r = JsonSerializer.Deserialize<FunctionCallRecord>(line[12..], _jsonOpts);
                    if (r != null)
                        contents.Add(new FunctionCallContent(r.CallId ?? "", r.Name ?? "", r.Arguments));
                }
                catch { /* malformed — skip */ }
            }
            else if (line.StartsWith("[tool_result] "))
            {
                FlushText(contents, textBuf);
                try
                {
                    var r = JsonSerializer.Deserialize<FunctionResultRecord>(line[14..], _jsonOpts);
                    if (r != null)
                        contents.Add(new FunctionResultContent(r.CallId ?? "", r.Result));
                }
                catch { /* malformed — skip */ }
            }
            else
            {
                textBuf.AppendLine(line);
            }
        }

        FlushText(contents, textBuf);

        if (contents.Count > 0)
            messages.Add(new ChatMessage(role, contents));
    }

    private static void FlushText(List<AIContent> contents, StringBuilder buf)
    {
        var text = buf.ToString().Trim();
        if (text.Length > 0)
            contents.Add(new TextContent(text));
        buf.Clear();
    }

    /// <summary>
    /// Resolves the .md file path for a conversation ID.
    /// IDs may include a single directory separator for sub-agent scoping
    /// (e.g. "agentfox/sa_abc123" → {directory}/agentfox/sa_abc123.md).
    /// </summary>
    // Resolves the .md path for a conversation WITHOUT creating anything on disk. Read-only
    // callers must use this so that merely inspecting a conversation never litters empty
    // session sub-directories (which then look like ghost sessions).
    private string ResolveFilePath(string id)
    {
        SessionManager.EnsureSafeSessionId(id);
        var rel = id.Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(_directory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(_directory, rel + ".md"));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Conversation ID resolves outside the session directory.", nameof(id));
        return path;
    }

    private string FilePath(string id)
    {
        var path = ResolveFilePath(id);
        // Ensure the sub-directory exists before callers try to write
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        return path;
    }

    // ------------------------------------------------------------------
    // JSON records for tool call / result lines
    // ------------------------------------------------------------------

    private sealed record FunctionCallRecord(
        [property: JsonPropertyName("callId")]    string? CallId,
        [property: JsonPropertyName("name")]      string? Name,
        [property: JsonPropertyName("arguments")] Dictionary<string, object?>? Arguments);

    private sealed record FunctionResultRecord(
        [property: JsonPropertyName("callId")] string? CallId,
        [property: JsonPropertyName("result")] string? Result);
}

public sealed record ConversationMessageSnapshot(
    string Role,
    string Content,
    int? AssistantIndex = null)
{
    public IReadOnlyList<ResearchReference> References { get; init; } = [];
    public string? AgentAddition { get; init; }
}

public sealed record ConversationToolActivitySnapshot(
    string CallId,
    string ToolName,
    string Status,
    long? DurationMs,
    object? Arguments,
    string? Result);

/// <summary>
/// Persisted AIContextProvider state for a conversation, with the wall-clock time it was written.
/// <paramref name="SavedAt"/> is what lets a restored todo list be judged stale.
/// </summary>
public sealed record SessionStateSnapshot(JsonElement StateBag, DateTimeOffset SavedAt);
