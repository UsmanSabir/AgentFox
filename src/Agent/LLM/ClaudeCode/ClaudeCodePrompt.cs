using System.Text;
using Microsoft.Extensions.AI;

namespace AgentFox.LLM.ClaudeCode;

/// <summary>
/// Turns a chat request into what one CLI call takes: a system prompt and one user message on stdin. Each call is
/// stateless — the host owns the conversation (compaction, retries, model switches), so the whole history is sent
/// every time rather than resumed from a Claude session that would drift from it.
/// </summary>
public static class ClaudeCodePrompt
{
    public const string DefaultSystemPrompt = "You are a helpful assistant.";

    public sealed record Rendered(string SystemPrompt, string UserMessage);

    public static Rendered Render(IEnumerable<ChatMessage> messages)
    {
        var system = new StringBuilder();
        var turns = new List<(ChatRole Role, string Text)>();
        foreach (var message in messages)
        {
            var text = TextOf(message);
            if (message.Role == ChatRole.System)
            {
                if (text.Length == 0) continue;
                if (system.Length > 0) system.Append("\n\n");
                system.Append(text);
            }
            else if (message.Role == ChatRole.User || message.Role == ChatRole.Assistant)
            {
                if (text.Length > 0) turns.Add((message.Role, text));
            }
            // Tool messages are dropped: this provider offers no tools, so a tool exchange can only come from an
            // earlier turn on another provider, and its gist is already in the assistant's reply that followed.
        }

        var systemPrompt = system.Length > 0 ? system.ToString() : DefaultSystemPrompt;
        if (turns.Count == 0)
            throw new ArgumentException("The request has no user message.", nameof(messages));
        if (turns.Count == 1 && turns[0].Role == ChatRole.User)
            return new Rendered(systemPrompt, turns[0].Text);

        var body = new StringBuilder("The conversation so far, oldest first:\n\n");
        for (var i = 0; i < turns.Count - 1; i++)
            body.Append(turns[i].Role == ChatRole.User ? "<user>\n" : "<assistant>\n")
                .Append(turns[i].Text)
                .Append(turns[i].Role == ChatRole.User ? "\n</user>\n\n" : "\n</assistant>\n\n");
        var last = turns[^1];
        if (last.Role == ChatRole.User)
            body.Append("Reply to this latest message from the user:\n\n").Append(last.Text);
        else
            body.Append("<assistant>\n").Append(last.Text).Append("\n</assistant>\n\nContinue your reply.");
        return new Rendered(systemPrompt, body.ToString());
    }

    private static string TextOf(ChatMessage message)
    {
        var sb = new StringBuilder();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent t when !string.IsNullOrEmpty(t.Text):
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(t.Text);
                    break;
                case DataContent or UriContent:
                    // Said rather than dropped silently: the model must not answer as if it saw the file.
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append("[An attachment was included here, but this model cannot read attachments.]");
                    break;
            }
        }
        return sb.ToString().Trim();
    }
}
