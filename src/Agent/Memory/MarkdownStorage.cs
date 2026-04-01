using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentFox.Memory;

// ---------------------------------------------------------------------------
// MarkdownConversationStore
//
// Unified session store + chat history provider.
// - Implements IConversationStore: session lifecycle (create, cache, restore).
// - Extends ChatHistoryProvider: append-only markdown files hold all messages.
//
// File layout (one .md per conversation in the configured directory):
//
//   ---
//   sessionId: main
//   createdAt: 2025-01-01T00:00:00Z
//   ---
//
//   # Chat Log
//
//   **user**: Hello
//
//   **assistant**: Hi there!
//
// On restart: GetSession returns null (cache miss), ProcessAsync creates a fresh
// AgentSession with ConversationId in the StateBag, then RunAsync calls
// ProvideChatHistoryAsync which reads the existing file and restores the full
// message history automatically.
// ---------------------------------------------------------------------------

public sealed class MarkdownConversationStore : ChatHistoryProvider, IConversationStore
{
    private readonly ConcurrentDictionary<string, AgentSession> _cache = new();
    private readonly string _directory;
    private readonly ProviderSessionState<MarkdownStorageState> _sessionState;

    public MarkdownConversationStore(string directory)
    {
        _directory = directory;
        System.IO.Directory.CreateDirectory(directory);

        _sessionState = new ProviderSessionState<MarkdownStorageState>(
            stateInitializer: session =>
            {
                // Prefer an already-initialised state (same session, second call).
                if (session.StateBag.TryGetValue<MarkdownStorageState>("MarkdownHistory", out var existing)
                    && existing != null)
                    return existing;

                session.StateBag.TryGetValue<string>("ConversationId", out var id);
                if (string.IsNullOrWhiteSpace(id))
                    id = Guid.NewGuid().ToString();

                return new MarkdownStorageState
                {
                    SessionId = id,
                    FilePath = Path.Combine(_directory, $"{id}.md")
                };
            },
            stateKey: "MarkdownHistory");
    }

    public string Directory => _directory;

    // ------------------------------------------------------------------
    // IConversationStore
    // ------------------------------------------------------------------

    public AgentSession? GetSession(string conversationId)
    {
        _cache.TryGetValue(conversationId, out var session);
        return session;
    }

    public void SaveSession(string conversationId, AgentSession session)
        => _cache[conversationId] = session;

    /// <summary>
    /// True when the session is cached in memory OR a markdown file exists on
    /// disk from a previous run (messages can be restored without further action).
    /// </summary>
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
        var path = FilePath(conversationId);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>
    /// MarkdownConversationStore restores messages via ChatHistoryProvider.ProvideChatHistoryAsync
    /// (triggered automatically on RunAsync). No manual restore step is needed.
    /// </summary>
    public Task RestoreAsync(string conversationId, AgentSession session)
        => Task.CompletedTask;

    // ------------------------------------------------------------------
    // ChatHistoryProvider — message persistence
    // ------------------------------------------------------------------

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);

        if (!File.Exists(state.FilePath))
            return [];

        var (_, bodyStart) = await MarkdownSessionReader.ReadHeaderAsync<AgentSessionHeader>(state.FilePath);
        var messages = new List<ChatMessage>();

        await foreach (var line in MarkdownSessionReader
            .ReadBodyLinesAsync(state.FilePath, bodyStart)
            .WithCancellation(cancellationToken))
        {
            var msg = ParseLine(line);
            if (msg != null)
                messages.Add(msg);
        }

        return messages;
    }

    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        bool isNew = !File.Exists(state.FilePath);

        using var stream = new FileStream(
            state.FilePath, FileMode.Append, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, Encoding.UTF8);

        if (isNew)
        {
            await writer.WriteLineAsync("---");
            await writer.WriteLineAsync($"sessionId: {state.SessionId}");
            await writer.WriteLineAsync($"createdAt: {state.CreatedAt:O}");
            await writer.WriteLineAsync("---");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("# Chat Log");
            await writer.WriteLineAsync();
        }

        foreach (var msg in context.RequestMessages)
            await WriteMessageAsync(writer, msg);

        foreach (var msg in context.ResponseMessages)
            await WriteMessageAsync(writer, msg);

        await writer.FlushAsync();
    }

    // ------------------------------------------------------------------

    private static async Task WriteMessageAsync(StreamWriter writer, ChatMessage msg)
    {
        await writer.WriteLineAsync($"**{msg.Role}**: {msg.Text?.Replace("\n", "  \n")}");
        await writer.WriteLineAsync();
    }

    private static ChatMessage? ParseLine(string line)
    {
        var match = Regex.Match(line, @"^\*\*(.*?)\*\*:\s*(.*)");
        if (!match.Success) return null;
        return new ChatMessage(new ChatRole(match.Groups[1].Value.ToLower()), match.Groups[2].Value);
    }

    private string FilePath(string conversationId)
        => Path.Combine(_directory, $"{conversationId}.md");
}

