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
}
