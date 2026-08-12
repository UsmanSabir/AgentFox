using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AgentFox.Hitl;

/// <summary>
/// Indicates what triggered a HITL approval request.
/// </summary>
public enum HitlTrigger { Tool, Checkpoint }

/// <summary>
/// Describes a pending approval request sent to the human.
/// </summary>
public record HitlRequest(
    string ApprovalId,
    string SessionKey,
    string? ChannelId,
    HitlTrigger Trigger,
    string Description,
    string Details
);

/// <summary>
/// A free-form question the agent is blocked on, waiting for any human reply.
/// </summary>
public record HitlQuestion(
    string QuestionId,
    string SessionKey,
    string? ChannelId,
    string Question,
    DateTime CreatedAt
);

/// <summary>
/// The human's decision on an approval request.
/// </summary>
public record HitlDecision(bool Approved, string? Feedback);

/// <summary>
/// Manages Human-In-The-Loop gates for both structured approval (Mode 1)
/// and free-form input (Mode 2).
///
/// Mode 1 — Tool approval:
///   Agent is blocked inside ExecuteToolAsync until the user sends
///   /approve &lt;id&gt; or /reject &lt;id&gt; via a channel or CLI.
///
/// Mode 2 — Free-form checkpoint:
///   Agent calls request_human_input; execution blocks until a reply arrives on the
///   originating channel, or — when there is no channel — until someone answers the
///   question by id from any surface (CLI <c>/hitl reply</c>, web).
///
/// <para>
/// Every gate here is bounded. An unanswered gate used to block its caller forever, and its caller
/// is a command occupying a lane slot: the Main lane is serial, so one unanswered approval from a
/// sub-agent announcement froze the interactive prompt permanently, and three from cron killed all
/// scheduled work. Gates now expire into a safe default — reject for approvals, no-answer for
/// questions — so a lane always drains.
/// </para>
/// </summary>
public class HitlManager
{
    private sealed record HitlEntry(
        HitlRequest Request,
        TaskCompletionSource<HitlDecision> Gate,
        DateTime CreatedAt);

    private sealed record FreeFormEntry(
        HitlQuestion Question,
        TaskCompletionSource<string> Gate);

    private readonly ConcurrentDictionary<string, HitlEntry> _pending = new();

    // Keyed by "wait key": the channel id when the question came from a channel session (so an
    // ordinary chat reply answers it), otherwise the question id.
    private readonly ConcurrentDictionary<string, FreeFormEntry> _freeForm = new();

    private readonly HitlConfig _config;
    private readonly ILogger<HitlManager>? _logger;

    public HitlManager(ILogger<HitlManager>? logger = null, HitlConfig? config = null)
    {
        _logger = logger;
        _config = config ?? new HitlConfig();
    }

    // ── Mode 1: Structured approve / reject ──────────────────────────────────

    /// <summary>
    /// Blocks the caller until the human sends /approve or /reject for the given request, the
    /// caller's token is cancelled, or <see cref="HitlConfig.ApprovalTimeoutSeconds"/> elapses —
    /// in which case the request is auto-rejected rather than left blocking its lane.
    /// The caller should send the approval notification to the user before awaiting this.
    /// </summary>
    public async Task<HitlDecision> RequestApprovalAsync(
        HitlRequest request,
        CancellationToken ct = default)
    {
        var gate = new TaskCompletionSource<HitlDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[request.ApprovalId] = new HitlEntry(request, gate, DateTime.UtcNow);
        _logger?.LogInformation(
            "HITL approval pending [{ApprovalId}]: {Description}",
            request.ApprovalId, request.Description);

        var timeout = ToTimeout(_config.ApprovalTimeoutSeconds);
        try
        {
            using var reg = ct.Register(() => gate.TrySetCanceled(ct));

            if (timeout == Timeout.InfiniteTimeSpan)
                return await gate.Task;

            var completed = await Task.WhenAny(gate.Task, Task.Delay(timeout, ct));
            if (completed == gate.Task)
                return await gate.Task;

            _logger?.LogWarning(
                "HITL approval [{ApprovalId}] for '{Description}' expired after {Seconds}s with no " +
                "response — auto-rejecting so the lane is not held.",
                request.ApprovalId, request.Description, timeout.TotalSeconds);

            // Auto-reject, never auto-approve: the whole point of the gate is that nobody vouched
            // for this call, and silence is not consent.
            var expired = new HitlDecision(
                false,
                $"No human responded within {timeout.TotalSeconds:F0}s — the request expired and was " +
                "not approved. Do not retry it blindly; report that approval timed out.");
            gate.TrySetResult(expired);
            return expired;
        }
        finally
        {
            _pending.TryRemove(request.ApprovalId, out _);
        }
    }

    /// <summary>
    /// Resolves a pending Mode 1 gate.
    /// Returns false if the approvalId is not recognised (already resolved or never created).
    /// </summary>
    public bool Respond(string approvalId, bool approved, string? feedback = null)
    {
        if (!_pending.TryGetValue(approvalId, out var entry))
            return false;

        entry.Gate.TrySetResult(new HitlDecision(approved, feedback));
        _logger?.LogInformation(
            "HITL [{ApprovalId}] → {Decision}",
            approvalId, approved ? "approved" : "rejected");
        return true;
    }

