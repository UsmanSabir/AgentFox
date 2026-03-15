namespace AgentFox.Memory;

/// <summary>
/// Interface for agent memory systems
/// </summary>
public interface IMemory
{
    /// <summary>
    /// Add a memory entry
    /// </summary>
    Task AddAsync(MemoryEntry entry);
    
    /// <summary>
    /// Search memories by query
    /// </summary>
    Task<List<MemoryEntry>> SearchAsync(string query, int limit = 10);
    
    /// <summary>
    /// Get all memories
    /// </summary>
    Task<List<MemoryEntry>> GetAllAsync();
    
    /// <summary>
    /// Clear all memories
    /// </summary>
    Task ClearAsync();
    
    /// <summary>
    /// Get recent memories
    /// </summary>
    Task<List<MemoryEntry>> GetRecentAsync(int count = 10);
}

/// <summary>
/// Represents a memory entry
/// </summary>
public class MemoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Content { get; set; } = string.Empty;
    public MemoryType Type { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Metadata { get; set; } = new();
    public double Importance { get; set; } = 0.5;
}

/// <summary>
/// Type of memory
/// </summary>
public enum MemoryType
{
    Conversation,
    ToolExecution,
    SubAgentResult,
    Observation,
    Fact,
    UserPreference
}

/// <summary>
/// Short-term memory (conversation context)
/// </summary>
public class ShortTermMemory : IMemory
{
    private readonly List<MemoryEntry> _memories = new();
    private readonly int _maxSize;
    private readonly object _lock = new();

    public ShortTermMemory(int maxSize = 100)
    {
        _maxSize = maxSize;
    }

    public Task AddAsync(MemoryEntry entry)
    {
        lock (_lock)
        {
            _memories.Add(entry);
            if (_memories.Count > _maxSize)
            {
                // Remove oldest memories
                _memories.RemoveRange(0, _memories.Count - _maxSize);
            }
        }
        return Task.CompletedTask;
    }

    public Task<List<MemoryEntry>> SearchAsync(string query, int limit = 10)
    {
        lock (_lock)
        {
            var results = _memories
                .Where(m => m.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Timestamp)
                .Take(limit)
                .ToList();
            return Task.FromResult(results);
        }
    }

    public Task<List<MemoryEntry>> GetAllAsync()
    {
        lock (_lock)
        {
            return Task.FromResult(_memories.ToList());
        }
    }

    public Task ClearAsync()
    {
        lock (_lock)
        {
            _memories.Clear();
        }
        return Task.CompletedTask;
    }

    public Task<List<MemoryEntry>> GetRecentAsync(int count = 10)
    {
        lock (_lock)
        {
            return Task.FromResult(_memories
                .OrderByDescending(m => m.Timestamp)
                .Take(count)
                .ToList());
        }
    }
}

/// <summary>
/// Long-term memory (persistent storage)
/// </summary>
public class LongTermMemory : IMemory
{
    private readonly List<MemoryEntry> _memories = new();
    private readonly string _storagePath;
    private readonly object _lock = new();

    public LongTermMemory(string? storagePath = null)
    {
        _storagePath = storagePath ?? "memory_store.json";
        LoadFromDisk();
    }

    public Task AddAsync(MemoryEntry entry)
    {
        lock (_lock)
        {
            _memories.Add(entry);
            SaveToDisk();
        }
        return Task.CompletedTask;
    }

    public Task<List<MemoryEntry>> SearchAsync(string query, int limit = 10)
    {
        lock (_lock)
        {
            var results = _memories
                .Where(m => m.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Importance)
                .ThenByDescending(m => m.Timestamp)
                .Take(limit)
                .ToList();
            return Task.FromResult(results);
        }
    }

    public Task<List<MemoryEntry>> GetAllAsync()
    {
        lock (_lock)
        {
            return Task.FromResult(_memories.ToList());
        }
    }

    public Task ClearAsync()
    {
        lock (_lock)
        {
            _memories.Clear();
            SaveToDisk();
        }
        return Task.CompletedTask;
    }

    public Task<List<MemoryEntry>> GetRecentAsync(int count = 10)
    {
        lock (_lock)
        {
            return Task.FromResult(_memories
                .OrderByDescending(m => m.Timestamp)
                .Take(count)
                .ToList());
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var memories = Newtonsoft.Json.JsonConvert.DeserializeObject<List<MemoryEntry>>(json);
                if (memories != null)
                {
                    _memories.AddRange(memories);
                }
            }
        }
        catch
        {
            // Start fresh if loading fails
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(_memories, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(_storagePath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }
}

/// <summary>
/// Hybrid memory system combining short-term and long-term
/// </summary>
public class HybridMemory : IMemory
{
    private readonly ShortTermMemory _shortTerm;
    private readonly LongTermMemory _longTerm;
    private readonly double _importanceThreshold;

    public HybridMemory(int shortTermSize = 50, string? longTermPath = null, double importanceThreshold = 0.7)
    {
        _shortTerm = new ShortTermMemory(shortTermSize);
        _longTerm = new LongTermMemory(longTermPath);
        _importanceThreshold = importanceThreshold;
    }

    public async Task AddAsync(MemoryEntry entry)
    {
        await _shortTerm.AddAsync(entry);
        
        // Auto-consolidate important memories to long-term
        if (entry.Importance >= _importanceThreshold)
        {
            await _longTerm.AddAsync(entry);
        }
    }

    public async Task<List<MemoryEntry>> SearchAsync(string query, int limit = 10)
    {
        var shortTerm = await _shortTerm.SearchAsync(query, limit);
        var longTerm = await _longTerm.SearchAsync(query, limit);
        
        // Merge and deduplicate
        var combined = shortTerm.Concat(longTerm)
            .GroupBy(m => m.Id)
            .Select(g => g.First())
            .OrderByDescending(m => m.Importance)
            .ThenByDescending(m => m.Timestamp)
            .Take(limit)
            .ToList();
        
        return combined;
    }

    public Task<List<MemoryEntry>> GetAllAsync()
    {
        return _shortTerm.GetAllAsync();
    }

    public async Task ClearAsync()
    {
        await _shortTerm.ClearAsync();
    }

    public Task<List<MemoryEntry>> GetRecentAsync(int count = 10)
    {
        return _shortTerm.GetRecentAsync(count);
    }
    
    /// <summary>
    /// Consolidate short-term to long-term
    /// </summary>
    public async Task ConsolidateAsync()
    {
        var recent = await _shortTerm.GetAllAsync();
        foreach (var entry in recent.Where(e => e.Importance >= _importanceThreshold))
        {
            await _longTerm.AddAsync(entry);
        }
    }
}
