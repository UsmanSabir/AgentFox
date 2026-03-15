using Microsoft.Extensions.Logging;

namespace AgentFox.Agents;

/// <summary>
/// Delegate for handling command execution
/// </summary>
public delegate Task CommandHandler(ICommand command, CancellationToken cancellationToken);

/// <summary>
/// Manages continuous processing of commands from the queue
/// Supports multiple lanes with configurable policies for each lane
/// </summary>
public class CommandProcessor : IDisposable
{
    private readonly ICommandQueue _commandQueue;
    private readonly ILogger? _logger;
    private readonly Dictionary<CommandLane, CommandHandler?> _handlers;
    private readonly CancellationTokenSource _processorCts;
    private Task? _processingTask;
    private bool _disposed;
    
    // Configuration
    private readonly int _processingDelayMilliseconds;
    private readonly int _maxCommandsPerBatch;
    
    // Statistics
    private long _totalProcessed;
    private long _totalFailed;
    private DateTime _startTime;
    
    public CommandProcessor(
        ICommandQueue commandQueue,
        ILogger? logger = null,
        int processingDelayMilliseconds = 10,
        int maxCommandsPerBatch = 1)
    {
        _commandQueue = commandQueue ?? throw new ArgumentNullException(nameof(commandQueue));
        _logger = logger;
        _processingDelayMilliseconds = processingDelayMilliseconds;
        _maxCommandsPerBatch = maxCommandsPerBatch;
        _processorCts = new CancellationTokenSource();
        _handlers = new Dictionary<CommandLane, CommandHandler?>();
        _startTime = DateTime.UtcNow;
        
        // Initialize handlers as null
        foreach (var lane in Enum.GetValues(typeof(CommandLane)).Cast<CommandLane>())
        {
            _handlers[lane] = null;
        }
    }
    
    /// <summary>
    /// Register a handler for a specific lane
    /// </summary>
    public void RegisterLaneHandler(CommandLane lane, CommandHandler handler)
    {
        _handlers[lane] = handler;
        _logger?.LogInformation($"Registered handler for lane: {lane}");
    }
    
    /// <summary>
    /// Start processing commands
    /// </summary>
    public void Start()
    {
        if (_processingTask != null)
        {
            _logger?.LogWarning("Command processor is already running");
            return;
        }
        
        _logger?.LogInformation("Starting command processor");
        _processingTask = ProcessCommandsAsync(_processorCts.Token);
    }
    
    /// <summary>
    /// Stop processing commands gracefully
    /// </summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        if (_processingTask == null)
        {
            _logger?.LogWarning("Command processor is not running");
            return;
        }
        
        _logger?.LogInformation("Stopping command processor");
        _processorCts.Cancel();
        
        try
        {
            await _processingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("Command processor stopped");
        }
    }
    
    /// <summary>
    /// Get statistics about command processing
    /// </summary>
    public ProcessorStatistics GetStatistics()
    {
        return new ProcessorStatistics
        {
            TotalProcessed = _totalProcessed,
            TotalFailed = _totalFailed,
            Uptime = DateTime.UtcNow - _startTime,
            QueuedCommands = _commandQueue.GetTotalQueueCount()
        };
    }
    
    private async Task ProcessCommandsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int processed = 0;
                
                // Process commands in batches
                while (processed < _maxCommandsPerBatch && _commandQueue.TryDequeue(out var command))
                {
                    if (command == null)
                        break;
                    
                    try
                    {
                        // Execute the command with its lane's handler
                        if (_handlers.TryGetValue(command.Lane, out var handler) && handler != null)
                        {
                            _logger?.LogDebug($"Processing command: RunId={command.RunId}, Lane={command.Lane}");
                            await handler(command, cancellationToken).ConfigureAwait(false);
                            Interlocked.Increment(ref _totalProcessed);
                        }
                        else
                        {
                            _logger?.LogWarning($"No handler registered for lane: {command.Lane}");
                            Interlocked.Increment(ref _totalFailed);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger?.LogDebug("Command processing cancelled");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"Error processing command: {ex.Message}");
                        Interlocked.Increment(ref _totalFailed);
                    }
                    
                    processed++;
                }
                
                // If no commands were processed, wait before checking again
                if (processed == 0)
                {
                    await Task.Delay(_processingDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("Command processing loop cancelled");
        }
    }
    
    public void Dispose()
    {
        if (_disposed)
            return;
        
        try
        {
            _processorCts.Cancel();
            _processingTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Ignore exceptions during cleanup
        }
        
        _processorCts?.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Statistics about command processor performance
/// </summary>
public class ProcessorStatistics
{
    public long TotalProcessed { get; set; }
    public long TotalFailed { get; set; }
    public TimeSpan Uptime { get; set; }
    public int QueuedCommands { get; set; }
}
