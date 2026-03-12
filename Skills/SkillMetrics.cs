using System.Globalization;

namespace AgentFox.Skills;

/// <summary>
/// Metrics for a single skill execution
/// </summary>
public class SkillExecutionMetrics
{
    public string SkillName { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public long ExecutionTimeMs { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int AttemptNumber { get; set; } = 1;
    public Dictionary<string, object> Tags { get; set; } = new();         // Custom metrics
    public decimal EstimatedCost { get; set; } = 0;                        // API costs, etc.
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
    public int ExecutionDepth { get; set; } = 0;                          // Sub-agent depth
    
    /// <summary>
    /// Get execution rate (calls per minute)
    /// </summary>
    public double GetExecutionRate(int minuteWindow = 1)
    {
        return ExecutionTimeMs > 0 ? (double)(60000 / ExecutionTimeMs) * minuteWindow : 0;
    }
}

/// <summary>
/// Aggregated statistics for a skill
/// </summary>
public class SkillStatistics
{
    public string SkillName { get; set; } = string.Empty;
    public int TotalExecutions { get; set; }
    public double SuccessRate { get; set; }  // 0.0 to 1.0
    public double AverageExecutionTimeMs { get; set; }
    public long MaxExecutionTimeMs { get; set; }
    public long MinExecutionTimeMs { get; set; }
    public double AverageAttemptsPerExecution { get; set; }
    public decimal TotalEstimatedCost { get; set; }
    public int FailureCount { get; set; }
    public DateTime? LastExecutedAt { get; set; }
    
    public override string ToString()
    {
        return $"SkillStatistics: {SkillName} " +
               $"[Total: {TotalExecutions}, Success: {SuccessRate:P2}, " +
               $"Avg Time: {AverageExecutionTimeMs:F0}ms, Cost: {TotalEstimatedCost:F2}]";
    }
}

/// <summary>
/// Collects and aggregates skill execution metrics
/// </summary>
public class SkillMetricsCollector
{
    private readonly List<SkillExecutionMetrics> _metrics = new();
    private readonly object _lock = new();
    private readonly int? _maxMetricsToStore;  // Null = unlimited
    
    public SkillMetricsCollector(int? maxMetricsToStore = null)
    {
        _maxMetricsToStore = maxMetricsToStore;
    }
    
    /// <summary>
    /// Record a skill execution
    /// </summary>
    public void Record(SkillExecutionMetrics metric)
    {
        lock (_lock)
        {
            _metrics.Add(metric);
            
            // Trim if we exceed max stored metrics
            if (_maxMetricsToStore.HasValue && _metrics.Count > _maxMetricsToStore.Value)
            {
                _metrics.RemoveRange(0, _metrics.Count - _maxMetricsToStore.Value);
            }
        }
    }
    
    /// <summary>
    /// Get metrics for a specific skill
    /// </summary>
    public List<SkillExecutionMetrics> GetMetricsFor(
        string skillName,
        TimeSpan? timeRange = null,
        string? agentId = null)
    {
        lock (_lock)
        {
            var query = _metrics.Where(m => m.SkillName == skillName);
            
            if (timeRange.HasValue)
            {
                var minTime = DateTime.UtcNow.Subtract(timeRange.Value);
                query = query.Where(m => m.ExecutedAt >= minTime);
            }
            
            if (!string.IsNullOrEmpty(agentId))
            {
                query = query.Where(m => m.AgentId == agentId);
            }
            
            return query.ToList();
        }
    }
    
    /// <summary>
    /// Get aggregated statistics for a skill
    /// </summary>
    public SkillStatistics GetStatistics(string skillName, TimeSpan? timeRange = null)
    {
        var metrics = GetMetricsFor(skillName, timeRange);
        
        if (metrics.Count == 0)
        {
            return new SkillStatistics { SkillName = skillName };
        }
        
        return new SkillStatistics
        {
            SkillName = skillName,
            TotalExecutions = metrics.Count,
            SuccessRate = metrics.Count > 0 
                ? metrics.Count(m => m.Success) / (double)metrics.Count 
                : 0,
            AverageExecutionTimeMs = metrics.Average(m => m.ExecutionTimeMs),
            MaxExecutionTimeMs = metrics.Max(m => m.ExecutionTimeMs),
            MinExecutionTimeMs = metrics.Min(m => m.ExecutionTimeMs),
            AverageAttemptsPerExecution = metrics.Average(m => m.AttemptNumber),
            TotalEstimatedCost = metrics.Sum(m => m.EstimatedCost),
            FailureCount = metrics.Count(m => !m.Success),
            LastExecutedAt = metrics.MaxBy(m => m.ExecutedAt)?.ExecutedAt
        };
    }
    
    /// <summary>
    /// Get statistics for all agents using a skill
    /// </summary>
    public Dictionary<string, SkillStatistics> GetStatisticsByAgent(string skillName, TimeSpan? timeRange = null)
    {
        var metrics = GetMetricsFor(skillName, timeRange);
        var grouped = metrics.GroupBy(m => m.AgentId);
        
        var result = new Dictionary<string, SkillStatistics>();
        foreach (var group in grouped)
        {
            var groupMetrics = group.ToList();
            result[group.Key] = new SkillStatistics
            {
                SkillName = skillName,
                TotalExecutions = groupMetrics.Count,
                SuccessRate = groupMetrics.Count(m => m.Success) / (double)groupMetrics.Count,
                AverageExecutionTimeMs = groupMetrics.Average(m => m.ExecutionTimeMs),
                MaxExecutionTimeMs = groupMetrics.Max(m => m.ExecutionTimeMs),
                MinExecutionTimeMs = groupMetrics.Min(m => m.ExecutionTimeMs),
                TotalEstimatedCost = groupMetrics.Sum(m => m.EstimatedCost)
            };
        }
        
        return result;
    }
    
    /// <summary>
    /// Get top skills by execution count
    /// </summary>
    public List<(string SkillName, int Count)> GetTopSkillsByExecutionCount(int top = 10)
    {
        lock (_lock)
        {
            return _metrics
                .GroupBy(m => m.SkillName)
                .Select(g => (g.Key, g.Count()))
                .OrderByDescending(x => x.Item2)
                .Take(top)
                .ToList();
        }
    }
    
    /// <summary>
    /// Get top skills by failure rate
    /// </summary>
    public List<(string SkillName, double FailureRate, int TotalExecutions)> GetTopSkillsByFailureRate(int top = 10)
    {
        lock (_lock)
        {
            return _metrics
                .GroupBy(m => m.SkillName)
                .Select(g => new
                {
                    SkillName = g.Key,
                    FailureRate = 1.0 - (g.Count(m => m.Success) / (double)g.Count()),
                    TotalExecutions = g.Count()
                })
                .OrderByDescending(x => x.FailureRate)
                .Take(top)
                .Select(x => (x.SkillName, x.FailureRate, x.TotalExecutions))
                .ToList();
        }
    }
    
    /// <summary>
    /// Get total cost across all skills
    /// </summary>
    public decimal GetTotalCost(TimeSpan? timeRange = null)
    {
        lock (_lock)
        {
            var query = _metrics.AsEnumerable();
            
            if (timeRange.HasValue)
            {
                var minTime = DateTime.UtcNow.Subtract(timeRange.Value);
                query = query.Where(m => m.ExecutedAt >= minTime);
            }
            
            return query.Sum(m => m.EstimatedCost);
        }
    }
    
    /// <summary>
    /// Get cost breakdown by skill
    /// </summary>
    public Dictionary<string, decimal> GetCostBySkill(TimeSpan? timeRange = null)
    {
        lock (_lock)
        {
            var query = _metrics.AsEnumerable();
            
            if (timeRange.HasValue)
            {
                var minTime = DateTime.UtcNow.Subtract(timeRange.Value);
                query = query.Where(m => m.ExecutedAt >= minTime);
            }
            
            return query
                .GroupBy(m => m.SkillName)
                .ToDictionary(g => g.Key, g => g.Sum(m => m.EstimatedCost));
        }
    }
    
    /// <summary>
    /// Clear all metrics (useful for testing)
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _metrics.Clear();
        }
    }
    
    /// <summary>
    /// Get all metrics for a time range
    /// </summary>
    public List<SkillExecutionMetrics> GetMetricsForTimeRange(TimeSpan timeRange)
    {
        lock (_lock)
        {
            var minTime = DateTime.UtcNow.Subtract(timeRange);
            return _metrics.Where(m => m.ExecutedAt >= minTime).ToList();
        }
    }
    
    /// <summary>
    /// Get total count of metrics stored
    /// </summary>
    public int GetTotalMetricsCount()
    {
        lock (_lock)
        {
            return _metrics.Count;
        }
    }
}
