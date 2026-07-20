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
///   Agent calls request_human_input; execution blocks until the next
///   message arrives on the originating channel (or console ReadLine).
/// </summary>
public class HitlManager
{
    private sealed record HitlEntry(
        HitlRequest Request,
        TaskCompletionSource<HitlDecision> Gate,
        DateTime CreatedAt);

    private readonly ConcurrentDictionary<string, HitlEntry> _pending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _freeForm = new();
    private readonly ILogger<HitlManager>? _logger;

    public HitlManager(ILogger<HitlManager>? logger = null)
    {
        _logger = logger;
    }

    // ── Mode 1: Structured approve / reject ──────────────────────────────────

    /// <summary>
    /// Blocks the caller until the human sends /approve or /reject for the given request.
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

        try
        {
            using var reg = ct.Register(() => gate.TrySetCanceled(ct));
            return await gate.Task;
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
    /// RequestHumanInputTool registers a gate here after sending its question.
    /// </summary>
    public async Task<string> RequestFreeFormAsync(
        string channelId,
        CancellationToken ct = default)
    {
        var gate = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _freeForm[channelId] = gate;
        _logger?.LogInformation("HITL free-form waiting on channel {ChannelId}", channelId);

        try
        {
            using var reg = ct.Register(() => gate.TrySetCanceled(ct));
            return await gate.Task;
        }
        finally
        {
            _freeForm.TryRemove(channelId, out _);
        }
    }

    /// <summary>
    /// Routes an incoming channel message to a waiting free-form gate.
    /// Returns false if no gate is registered for the channel.
    /// </summary>
    public bool RespondFreeForm(string channelId, string message)
    {
        if (!_freeForm.TryGetValue(channelId, out var gate))
            return false;

        gate.TrySetResult(message);
        _logger?.LogInformation(
            "HITL free-form response received on channel {ChannelId}", channelId);
        return true;
    }

    /// <summary>True when a Mode 2 free-form gate is open for the given channel.</summary>
    public bool HasPendingFreeForm(string channelId) =>
        _freeForm.ContainsKey(channelId);

    /// <summary>All currently pending Mode 1 approval requests (for status display).</summary>
    public IReadOnlyList<(HitlRequest Request, DateTime CreatedAt)> GetPending() =>
        _pending.Values.Select(e => (e.Request, e.CreatedAt)).ToList();

    public bool HasAnyPending() => !_pending.IsEmpty;
}
