using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PageAgent.Models;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PageAgent.Core;

/// <summary>
/// Uses the injected <see cref="IChatClient"/> to decide the next browser action
/// given the current goal, page state, history, and memory.
///
/// The planner sends a structured prompt and parses the LLM's JSON response into
/// an <see cref="AgentAction"/>. Falls back to "analyze" if the response cannot be parsed.
/// </summary>
public sealed partial class LlmPlanner
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<LlmPlanner> _logger;

    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    private const string SystemPrompt = """
        You are an autonomous web browsing agent. Decide the single best next action to
        make progress toward the given goal.

        AVAILABLE ACTIONS
        -----------------
        search   — Search Google for information. Populate "query".
        open     — Navigate directly to a known URL. Populate "url".
        click    — Click a link/button by its visible text. Populate "selector".
        analyze  — Inspect the current page structure (headings + links). No extra fields.
        extract  — Extract the full visible text of the current page. No extra fields.
        done     — Goal is satisfied; stop browsing. Populate "reason" with a summary.

        RULES
        -----
        - Output ONLY valid JSON. No prose, no markdown fences.
        - Never revisit a URL already in the VISITED URLS list.
        - Prefer "analyze" on a newly loaded page before clicking.
        - Choose "done" as soon as the goal can be answered from collected notes.
        - When clicking, use the exact visible link text from the page's link list.

        OUTPUT FORMAT (strict JSON, no extra keys)
        ------------------------------------------
        {
          "action":   "search|open|click|analyze|extract|done",
          "query":    "search terms (search only)",
          "url":      "https://... (open only)",
          "selector": "visible link text (click only)",
          "reason":   "one-line rationale"
        }
        """;

    public LlmPlanner(IChatClient chatClient, ILogger<LlmPlanner> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    /// <summary>
    /// Asks the LLM to decide the next action given full context.
    /// </summary>
    /// <param name="goal">The user's original goal.</param>
    /// <param name="history">All steps executed so far.</param>
    /// <param name="currentPage">Latest snapshot of the page DOM.</param>
    /// <param name="memory">Visited URLs and accumulated notes.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentAction> DecideNextActionAsync(
        string goal,
        IReadOnlyList<StepResult> history,
        PageAnalysis currentPage,
        AgentMemory memory,
        CancellationToken ct = default)
    {
        var userMessage = BuildContextPrompt(goal, history, currentPage, memory);

        _logger.LogDebug("Requesting planning decision from LLM (step {Next})...",
            history.Count + 1);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, userMessage),
        };

        ChatResponse response;
        try
        {
            response = await _chatClient.GetResponseAsync(messages, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("LLM call failed: {Error}. Falling back to 'analyze'.", ex.Message);
            return new AgentAction { Action = "analyze", Reason = "LLM unavailable — retrying with analyze" };
        }

        var raw = (response.Text ?? string.Empty).Trim();
        _logger.LogDebug("LLM response: {Raw}", raw.Length > 300 ? raw[..300] + "…" : raw);

        return ParseAction(raw);
    }

    // ── Prompt construction ──────────────────────────────────────────────────

    private static string BuildContextPrompt(
        string goal,
        IReadOnlyList<StepResult> history,
        PageAnalysis page,
        AgentMemory memory)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"GOAL: {goal}");
        sb.AppendLine();

        // ── Current page ────────────────────────────────────────────────────
        sb.AppendLine("CURRENT PAGE:");
        sb.AppendLine($"  URL   : {(page.IsEmpty ? "(not navigated yet)" : page.Url)}");
        sb.AppendLine($"  Title : {page.Title}");

        if (page.Headings.Count > 0)
            sb.AppendLine($"  Headings: {string.Join(" | ", page.Headings.Take(5))}");

        if (page.Links.Count > 0)
        {
            sb.AppendLine("  Links (text → href):");
            foreach (var link in page.Links.Take(12))
                sb.AppendLine($"    • \"{link.Text}\" → {link.Href}");
        }

        // ── Memory ──────────────────────────────────────────────────────────
        if (memory.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("NOTES COLLECTED SO FAR:");
            foreach (var note in memory.Notes.TakeLast(6))
                sb.AppendLine($"  - {note}");
        }

        if (memory.VisitedUrls.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"VISITED URLS (do not revisit): {string.Join(", ", memory.VisitedUrls.TakeLast(8))}");
        }

        // ── Recent history ──────────────────────────────────────────────────
        if (history.Count > 0)
        {
            sb.AppendLine();
            var recent = history.TakeLast(5).ToList();
            sb.AppendLine($"LAST {recent.Count} STEP(S):");
            foreach (var s in recent)
            {
                var outcome = s.Success
                    ? s.Output.Length > 160 ? s.Output[..160] + "…" : s.Output
                    : $"FAILED — {s.Error}";
                sb.AppendLine($"  [{s.Step:D2}] {s.Action.Action} → {outcome}");
            }
        }

        sb.AppendLine();
        sb.Append("Decide the next single action:");

        return sb.ToString();
    }

    // ── Response parsing ─────────────────────────────────────────────────────

    private AgentAction ParseAction(string raw)
    {
        var json = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json))
        {
            _logger.LogWarning("LLM returned no parseable JSON. Raw: {Raw}", raw);
            return Fallback();
        }

        try
        {
            var action = JsonSerializer.Deserialize<AgentAction>(json, _jsonOpts);
            if (action is not null && !string.IsNullOrWhiteSpace(action.Action))
                return action;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("JSON parse error: {Error}. Input: {Json}", ex.Message, json);
        }

        return Fallback();
    }

    private static AgentAction Fallback() =>
        new() { Action = "analyze", Reason = "Fallback: could not parse LLM response" };

    /// <summary>
    /// Extracts a JSON object from the LLM response, handling:
    /// 1. Bare JSON objects
    /// 2. JSON wrapped in ```json ... ``` fences
    /// 3. JSON embedded after prose text
    /// </summary>
    private static string ExtractJson(string text)
    {
        // Strip markdown code fences
        var fenceMatch = MarkdownFenceRegex().Match(text);
        if (fenceMatch.Success)
            return fenceMatch.Groups[1].Value.Trim();

        // Find first { … last }
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
            return text[start..(end + 1)];

        return text.Trim();
    }

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownFenceRegex();
}
