using System.Collections.Concurrent;
using AgentFox.Plugins.Models;

namespace AgentFox.Agents;

/// <summary>
/// Owns the lifecycle of web chat turns independently from the browser connection.
/// Turns are serialized per conversation, while different conversations may run in parallel.
/// </summary>
public sealed class WebChatTurnCoordinator
{
    private readonly ConcurrentDictionary<string, ConversationState> _conversations = new(
        StringComparer.Ordinal);

    /// <summary>Enqueue a turn and return its stable run id and queue position.</summary>
    public WebChatTurnHandle Enqueue(
        string conversationId,
        Func<string, CancellationToken, Task<WebChatTurnResult>> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(execute);

        while (true)
        {
            var state = _conversations.GetOrAdd(conversationId, static _ => new ConversationState());
            lock (state.Gate)
            {
                if (state.Retired)
                    continue;

                var turn = new Turn(conversationId, execute);
                var position = state.Active is null ? state.Queue.Count : state.Queue.Count + 1;
                state.Queue.AddLast(turn);

                if (!state.PumpStarted)
                {
                    state.PumpStarted = true;
                    _ = PumpAsync(conversationId, state);
                }

                return new WebChatTurnHandle(turn, position);
            }
        }
    }

    /// <summary>
    /// Moves a queued turn to the front and requests cancellation of the active turn.
    /// The active turn is allowed to unwind before the selected turn starts.
    /// </summary>
    public bool Steer(string conversationId, string queuedRunId)
    {
        if (!_conversations.TryGetValue(conversationId, out var state))
            return false;

        lock (state.Gate)
        {
            var node = state.Queue.First;
            while (node is not null && !node.Value.RunId.Equals(queuedRunId, StringComparison.Ordinal))
                node = node.Next;

            if (node is null)
                return false;

            state.Queue.Remove(node);
            state.Queue.AddFirst(node);
            state.Active?.Cancellation.Cancel();
            return true;
        }
    }

    /// <summary>Requests cancellation of the currently running turn.</summary>
    public bool CancelActive(string conversationId)
    {
        if (!_conversations.TryGetValue(conversationId, out var state))
            return false;

        lock (state.Gate)
        {
            if (state.Active is null)
                return false;
            state.Active.Cancellation.Cancel();
            return true;
        }
    }

    public WebChatQueueSnapshot GetSnapshot(string conversationId)
    {
        if (!_conversations.TryGetValue(conversationId, out var state))
            return new WebChatQueueSnapshot(conversationId, null, []);

        lock (state.Gate)
        {
            return new WebChatQueueSnapshot(
                conversationId,
                state.Active?.RunId,
                state.Queue.Select((turn, index) => new WebChatQueuedTurn(turn.RunId, index + 1)).ToList());
        }
    }

    private async Task PumpAsync(string conversationId, ConversationState state)
    {
        try
        {
            while (true)
            {
                Turn? turn;
                lock (state.Gate)
                {
                    if (state.Queue.First is null)
                    {
                        state.Active = null;
                        state.PumpStarted = false;
                        state.Retired = true;
                        if (_conversations.TryGetValue(conversationId, out var current) &&
                            ReferenceEquals(current, state))
                            _conversations.TryRemove(conversationId, out _);
                        return;
                    }

                    turn = state.Queue.First.Value;
                    state.Queue.RemoveFirst();
                    state.Active = turn;
                }

                WebChatTurnResult result;
                try
                {
                    await turn.Release.Task.ConfigureAwait(false);
                    result = await turn.ExecuteAsync(turn.RunId, turn.Cancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (turn.Cancellation.IsCancellationRequested)
                {
                    result = WebChatTurnResult.Interrupted();
                }
                catch (Exception ex)
                {
                    result = WebChatTurnResult.Failed(ex.Message);
                }
                finally
                {
                    turn.Cancellation.Dispose();
                }

                turn.Completion.TrySetResult(result);

                lock (state.Gate)
                {
                    if (ReferenceEquals(state.Active, turn))
                        state.Active = null;
                }
            }
        }
        finally
        {
            lock (state.Gate)
            {
                state.Active = null;
                state.PumpStarted = false;
            }
        }
    }

    private sealed class ConversationState
    {
        public object Gate { get; } = new();
        public LinkedList<Turn> Queue { get; } = [];
        public Turn? Active { get; set; }
        public bool PumpStarted { get; set; }
        public bool Retired { get; set; }
    }

    internal sealed class Turn
    {
        public Turn(string conversationId, Func<string, CancellationToken, Task<WebChatTurnResult>> execute)
        {
            ConversationId = conversationId;
            ExecuteAsync = execute;
        }

        public string ConversationId { get; }
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public Func<string, CancellationToken, Task<WebChatTurnResult>> ExecuteAsync { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<object?> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WebChatTurnResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class WebChatTurnHandle
    {
        private readonly Turn _turn;

        internal WebChatTurnHandle(Turn turn, int position)
        {
            _turn = turn;
            Position = position;
        }

        public string RunId => _turn.RunId;
        public int Position { get; }
        public Task<WebChatTurnResult> Completion => _turn.Completion.Task;
        public void Release() => _turn.Release.TrySetResult(null);
    }
}

public sealed record WebChatTurnResult(
    WebChatTurnState State,
    AgentReply? Reply = null,
    string? Error = null)
{
    public static WebChatTurnResult Completed(AgentReply reply) =>
        new(WebChatTurnState.Completed, reply);

    public static WebChatTurnResult Interrupted() =>
        new(WebChatTurnState.Interrupted);

    public static WebChatTurnResult Failed(string error) =>
        new(WebChatTurnState.Failed, Error: error);
}

public enum WebChatTurnState
{
    Completed,
    Interrupted,
    Failed
}

public sealed record WebChatQueueSnapshot(
    string ConversationId,
    string? ActiveRunId,
    IReadOnlyList<WebChatQueuedTurn> Queued);

public sealed record WebChatQueuedTurn(string RunId, int Position);
