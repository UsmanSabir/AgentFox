namespace AgentFox.Tools;

/// <summary>
/// Metrics for a single tool execution
/// </summary>
public class ToolExecutionMetrics
{
    public string ToolName { get; set; } = string.Empty;
    public int ExecutionCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public double AverageExecutionTimeMs { get; set; }
    public long MinExecutionTimeMs { get; set; }
    public long MaxExecutionTimeMs { get; set; }
    public DateTime FirstExecutedAt { get; set; }
    public DateTime LastExecutedAt { get; set; }
    public string? ToolVersion { get; set; }
    
    /// <summary>
    /// Success rate as percentage (0-100)
    /// </summary>
    public double SuccessRate => ExecutionCount == 0 ? 0 : (SuccessCount * 100.0 / ExecutionCount);
}

/// <summary>
/// Tracks execution metrics for all tools
/// </summary>
public class ToolMetricsCollector
{
    private readonly Dictionary<string, ToolExecutionMetrics> _metrics = new();
    private readonly Dictionary<string, List<long>> _executionTimes = new();
    private readonly object _lock = new();
    
    /// <summary>
    /// Record a successful tool execution
    /// </summary>
    public void RecordSuccess(string toolName, long executionTimeMs, string? version = null)
    {
        lock (_lock)
        {
            if (!_metrics.ContainsKey(toolName))
            {
                _metrics[toolName] = new ToolExecutionMetrics
                {
                    ToolName = toolName,
                    FirstExecutedAt = DateTime.UtcNow
                };
                _executionTimes[toolName] = new List<long>();
            }
            
            var metric = _metrics[toolName];
            metric.ExecutionCount++;
            metric.SuccessCount++;
            metric.LastExecutedAt = DateTime.UtcNow;
            metric.ToolVersion = version ?? metric.ToolVersion;
            
            _executionTimes[toolName].Add(executionTimeMs);
            UpdateAverages(toolName);
        }
    }
    
    /// <summary>
    /// Record a failed tool execution
    /// </summary>
    public void RecordFailure(string toolName, long executionTimeMs, string? version = null)
    {
        lock (_lock)
        {
            if (!_metrics.ContainsKey(toolName))
            {
                _metrics[toolName] = new ToolExecutionMetrics
                {
                    ToolName = toolName,
                    FirstExecutedAt = DateTime.UtcNow
                };
                _executionTimes[toolName] = new List<long>();
            }
            
            var metric = _metrics[toolName];
            metric.ExecutionCount++;
            metric.FailureCount++;
            metric.LastExecutedAt = DateTime.UtcNow;
            metric.ToolVersion = version ?? metric.ToolVersion;
            
            _executionTimes[toolName].Add(executionTimeMs);
            UpdateAverages(toolName);
        }
    }
    
    /// <summary>
    /// Get metrics for a specific tool
    /// </summary>
    public ToolExecutionMetrics? GetMetrics(string toolName)
    {
        lock (_lock)
        {
            return _metrics.TryGetValue(toolName, out var metric) ? metric : null;
        }
    }
    
    /// <summary>
    /// Get all metrics
    /// </summary>
    public List<ToolExecutionMetrics> GetAllMetrics()
    {
        lock (_lock)
        {
            return _metrics.Values.ToList();
        }
    }
    
    /// <summary>
    /// Get metrics ordered by execution count (most used first)
    /// </summary>
    public List<ToolExecutionMetrics> GetMetricsByUsage()
    {
        lock (_lock)
        {
            return _metrics.Values.OrderByDescending(m => m.ExecutionCount).ToList();
        }
    }
    
    /// <summary>
    /// Get metrics ordered by failure rate (highest failure first)
    /// </summary>
    public List<ToolExecutionMetrics> GetMetricsByFailureRate()
    {
        lock (_lock)
        {
            return _metrics.Values.OrderByDescending(m => 100 - m.SuccessRate).ToList();
        }
    }
    
    /// <summary>
    /// Get metrics ordered by average execution time (slowest first)
    /// </summary>
    public List<ToolExecutionMetrics> GetMetricsBySlowest()
    {
        lock (_lock)
        {
            return _metrics.Values.OrderByDescending(m => m.AverageExecutionTimeMs).ToList();
        }
    }
    
    /// <summary>
    /// Reset metrics for a tool
    /// </summary>
    public void ResetMetrics(string toolName)
    {
        lock (_lock)
        {
            _metrics.Remove(toolName);
            _executionTimes.Remove(toolName);
        }
    }
    
    /// <summary>
    /// Clear all metrics
    /// </summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _metrics.Clear();
            _executionTimes.Clear();
        }
    }
    
    /// <summary>
    /// Get summary statistics
    /// </summary>
    public (int TotalTools, int TotalExecutions, double AverageSuccessRate) GetSummaryStatistics()
    {
        lock (_lock)
        {
            var totalTools = _metrics.Count;
            var totalExecutions = _metrics.Values.Sum(m => m.ExecutionCount);
            var averageSuccessRate = totalTools > 0 
                ? _metrics.Values.Average(m => m.SuccessRate)
                : 0;
            
            return (totalTools, totalExecutions, averageSuccessRate);
        }
    }
    
    private void UpdateAverages(string toolName)
    {
        if (!_executionTimes.TryGetValue(toolName, out var times) || times.Count == 0)
            return;
        
        var metric = _metrics[toolName];
        metric.AverageExecutionTimeMs = times.Average();
        metric.MinExecutionTimeMs = times.Min();
        metric.MaxExecutionTimeMs = times.Max();
        
        // Keep only last 100 execution times to avoid memory growth
        if (times.Count > 100)
            _executionTimes[toolName] = new List<long>(times.TakeLast(100));
    }
}