// ---------------------------------------------------------------------------
// MarkdownChatHistoryProvider — kept for standalone use without IConversationStore.
// For new code prefer MarkdownConversationStore which unifies both concerns.
// ---------------------------------------------------------------------------

public class MarkdownStorageState
{
    public string SessionId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class MarkdownChatHistoryProvider : ChatHistoryProvider
{
    private readonly string _baseDirectory;
    private readonly ProviderSessionState<MarkdownStorageState> _sessionState;

    public MarkdownChatHistoryProvider(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
        System.IO.Directory.CreateDirectory(_baseDirectory);

        _sessionState = new ProviderSessionState<MarkdownStorageState>(
            stateInitializer: session =>
            {
                string? id = null;
                if (session.StateBag.TryGetValue<string>("ConversationId", out var sessionId))
                    id = sessionId;

                if (string.IsNullOrWhiteSpace(id))
                    id = Guid.NewGuid().ToString();

                var stateExist = session.StateBag.TryGetValue<MarkdownStorageState>("MarkdownHistory", out var state);
                if (stateExist && state != null)
                    id = state.SessionId;

                return new MarkdownStorageState
                {
                    SessionId = id,
                    FilePath = Path.Combine(_baseDirectory, $"{id}.md")
                };
            },
            stateKey: "MarkdownHistory");
    }

    public string BaseDirectory => _baseDirectory;

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);

        if (!File.Exists(state.FilePath))
            return [];

        var (_, bodyStart) = await MarkdownSessionReader.ReadHeaderAsync<AgentSessionHeader>(state.FilePath);
        var messages = new List<ChatMessage>();

        await foreach (var line in MarkdownSessionReader
            .ReadBodyLinesAsync(state.FilePath, bodyStart)
            .WithCancellation(cancellationToken))
        {
            var msg = ParseLine(line);
            if (msg != null) messages.Add(msg);
        }

        return messages;
    }

    private static ChatMessage? ParseLine(string line)
    {
        var match = Regex.Match(line, @"^\*\*(.*?)\*\*:\s*(.*)");
        if (!match.Success) return null;
        return new ChatMessage(new ChatRole(match.Groups[1].Value.ToLower()), match.Groups[2].Value);
    }

    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        bool isNew = !File.Exists(state.FilePath);

        using var stream = new FileStream(
            state.FilePath, FileMode.Append, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, Encoding.UTF8);

        if (isNew)
        {
            await writer.WriteLineAsync("---");
            await writer.WriteLineAsync($"sessionId: {state.SessionId}");
            await writer.WriteLineAsync($"createdAt: {DateTime.UtcNow:O}");
            await writer.WriteLineAsync("---");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("# Chat Log");
            await writer.WriteLineAsync();
        }

        foreach (var msg in context.RequestMessages)
        {
            await writer.WriteLineAsync($"**{msg.Role}**: {msg.Text?.Replace("\n", "  \n")}");
            await writer.WriteLineAsync();
        }

        foreach (var msg in context.ResponseMessages)
        {
            await writer.WriteLineAsync($"**{msg.Role}**: {msg.Text?.Replace("\n", "  \n")}");
            await writer.WriteLineAsync();
        }

        await writer.FlushAsync();
    }
}
