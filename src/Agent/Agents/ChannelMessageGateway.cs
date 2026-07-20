using System.Collections.Concurrent;
using AgentFox.Models;
using AgentFox.Plugins.Channels;
using Microsoft.Extensions.Logging;

namespace AgentFox.Agents;

/// <summary>
/// Tracks a channel message processing task
/// </summary>
public class ChannelMessageTask
{
    public string MessageId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string CommandRunId { get; set; } = string.Empty;
    public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public string? Response { get; set; }
    public string? Error { get; set; }
    public ChannelMessageProcessingState State { get; set; } = ChannelMessageProcessingState.Pending;
}

/// <summary>
/// Processing states for channel messages
/// </summary>
public enum ChannelMessageProcessingState
{
    Pending,
    Processing,
    Completed,
    Failed,
    TimedOut
}

/// <summary>
/// Statistics for channel message gateway
/// </summary>
public class ChannelGatewayStatistics
{
    public long TotalMessagesReceived { get; set; }
    public long TotalMessagesProcessed { get; set; }
    public long TotalMessagesFailed { get; set; }
    public Dictionary<string, long> MessagesPerChannel { get; set; } = new();
    public TimeSpan Uptime { get; set; }
    public double AverageProcessingTimeMilliseconds { get; set; }
}

/// <summary>
/// Central gateway for bridging channel messages to the agent command lane system
/// Inspired by OpenClaw's gateway pattern for multi-channel message routing
/// </summary>
public class ChannelMessageGateway : IDisposable
{
    private readonly ICommandQueue _commandQueue;
    private readonly IAgentRuntime _agentRuntime;
    private readonly ILogger? _logger;
    
    // Track processing tasks
    private readonly ConcurrentDictionary<string, ChannelMessageTask> _processingTasks;
    
    // Statistics
    private long _totalReceived;
    private long _totalProcessed;
    private long _totalFailed;
    private readonly ConcurrentDictionary<string, long> _messagesPerChannel;
    private readonly DateTime _startTime;
    private readonly List<long> _processingTimes;
    private readonly object _timesLock = new();
    
    // Configuration
    private readonly CommandLane _defaultLane;
    private readonly int? _defaultTimeoutSeconds;
    private readonly int _maxConcurrentProcessing;
    private int _currentProcessingCount;
    private bool _disposed;
    
    public ChannelMessageGateway(
        ICommandQueue commandQueue,
        IAgentRuntime agentRuntime,
        ILogger? logger = null,
        CommandLane defaultLane = CommandLane.Main,
        int? defaultTimeoutSeconds = null,
        int maxConcurrentProcessing = 10)
    {
        _commandQueue = commandQueue ?? throw new ArgumentNullException(nameof(commandQueue));
        _agentRuntime = agentRuntime ?? throw new ArgumentNullException(nameof(agentRuntime));
        _logger = logger;
        _defaultLane = defaultLane;
        _defaultTimeoutSeconds = defaultTimeoutSeconds ?? 300;
        _maxConcurrentProcessing = maxConcurrentProcessing;
        _startTime = DateTime.UtcNow;
        
        _processingTasks = new ConcurrentDictionary<string, ChannelMessageTask>();
        _messagesPerChannel = new ConcurrentDictionary<string, long>();
        _processingTimes = new List<long>();
        
        _logger?.LogInformation("ChannelMessageGateway initialized with default lane: {Lane}", _defaultLane);
    }
    
    /// <summary>
    /// Process an incoming channel message
    /// Wraps it as a command and enqueues for lane-based processing
    /// </summary>
    public async Task<ChannelMessageTask> ProcessChannelMessageAsync(
        ChannelMessage channelMessage,
        Channel originatingChannel,
        string agentId,
        CommandLane? overrideLane = null,
        int? overrideTimeoutSeconds = null)
    {
        ThrowIfDisposed();
        
        try
        {
            Interlocked.Increment(ref _totalReceived);
            _messagesPerChannel.AddOrUpdate(originatingChannel.ChannelId, 1, (_, count) => count + 1);
            
            // Create tracking task
            var messageTask = new ChannelMessageTask
            {
                MessageId = channelMessage.Id,
                ChannelId = originatingChannel.ChannelId,
                EnqueuedAt = DateTime.UtcNow
            };
            
            // Check concurrency limit
            if (Interlocked.Increment(ref _currentProcessingCount) > _maxConcurrentProcessing)
            {
                Interlocked.Decrement(ref _currentProcessingCount);
                messageTask.State = ChannelMessageProcessingState.Failed;
                messageTask.Error = $"Gateway is at maximum concurrent processing limit ({_maxConcurrentProcessing})";
                Interlocked.Increment(ref _totalFailed);
                
                _logger?.LogWarning("Channel message rejected due to concurrency limit: {MessageId}", channelMessage.Id);
                return messageTask;
            }
            
            try
            {
                // Create channel command
                var command = ChannelCommand.CreateFromChannelMessage(
                    channelMessage,
                    originatingChannel,
                    agentId,
                    overrideLane ?? _defaultLane,
                    priority: 0,
                    timeoutSeconds: overrideTimeoutSeconds ?? _defaultTimeoutSeconds
                );
                
                messageTask.CommandRunId = command.RunId;
                messageTask.State = ChannelMessageProcessingState.Processing;
                
                // Track the task
                _processingTasks.TryAdd(command.RunId, messageTask);
                
                // Enqueue the command for processing
                _commandQueue.Enqueue(command);
                
                _logger?.LogInformation(
                    "Channel message enqueued: MessageId={MessageId}, Channel={Channel}, Lane={Lane}, Command={RunId}",
                    channelMessage.Id, originatingChannel.Name, command.Lane, command.RunId);
                
                // Start background monitoring of this task
                _ = MonitorTaskCompletionAsync(command.RunId, originatingChannel, channelMessage, messageTask);
                
                return messageTask;
            }
            finally
            {
                Interlocked.Decrement(ref _currentProcessingCount);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _totalFailed);
            _logger?.LogError(ex, "Error processing channel message: {MessageId}", channelMessage.Id);
            
            return new ChannelMessageTask
            {
                MessageId = channelMessage.Id,
                ChannelId = originatingChannel.ChannelId,
                State = ChannelMessageProcessingState.Failed,
                Error = ex.Message
            };
        }
    }
    
