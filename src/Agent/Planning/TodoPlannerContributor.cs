using AgentFox.Agents;

namespace AgentFox.Planning;

/// <summary>
/// Steers how the model uses the Agent Framework <c>TodoProvider</c> tools, phased by the
/// session's <see cref="PlanPhase"/>.
///
/// The two systems are complementary and must not be confused:
///  - <see cref="PlanState"/> governs *permission* — mutating tools stay locked until a human
///    approves the plan. It is enforced in the tool-approval gate and cannot be talked around.
///  - The todo list tracks *progress* through an approved plan. It is advisory context, held in
///    the session state bag rather than the message list, which is why it survives compaction.
///
/// Because the todo list is advisory, this contributor is also the first line of defence for
/// completion: the Agent Framework's <c>TodoCompletionLoopEvaluator</c> is left off (it is still
/// [Experimental]/MAAI001 and takes over the agent loop), so nothing structurally stops the model
/// from ending a turn with items outstanding. The prompt asks it not to; the bounded check in
/// <c>FoxAgent.EnsureTodosCompletedAsync</c> catches it when it does anyway.
/// </summary>
public sealed class TodoPlannerContributor : IPromptContributor
{
    private readonly PlanStateStore? _store;
    private readonly TodoRestoreTracker? _restores;
    private readonly TimeSpan _staleAfter;

    /// <param name="store">
    /// Plan state, when the Plan feature is enabled. Null means the plan gate is off, so the
    /// contributor emits phase-independent guidance instead.
    /// </param>
    /// <param name="restores">
    /// Tracks todo lists rehydrated from disk after a restart, so the model can hand the
    /// resume-or-discard decision to the human instead of silently continuing old work.
    /// </param>
    /// <param name="staleAfter">
    /// How old a restored list must be before the model is told to confirm. Anything fresher
    /// resumes silently — a crash thirty seconds ago does not need a conversation about it.
    /// </param>
    public TodoPlannerContributor(
        PlanStateStore? store = null,
        TodoRestoreTracker? restores = null,
        TimeSpan? staleAfter = null)
    {
        _store = store;
        _restores = restores;
        _staleAfter = staleAfter ?? TimeSpan.FromHours(1);
    }

    public string ContributorId => "todo-planner";

    public string? GetFragment()
    {
        var restoreNotice = BuildRestoreNotice();
        var guidance = BuildGuidance();

        if (restoreNotice == null) return guidance;
        return guidance == null ? restoreNotice : guidance + "\n" + restoreNotice;
    }

    /// <summary>
    /// One-shot notice emitted on the first turn after a restart that recovered unfinished todos.
    /// Fresh restores stay silent; stale ones require the human to choose.
    /// </summary>
    private string? BuildRestoreNotice()
    {
        var restored = _restores?.Consume(FoxAgent.CurrentSessionKey.Value);
        if (restored == null || restored.OutstandingCount == 0)
            return null;

        var count = restored.OutstandingCount;
        var items = $"{count} unfinished todo item{(count == 1 ? "" : "s")}";

        if (restored.Age < _staleAfter)
            return $"""

                ## Recovered work
                This session was interrupted and restarted. {items} were recovered from
                {restored.DescribeAge()} ago. Pick up where you left off — call
                `todos_get_remaining` first to see exactly what is outstanding.
                """;

        return $"""

            ## Recovered work — confirm before resuming
            This session was interrupted and restarted. {items} were recovered, but they were
            last saved {restored.DescribeAge()} ago and may no longer be relevant.
            Do NOT resume this work automatically. Call `todos_get_remaining`, show the user
            what is outstanding, and ask whether to continue with it or discard it. Only act
            once they have answered; if they discard it, clear the items with `todos_remove`.
            """;
    }

    private string? BuildGuidance()
    {
        const string executeGuidance =
            """

            ## Todo list
            You have a persistent todo list (`todos_add`, `todos_complete`, `todos_remove`,
            `todos_get_remaining`, `todos_get_all`). It is the record of your progress and it
            survives context compaction — the conversation above may be summarized away, but the
            list will not be. Treat it, not your memory of this conversation, as the source of
            truth for what is left to do.

            - For any task with more than about three steps, call `todos_add` before you start.
            - Mark each item complete with `todos_complete` as soon as it is actually done —
              not in a batch at the end, or the record is lost if the turn is interrupted.
            - Do not end your turn while items remain incomplete. If you genuinely cannot finish
              one, `todos_remove` it and say plainly why, rather than leaving it dangling.
            - If the work changes shape, add or remove items so the list keeps matching reality.
            """;

        if (_store == null)
            return executeGuidance;

        var sessionKey = FoxAgent.CurrentSessionKey.Value;
        var state = sessionKey != null ? _store.Peek(sessionKey) : null;

        return (state?.Phase ?? PlanPhase.Research) switch
        {
            // In research the plan itself is the deliverable, and mutating tools are locked.
            // Building a todo list here would just duplicate the plan awaiting approval.
            PlanPhase.Research =>
                """

                ## Todo list
                Do not build a todo list yet — `submit_plan` carries the plan while you are in the
                research phase. Once a plan is approved you will track its execution with the
                `todos_*` tools.
                """,

            PlanPhase.AwaitingApproval => null,

            PlanPhase.Execute =>
                executeGuidance
                + "\n- Seed the list from the approved plan's steps if you have not already.",

            _ => null
        };
    }
}
