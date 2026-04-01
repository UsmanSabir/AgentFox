using Microsoft.Agents.AI;
using System.Text;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentFox.Memory;

public class MarkdownStorageState
{
    public MarkdownStorageState()
    {
        
    }
    public string SessionId { get; set; }
    public string FilePath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class MarkdownChatHistoryProvider : ChatHistoryProvider
{
    private readonly string _baseDirectory;
    private readonly ProviderSessionState<MarkdownStorageState> _sessionState;

    public MarkdownChatHistoryProvider(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
        Directory.CreateDirectory(_baseDirectory);

        // FIX CS7036: Added "MarkdownHistory" as the required 'stateKey'
        _sessionState = new ProviderSessionState<MarkdownStorageState>(
            stateInitializer: session =>
            {
                string? id=null;
                if (session.StateBag.TryGetValue<string>("ConversationId", out var sessionId))
                {
                    id = sessionId;
                }

                if (string.IsNullOrWhiteSpace(id))
                {
                    id = Guid.NewGuid().ToString();
                }
                //session.StateBag.SetValue("ConversationId", id);
                //session.StateBag.SetValue("CreatedAt", DateTime.UtcNow.ToString("O")); //ISO format
                var stateExist = session.StateBag.TryGetValue<MarkdownStorageState>("MarkdownHistory", out var state);
                if (stateExist && state!=null)
                {
                    id = state.SessionId;
                }

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
            return [];// Enumerable.Empty<ChatMessage>();

        // Use your logic to find the body start
        var (metadata, bodyStart) = await MarkdownSessionReader.ReadHeaderAsync<AgentSessionHeader>(state.FilePath);

        var messages = new List<ChatMessage>();

        // Use your IAsyncEnumerable to parse messages without loading the whole file
        await foreach (var line in MarkdownSessionReader.ReadBodyLinesAsync(state.FilePath, bodyStart).WithCancellation(cancellationToken))
        {
            var msg = ParseSingleLineToMessage(line);
            if (msg != null) messages.Add(msg);
        }

        return messages;
    }

    private ChatMessage? ParseSingleLineToMessage(string line)
    {
        // Your logic to turn "**user**: Hello" into a ChatMessage object
        var match = Regex.Match(line, @"^\*\*(.*?)\*\*:\s*(.*)");
        if (!match.Success) return null;

        return new ChatMessage(new ChatRole(match.Groups[1].Value.ToLower()), match.Groups[2].Value);
    }

    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        bool isNewFile = !File.Exists(state.FilePath);

        // Use a FileStream with Append access to ensure we only add to the end
        using var stream = new FileStream(state.FilePath, FileMode.Append, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, Encoding.UTF8);

        // 1. If it's a new file, write the YAML Frontmatter and Header first
        if (isNewFile)
        {
            await writer.WriteLineAsync("---");
            await writer.WriteLineAsync($"sessionId: {state.SessionId}");
            await writer.WriteLineAsync($"createdAt: {DateTime.UtcNow:O}");
            await writer.WriteLineAsync("---");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("# Chat Log");
            await writer.WriteLineAsync();
        }

        // 2. Append the Request Messages (User input)
        foreach (var msg in context.RequestMessages)
        {
            await writer.WriteLineAsync($"**{msg.Role}**: {msg.Text?.Replace("\n", "  \n")}");
            await writer.WriteLineAsync();
        }

        // 3. Append the Response Messages (Agent output)
        foreach (var msg in context.ResponseMessages)
        {
            await writer.WriteLineAsync($"**{msg.Role}**: {msg.Text?.Replace("\n", "  \n")}");
            await writer.WriteLineAsync();
        }

        // StreamWriter flush ensures data is physically written to disk
        await writer.FlushAsync();

        //var invokingContext = new InvokingContext(context.Agent, context.Session, context.RequestMessages);

        //var existing = await ProvideChatHistoryAsync(invokingContext, cancellationToken);

        //var allMessages = existing.Concat(context.ResponseMessages).ToList();

        //var sb = new StringBuilder();
        //sb.AppendLine("---");
        //sb.AppendLine($"sessionId: {state.SessionId}");
        //sb.AppendLine($"createdAt: {state.CreatedAt:O}");
        //sb.AppendLine($"lastUpdated: {DateTime.UtcNow:O}");
        //sb.AppendLine("---");
        //sb.AppendLine();
        //sb.AppendLine("# Chat Log");
        //sb.AppendLine();

        //foreach (var msg in allMessages)
        //{
        //    sb.AppendLine($"**{msg.Role}**: {msg.Text?.Replace("\n", "  \n")}");
        //    sb.AppendLine();
        //}

        //await File.WriteAllTextAsync(state.FilePath, sb.ToString(), cancellationToken);
    }

    private IEnumerable<ChatMessage> ParseMarkdownToMessages(string markdown)
    {
        var messages = new List<ChatMessage>();
        var content = Regex.Replace(markdown, @"^---[\s\S]*?---", "").Trim();
        var blocks = content.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in blocks)
        {
            if (block.StartsWith("#")) continue;

            var match = Regex.Match(block, @"^\*\*(.*?)\*\*:\s*(.*)", RegexOptions.Singleline);
            if (match.Success)
            {
                var role = new ChatRole(match.Groups[1].Value.ToLower());
                var text = match.Groups[2].Value.Trim();
                messages.Add(new ChatMessage(role, text));
            }
        }

        return messages;
    }
}