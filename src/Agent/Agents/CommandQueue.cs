namespace AgentFox.Agents;

/// <summary>
/// Interface for a command queue that supports multiple execution lanes
/// </summary>
public interface ICommandQueue
{
    /// <summary>
    /// Enqueue a command for processing
    /// </summary>
    void Enqueue(ICommand command);
    
    /// <summary>
    /// Try to dequeue a command from a specific lane
    /// </summary>
    bool TryDequeue(CommandLane lane, out ICommand? command);
    
    /// <summary>
    /// Try to dequeue a command from any lane (respecting lane priority)
    /// </summary>
    bool TryDequeue(out ICommand? command);
    
    /// <summary>
    /// Get the number of commands waiting in a lane
    /// </summary>
    int GetQueueCount(CommandLane lane);
    
    /// <summary>
    /// Get total number of commands in all lanes
    /// </summary>
    int GetTotalQueueCount();
    
    /// <summary>
    /// Clear a specific lane's queue
    /// </summary>
    void ClearLane(CommandLane lane);
    
    /// <summary>
    /// Clear all queues
    /// </summary>
    void ClearAll();
}

/// <summary>
/// Lane-aware command queue implementation using ConcurrentQueue
/// Commands are segregated by lane to allow independent execution policies for each lane
/// </summary>
public class CommandQueue : ICommandQueue
{
    private readonly Dictionary<CommandLane, System.Collections.Concurrent.ConcurrentQueue<ICommand>> _lanes;
    private readonly object _lockObj = new();
    
    // Lane priority for dequeue operations (lower index = higher priority)
    private static readonly CommandLane[] LanePriority = { CommandLane.Main, CommandLane.Subagent, CommandLane.Tool, CommandLane.Background };
    
    public CommandQueue()
    {
        _lanes = new Dictionary<CommandLane, System.Collections.Concurrent.ConcurrentQueue<ICommand>>();
        foreach (var lane in Enum.GetValues(typeof(CommandLane)).Cast<CommandLane>())
        {
            _lanes[lane] = new System.Collections.Concurrent.ConcurrentQueue<ICommand>();
        }
    }
    
    public void Enqueue(ICommand command)
    {
        if (_lanes.TryGetValue(command.Lane, out var queue))
        {
            queue.Enqueue(command);
        }
    }
    
    public bool TryDequeue(CommandLane lane, out ICommand? command)
    {
        command = null;
        if (_lanes.TryGetValue(lane, out var queue))
        {
            return queue.TryDequeue(out command);
        }
        return false;
    }
    
    public bool TryDequeue(out ICommand? command)
    {
        command = null;
        
        // Try each lane in priority order
        foreach (var lane in LanePriority)
        {
            if (TryDequeue(lane, out var cmd))
            {
                command = cmd;
                return true;
            }
        }
        
        return false;
    }
    
    public int GetQueueCount(CommandLane lane)
    {
        if (_lanes.TryGetValue(lane, out var queue))
        {
            return queue.Count;
        }
        return 0;
    }
    
    public int GetTotalQueueCount()
    {
        return _lanes.Values.Sum(q => q.Count);
    }
    
    public void ClearLane(CommandLane lane)
    {
        if (_lanes.TryGetValue(lane, out var queue))
        {
            lock (_lockObj)
            {
                // Create a new queue to clear it
                _lanes[lane] = new System.Collections.Concurrent.ConcurrentQueue<ICommand>();
            }
        }
    }
    
    public void ClearAll()
    {
        lock (_lockObj)
        {
            foreach (var lane in _lanes.Keys)
            {
                _lanes[lane] = new System.Collections.Concurrent.ConcurrentQueue<ICommand>();
            }
        }
    }
}
