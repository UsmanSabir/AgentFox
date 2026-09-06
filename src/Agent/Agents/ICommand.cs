namespace AgentFox.Agents;

/// <summary>
/// Interface for commands that can be enqueued and processed in the command queue
/// </summary>
public interface ICommand
{
    /// <summary>
    /// Unique identifier for this command execution
    /// </summary>
    string RunId { get; }
    
    /// <summary>
    /// Session key identifying the agent session making this request
    /// </summary>
    string SessionKey { get; }
    
    /// <summary>
    /// The lane this command should execute in
    /// </summary>
    CommandLane Lane { get; }
    
    /// <summary>
    /// Creation time of this command
    /// </summary>
    DateTime CreatedAt { get; }
    
    /// <summary>
    /// Priority level (0 = lowest, higher = higher priority)
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// The correlation id of whatever caused this command, carried ACROSS the queue.
    ///
    /// <para>
    /// This is the one place the ambient <c>CorrelationContext</c> cannot reach on its own. An
    /// <see cref="AsyncLocal{T}"/> flows across <c>await</c> because the continuation captures the
    /// producer's execution context; a queued command does not — it is picked up by a lane loop
    /// that started before the producer existed and never awaited it. So the id travels as data
    /// here and <c>CommandProcessor.ExecuteHandlerAsync</c> re-enters a scope from it.
    /// </para>
    ///
    /// <para>
    /// Defaulted rather than required: a command type that does not set it still compiles and
    /// still traces, it just starts a new correlation instead of continuing one. Making this
    /// mandatory would have been a breaking change to every command for no safety gain.
    /// </para>
    /// </summary>
    string? CorrelationId => null;
}
