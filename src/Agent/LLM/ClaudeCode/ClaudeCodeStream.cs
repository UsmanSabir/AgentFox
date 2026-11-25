using System.Text.Json;

namespace AgentFox.LLM.ClaudeCode;

/// <summary>One line of the CLI's stream-json output, reduced to what the provider uses.</summary>
public abstract record ClaudeCodeEvent;

public sealed record ClaudeCodeInit(string? Model, string? Version, string? ApiKeySource) : ClaudeCodeEvent;

/// <summary>Incremental reply text (<c>stream_event</c> / <c>content_block_delta</c> / <c>text_delta</c>).</summary>
public sealed record ClaudeCodeTextDelta(string Text) : ClaudeCodeEvent;

/// <summary>A whole assistant message's text. Only used when no delta arrived, or text is said twice.</summary>
public sealed record ClaudeCodeAssistantText(string Text) : ClaudeCodeEvent;

public sealed record ClaudeCodeRetry(int Attempt, int? MaxRetries, int? ErrorStatus) : ClaudeCodeEvent;

/// <summary>
/// The subscription's usage, as the CLI reports it (<c>rate_limit_event</c>). NOT documented — observed 2026-11-25 —
/// so it is shown when present and never required.
/// </summary>
public sealed record ClaudeCodeUsageWindows(
    string? Status, string? LimitType, double? Utilization, DateTimeOffset? ResetsAt,
    double? FiveHourUtilization, DateTimeOffset? FiveHourResetsAt,
    double? SevenDayUtilization, DateTimeOffset? SevenDayResetsAt) : ClaudeCodeEvent;

public sealed record ClaudeCodeResult(
    bool IsError, string? Subtype, string? Text, string? StructuredOutputJson, int? ApiErrorStatus,
    long? InputTokens, long? OutputTokens, string? SessionId) : ClaudeCodeEvent;

/// <summary>Parses stream-json lines. Pure: an unknown, future or malformed line yields null and is skipped.</summary>
public static class ClaudeCodeStream
{
    public static ClaudeCodeEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return Str(root, "type") switch
            {
                "system" => System(root),
                "stream_event" => StreamEvent(root),
                "assistant" => Assistant(root),
                "rate_limit_event" => RateLimit(root),
                "result" => Result(root),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ClaudeCodeEvent? System(JsonElement root) => Str(root, "subtype") switch
    {
        "init" => new ClaudeCodeInit(Str(root, "model"), Str(root, "claude_code_version"), Str(root, "apiKeySource")),
        "api_retry" => new ClaudeCodeRetry(Int(root, "attempt") ?? 0, Int(root, "max_retries"), Int(root, "error_status")),
        _ => null,
    };

    private static ClaudeCodeEvent? StreamEvent(JsonElement root)
    {
        // Only text deltas: thinking and signature deltas are the model's reasoning and are never shown.
        if (!root.TryGetProperty("event", out var ev) || Str(ev, "type") != "content_block_delta") return null;
        if (!ev.TryGetProperty("delta", out var delta) || Str(delta, "type") != "text_delta") return null;
        var text = Str(delta, "text");
        return string.IsNullOrEmpty(text) ? null : new ClaudeCodeTextDelta(text);
    }

    private static ClaudeCodeEvent? Assistant(JsonElement root)
    {
        if (root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String)
            return null; // a subagent's message, not the reply
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) return null;
        var text = string.Concat(content.EnumerateArray()
            .Where(c => Str(c, "type") == "text").Select(c => Str(c, "text") ?? ""));
        return text.Length == 0 ? null : new ClaudeCodeAssistantText(text);
    }

    private static ClaudeCodeEvent? RateLimit(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit_info", out var info) || info.ValueKind != JsonValueKind.Object) return null;
        double? fiveU = null, sevenU = null;
        DateTimeOffset? fiveR = null, sevenR = null;
        if (info.TryGetProperty("unifiedWindows", out var windows) && windows.ValueKind == JsonValueKind.Object)
        {
            if (windows.TryGetProperty("five_hour", out var five)) { fiveU = Dbl(five, "utilization"); fiveR = Unix(five, "resetsAt"); }
            if (windows.TryGetProperty("seven_day", out var seven)) { sevenU = Dbl(seven, "utilization"); sevenR = Unix(seven, "resetsAt"); }
        }
        return new ClaudeCodeUsageWindows(Str(info, "status"), Str(info, "rateLimitType"), Dbl(info, "utilization"),
            Unix(info, "resetsAt"), fiveU, fiveR, sevenU, sevenR);
    }

    private static ClaudeCodeEvent Result(JsonElement root)
    {
        long? input = null, output = null;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input = Long(usage, "input_tokens") is { } i
                ? i + (Long(usage, "cache_read_input_tokens") ?? 0) + (Long(usage, "cache_creation_input_tokens") ?? 0)
                : null;
            output = Long(usage, "output_tokens");
        }
        string? structured = root.TryGetProperty("structured_output", out var so)
                             && so.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? so.GetRawText()
            : null;
        return new ClaudeCodeResult(
            IsError: root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True,
            Subtype: Str(root, "subtype"),
            Text: Str(root, "result"),
            StructuredOutputJson: structured,
            ApiErrorStatus: Int(root, "api_error_status"),
            InputTokens: input,
            OutputTokens: output,
            SessionId: Str(root, "session_id"));
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;

    private static double? Dbl(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;

    private static DateTimeOffset? Unix(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out var s) && s > 0 ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
}
