using System.Collections.Concurrent;
using System.Text.Json;
using AgentFox.Agents;
using AgentFox.Plugins.Interfaces;
using AgentFox.Tools;
using Microsoft.Extensions.Configuration;

namespace AgentFox.Memory;

/// <summary>
/// Runtime memory policy shared by agents, tools, the session manager, and the web UI.
/// Global/specialist settings are persisted under the workspace; session overrides live in
/// sessions/index.json with the rest of the session metadata.
/// </summary>
public sealed class MemoryAccessPolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ConcurrentDictionary<string, bool> _sessionOverrides =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SpecialistMemoryMode> _agentModes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _settingsPath;
    private readonly object _saveLock = new();
    private bool _globalEnabled;

    public MemoryAccessPolicy(IConfiguration configuration, WorkspaceManager workspaceManager)
    {
        _globalEnabled = configuration.GetValue("Memory:Enabled", true);
        _settingsPath = workspaceManager.ResolvePath(Path.Combine("memory", "settings.json"));
        Load();
    }

    public bool GlobalEnabled => Volatile.Read(ref _globalEnabled);

    public bool IsEnabled(string? sessionId)
    {
        if (!GlobalEnabled) return false;
        return string.IsNullOrWhiteSpace(sessionId)
            || !_sessionOverrides.TryGetValue(sessionId, out var enabled)
            || enabled;
    }

    public bool? GetSessionOverride(string sessionId) =>
        _sessionOverrides.TryGetValue(sessionId, out var enabled) ? enabled : null;

    public void SetSessionOverride(string sessionId, bool? enabled)
    {
        if (enabled.HasValue) _sessionOverrides[sessionId] = enabled.Value;
        else _sessionOverrides.TryRemove(sessionId, out _);
    }

    public void SetGlobalEnabled(bool enabled)
    {
        lock (_saveLock)
        {
            var previous = GlobalEnabled;
            Volatile.Write(ref _globalEnabled, enabled);
            try { Save(); }
            catch
            {
                Volatile.Write(ref _globalEnabled, previous);
                throw;
            }
        }
    }

    public void RegisterAgentMode(string agentId, SpecialistMemoryMode defaultMode) =>
        _agentModes.TryAdd(agentId, defaultMode);

    public SpecialistMemoryMode GetAgentMode(string agentId) =>
        _agentModes.TryGetValue(agentId, out var mode) ? mode : SpecialistMemoryMode.Shared;

    public void SetAgentMode(string agentId, SpecialistMemoryMode mode)
    {
        lock (_saveLock)
        {
            var hadPrevious = _agentModes.TryGetValue(agentId, out var previous);
            _agentModes[agentId] = mode;
            try { Save(); }
            catch
            {
                if (hadPrevious) _agentModes[agentId] = previous;
                else _agentModes.TryRemove(agentId, out _);
                throw;
            }
        }
    }

    public IReadOnlyDictionary<string, SpecialistMemoryMode> GetAgentModes() =>
        new Dictionary<string, SpecialistMemoryMode>(_agentModes, StringComparer.OrdinalIgnoreCase);

    private void Load()
    {
        if (!File.Exists(_settingsPath)) return;
        try
        {
            var settings = JsonSerializer.Deserialize<PersistedMemorySettings>(
                File.ReadAllText(_settingsPath), JsonOptions);
            if (settings?.GlobalEnabled is bool enabled)
                _globalEnabled = enabled;
            if (settings?.AgentModes is not null)
            {
                foreach (var (agentId, value) in settings.AgentModes)
                    if (Enum.TryParse<SpecialistMemoryMode>(value, true, out var mode))
                        _agentModes[agentId] = mode;
            }
        }
        catch (Exception)
        {
            // A corrupt runtime preference file must not prevent the agent from starting.
        }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var settings = new PersistedMemorySettings
                {
                    GlobalEnabled = GlobalEnabled,
                    AgentModes = _agentModes.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.ToString(),
                        StringComparer.OrdinalIgnoreCase)
                };
                File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(tempPath, _settingsPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
    }

    private sealed class PersistedMemorySettings
    {
        public bool? GlobalEnabled { get; set; }
        public Dictionary<string, string>? AgentModes { get; set; }
    }
}

/// <summary>Exposes whether a memory wrapper is available in the current async session scope.</summary>
public interface ISessionMemoryAccess
{
    bool IsEnabled { get; }
}

/// <summary>
/// Routes memory operations through the current global/session policy and, for specialists,
/// dynamically selects shared, isolated, or disabled storage.
/// </summary>
public sealed class RoutedMemory : IMemory, ISessionMemoryAccess, IAsyncDisposable
{
    private readonly IMemory _shared;
    private readonly MemoryAccessPolicy _policy;
    private readonly string _agentId;
    private readonly Lazy<HybridMemory>? _isolated;

    public RoutedMemory(
        IMemory shared,
        MemoryAccessPolicy policy,
        string agentId,
        Func<HybridMemory>? isolatedFactory = null)
    {
        _shared = shared;
        _policy = policy;
        _agentId = agentId;
        if (isolatedFactory is not null)
            _isolated = new Lazy<HybridMemory>(isolatedFactory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsEnabled => Resolve() is not null;

    public async Task AddAsync(MemoryEntry entry)
    {
        var memory = Resolve() ?? throw new InvalidOperationException("Memory is disabled for this session.");
        await memory.AddAsync(entry);
    }

    public Task<List<MemoryEntry>> SearchAsync(string query, int limit = 10) =>
        Resolve() is { } memory
            ? memory.SearchAsync(query, limit)
            : Task.FromResult(new List<MemoryEntry>());

    public Task<List<MemoryEntry>> GetAllAsync() =>
        Resolve() is { } memory
            ? memory.GetAllAsync()
            : Task.FromResult(new List<MemoryEntry>());

    public async Task ClearAsync()
    {
        var memory = Resolve() ?? throw new InvalidOperationException("Memory is disabled for this session.");
        await memory.ClearAsync();
    }

    public Task<List<MemoryEntry>> GetRecentAsync(int count = 10) =>
        Resolve() is { } memory
            ? memory.GetRecentAsync(count)
            : Task.FromResult(new List<MemoryEntry>());

    public async ValueTask DisposeAsync()
    {
        if (_isolated is { IsValueCreated: true })
            await _isolated.Value.DisposeAsync();
    }

    private IMemory? Resolve()
    {
        if (!_policy.IsEnabled(FoxAgent.CurrentSessionKey.Value)) return null;
        if (_agentId.Equals("main", StringComparison.OrdinalIgnoreCase)) return _shared;

        return _policy.GetAgentMode(_agentId) switch
        {
            SpecialistMemoryMode.Shared => _shared,
            SpecialistMemoryMode.Isolated when _isolated is not null => _isolated.Value,
            _ => null
        };
    }
}