    // ── Mode 2: Free-form input ───────────────────────────────────────────────

    /// <summary>
    /// Blocks the caller until the next free-form message arrives on the given channel.
    /// Kept for the channel path, where an ordinary reply on the channel is the answer.
    /// </summary>
    public async Task<string> RequestFreeFormAsync(
        string channelId,
        CancellationToken ct = default)
    {
        var question = new HitlQuestion(
            Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            SessionKey: string.Empty, channelId, Question: string.Empty, DateTime.UtcNow);

        var answer = await AskAsync(question, waitKey: channelId, ct);
        return answer ?? string.Empty;
    }

    /// <summary>
    /// Registers a question and blocks until someone answers it, the caller is cancelled, or
    /// <see cref="HitlConfig.QuestionTimeoutSeconds"/> elapses. Returns <c>null</c> on timeout so
    /// the caller can tell "the human said nothing" from "the human said something empty".
    /// </summary>
    /// <param name="waitKey">
    /// Channel id when a reply on that channel should answer it; otherwise the question id, which
    /// makes it answerable by id from any surface.
    /// </param>
    public async Task<string?> AskAsync(
        HitlQuestion question,
        string? waitKey = null,
        CancellationToken ct = default)
    {
        var key  = waitKey ?? question.QuestionId;
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        _freeForm[key] = new FreeFormEntry(question, gate);
        _logger?.LogInformation(
            "HITL question [{QuestionId}] pending on '{Key}'", question.QuestionId, key);

        var timeout = ToTimeout(_config.QuestionTimeoutSeconds);
        try
        {
            using var reg = ct.Register(() => gate.TrySetCanceled(ct));

            if (timeout == Timeout.InfiniteTimeSpan)
                return await gate.Task;

            var completed = await Task.WhenAny(gate.Task, Task.Delay(timeout, ct));
            if (completed == gate.Task)
                return await gate.Task;

            _logger?.LogWarning(
                "HITL question [{QuestionId}] expired after {Seconds}s with no reply.",
                question.QuestionId, timeout.TotalSeconds);
            return null;
        }
        finally
        {
            _freeForm.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Routes an incoming channel message to a waiting free-form gate.
    /// Returns false if no gate is registered for the channel.
    /// </summary>
    public bool RespondFreeForm(string channelId, string message)
    {
        if (!_freeForm.TryGetValue(channelId, out var entry))
            return false;

        entry.Gate.TrySetResult(message);
        _logger?.LogInformation(
            "HITL free-form response received on channel {ChannelId}", channelId);
        return true;
    }

    /// <summary>
    /// Answers a pending question by its id, from any surface.
    /// Returns false when the id is unknown (already answered, expired, or never issued).
    /// </summary>
    public bool RespondToQuestion(string questionId, string answer)
    {
        var match = _freeForm.FirstOrDefault(kv =>
            string.Equals(kv.Value.Question.QuestionId, questionId, StringComparison.OrdinalIgnoreCase));

        if (match.Value is null)
            return false;

        match.Value.Gate.TrySetResult(answer);
        _logger?.LogInformation("HITL question [{QuestionId}] answered.", questionId);
        return true;
    }

    /// <summary>True when a Mode 2 free-form gate is open for the given channel.</summary>
    public bool HasPendingFreeForm(string channelId) =>
        _freeForm.ContainsKey(channelId);

    /// <summary>
    /// Questions currently awaiting a human, for status display. Channel-answered questions carry
    /// an empty <see cref="HitlQuestion.Question"/> and are excluded — they are answered by replying
    /// on the channel, not by id.
    /// </summary>
    public IReadOnlyList<HitlQuestion> GetPendingQuestions() =>
        _freeForm.Values
            .Select(e => e.Question)
            .Where(q => q.Question.Length > 0)
            .OrderBy(q => q.CreatedAt)
            .ToList();

    /// <summary>All currently pending Mode 1 approval requests (for status display).</summary>
    public IReadOnlyList<(HitlRequest Request, DateTime CreatedAt)> GetPending() =>
        _pending.Values.Select(e => (e.Request, e.CreatedAt)).ToList();

    public bool HasAnyPending() => !_pending.IsEmpty;

    /// <summary>
    /// Returns the pending Mode 1 approval request for the given session, if any.
    /// Lets a client (e.g. the web chat UI) poll "is my turn currently blocked on approval?"
    /// without holding the originating request open for the whole wait.
    /// </summary>
    public HitlRequest? GetPendingForSession(string sessionKey) =>
        _pending.Values.FirstOrDefault(e => e.Request.SessionKey == sessionKey)?.Request;

    /// <summary>Non-positive settings mean "wait forever" — opt-in, and no longer the default.</summary>
    private static TimeSpan ToTimeout(int seconds) =>
        seconds > 0 ? TimeSpan.FromSeconds(seconds) : Timeout.InfiniteTimeSpan;
}
