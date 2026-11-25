using System.Text.Json;

namespace AgentFox.LLM.Codex;

/// <summary>One line of <c>codex exec --json</c>, reduced to what the provider uses.</summary>
public abstract record CodexEvent;

public sealed record CodexThreadStarted(string? ThreadId) : CodexEvent;

/// <summary>A whole agent message. Codex sends no token deltas, and a turn can carry several: a preamble ("I'll read
/// the file…") and then the answer, so the provider keeps the LAST one (measured 2026-11-25).</summary>
public sealed record CodexAgentMessage(string Text) : CodexEvent;

/// <summary>The model reached for a tool (a command, a file change, a search). With the tool features disabled this
/// should never appear; when it does it is logged, and nothing it produced is shown.</summary>
public sealed record CodexToolAttempt(string ItemType, string? Status) : CodexEvent;

public sealed record CodexTurnCompleted(long? InputTokens, long? CachedInputTokens, long? OutputTokens) : CodexEvent;

public sealed record CodexFailed(string Message) : CodexEvent;

/// <summary>Parses JSONL lines. Pure: an unknown, future or malformed line yields null and is skipped.</summary>
public static class CodexStream
{
    public static CodexEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            switch (Str(root, "type"))
            {
                case "thread.started":
                    return new CodexThreadStarted(Str(root, "thread_id"));
                case "item.completed" or "item.started" or "item.updated":
                    if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
                    var itemType = Str(item, "type");
                    if (itemType is null or "reasoning") return null;
                    if (itemType == "agent_message")
                        return Str(root, "type") == "item.completed" && Str(item, "text") is { Length: > 0 } text
                            ? new CodexAgentMessage(text) : null;
                    if (itemType == "error")
                    {
                        var message = Str(item, "message");
                        // CLI 0.172.0 reports this on EVERY call where its optional code-mode helper is absent (measured
                        // 2026-11-25; switching code_mode or code_mode_host off only rewords it). It is a notice, not a
                        // failure, and the helper is deliberately not shipped: Code Mode would hand the model a code
                        // runtime, a way to act that the disabled features exist to remove. Kept as a failure, it would
                        // become the reason shown for an unrelated failure, such as a timeout.
                        if (message?.StartsWith("Code Mode is unavailable", StringComparison.Ordinal) == true) return null;
                        return new CodexFailed(message ?? "Codex reported an error.");
                    }
                    return new CodexToolAttempt(itemType, Str(item, "status"));
                case "turn.completed":
                    if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                        return new CodexTurnCompleted(null, null, null);
                    return new CodexTurnCompleted(Long(usage, "input_tokens"), Long(usage, "cached_input_tokens"),
                        Long(usage, "output_tokens"));
                case "turn.failed":
                    return new CodexFailed(root.TryGetProperty("error", out var error)
                        ? Str(error, "message") ?? "The Codex turn failed."
                        : "The Codex turn failed.");
                case "error":
                    return new CodexFailed(Str(root, "message") ?? "Codex reported an error.");
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;
}
