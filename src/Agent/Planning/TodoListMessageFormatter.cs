using System.Text;
using Microsoft.Agents.AI;

namespace AgentFox.Planning;

/// <summary>
/// Keeps the framework's synthetic todo context message useful without exposing an
/// empty or completed-only list in the conversation transcript.
/// </summary>
internal static class TodoListMessageFormatter
{
    public static string Build(IReadOnlyList<TodoItem> items)
    {
        var remaining = items
            .Where(item => !item.IsComplete)
            .ToArray();

        if (remaining.Length == 0)
            return string.Empty;

        var builder = new StringBuilder("### Current todo list\n");
        foreach (var item in remaining)
        {
            builder.Append("- ")
                .Append(item.Id)
                .Append(" ")
                .Append(item.Title);

            if (!string.IsNullOrWhiteSpace(item.Description))
                builder.Append(": ").Append(item.Description);

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}