    /// <summary>
    /// Monitor a task for completion and handle the response
    /// </summary>
    private async Task MonitorTaskCompletionAsync(
        string commandRunId,
        Channel channel,
        ChannelMessage originalMessage,
        ChannelMessageTask messageTask)
    {
        var startTime = DateTime.UtcNow;
        var timeout = messageTask.State == ChannelMessageProcessingState.Processing ? 
            TimeSpan.FromSeconds(_defaultTimeoutSeconds ?? 300) : 
            TimeSpan.FromSeconds(5);
        
        // Simulate waiting for task completion (in real implementation, would use TaskCompletionSource)
        // For demonstration, we'll check if the task completes within timeout
        while (messageTask.State == ChannelMessageProcessingState.Processing)
        {
            if (DateTime.UtcNow - startTime > timeout)
            {
                messageTask.State = ChannelMessageProcessingState.TimedOut;
                messageTask.Error = $"Command execution timed out after {timeout.TotalSeconds}s";
                Interlocked.Increment(ref _totalFailed);
                
                _logger?.LogWarning("Channel message processing timed out: {MessageId}", originalMessage.Id);
                
                try
                {
                    await channel.SendMessageAsync($"⏱️ Request timed out after {timeout.TotalSeconds}s. Please try again.");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error sending timeout response to channel");
                }
                
                break;
            }
            
            await Task.Delay(100); // Check every 100ms
        }
        
        // Record processing time
        var processingTime = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
        lock (_timesLock)
        {
            _processingTimes.Add(processingTime);
            if (_processingTimes.Count > 1000) // Keep last 1000
            {
                _processingTimes.RemoveAt(0);
            }
        }
    }
    
    /// <summary>
    /// Mark a task as completed with a response
    /// Called by command handlers after agent execution
    /// </summary>
    public async Task CompleteChannelMessageAsync(
        string commandRunId,
        string response,
        Channel channel)
    {
        try
        {
            if (_processingTasks.TryRemove(commandRunId, out var messageTask))
            {
                messageTask.Response = response;
                messageTask.State = ChannelMessageProcessingState.Completed;
                messageTask.ProcessedAt = DateTime.UtcNow;
                
                Interlocked.Increment(ref _totalProcessed);
                
                _logger?.LogInformation(
                    "Channel message completed: MessageId={MessageId}, Channel={ChannelId}",
                    messageTask.MessageId, messageTask.ChannelId);
                
                // Send response back to channel
                try
                {
                    await channel.SendMessageAsync(response);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error sending response to channel: {ChannelId}", channel.ChannelId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error completing channel message: {CommandRunId}", commandRunId);
        }
    }
    
    /// <summary>
    /// Mark a task as failed with an error
    /// Called by command handlers on error
    /// </summary>
    public async Task FailChannelMessageAsync(
        string commandRunId,
        string error,
        Channel channel)
    {
        try
        {
            if (_processingTasks.TryRemove(commandRunId, out var messageTask))
            {
                messageTask.Error = error;
                messageTask.State = ChannelMessageProcessingState.Failed;
                messageTask.ProcessedAt = DateTime.UtcNow;
                
                Interlocked.Increment(ref _totalFailed);
                
                _logger?.LogError(
                    "Channel message failed: MessageId={MessageId}, Error={Error}",
                    messageTask.MessageId, error);
                
                // Send error response to channel
                try
                {
                    var errorMessage = $"❌ Error processing your request: {error}";
                    await channel.SendMessageAsync(errorMessage);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error sending error response to channel: {ChannelId}", channel.ChannelId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error failing channel message: {CommandRunId}", commandRunId);
        }
    }
    
    /// <summary>
    /// Get current statistics
    /// </summary>
    public ChannelGatewayStatistics GetStatistics()
    {
        double avgTime = 0;
        lock (_timesLock)
        {
            if (_processingTimes.Count > 0)
            {
                avgTime = _processingTimes.Average();
            }
        }
        
        return new ChannelGatewayStatistics
        {
            TotalMessagesReceived = _totalReceived,
            TotalMessagesProcessed = _totalProcessed,
            TotalMessagesFailed = _totalFailed,
            MessagesPerChannel = new Dictionary<string, long>(_messagesPerChannel),
            Uptime = DateTime.UtcNow - _startTime,
            AverageProcessingTimeMilliseconds = avgTime
        };
    }
    
    /// <summary>
    /// Get currently processing tasks
    /// </summary>
    public IReadOnlyDictionary<string, ChannelMessageTask> GetProcessingTasks()
    {
        return _processingTasks.AsReadOnly();
    }
    
    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }
    
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException("ChannelMessageGateway");
    }
}
