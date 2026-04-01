using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

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

        _messages.TryGetValue(id, out var list);
        return ValueTask.FromResult((list ?? []) as IEnumerable<ChatMessage>);
    }

    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context, CancellationToken cancellationToken)
    {
        if (!TryGetId(context.Session, out var id))
            return ValueTask.CompletedTask;

        var list = _messages.GetOrAdd(id, _ => []);

        foreach (var msg in context.RequestMessages)
            if (msg.Role != ChatRole.System)
                list.Add(msg);

        foreach (var msg in context.ResponseMessages)
            list.Add(msg);

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
        WriteIndented = false
    };

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

        int written = _writtenCounts.GetOrAdd(conversationId, 0);
        if (messages.Count <= written)
            return;

        var delta = messages.Skip(written).ToList();
        bool isNewFile = written == 0 && !File.Exists(FilePath(conversationId));
        AppendToFile(conversationId, delta, isNewFile);
        _writtenCounts[conversationId] = messages.Count;
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
        => _cache.ContainsKey(conversationId) || File.Exists(FilePath(conversationId));

    public IEnumerable<string> GetAllSessionIds()
    {
        var fromFiles = System.IO.Directory.EnumerateFiles(_directory, "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>();
        return _cache.Keys.Union(fromFiles).Distinct();
    }

    public void DeleteSession(string conversationId)
    {
        _cache.TryRemove(conversationId, out _);
        _messages.TryRemove(conversationId, out _);
        _writtenCounts.TryRemove(conversationId, out _);
        var path = FilePath(conversationId);
        if (File.Exists(path))
            File.Delete(path);
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
        {
            writer.WriteLine("---");
            writer.WriteLine($"sessionId: {conversationId}");
            writer.WriteLine($"createdAt: {DateTime.UtcNow:O}");
            writer.WriteLine("---");
            writer.WriteLine();
            writer.WriteLine("# Chat Log");
            writer.WriteLine();
        }

        foreach (var msg in messages)
            WriteMessage(writer, msg);

        writer.Flush();
    }

    private static void WriteMessage(StreamWriter writer, ChatMessage msg)
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
        var messages = new List<ChatMessage>();
        var allText = File.ReadAllText(path, Encoding.UTF8);

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

    private string FilePath(string id) => Path.Combine(_directory, $"{id}.md");

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
