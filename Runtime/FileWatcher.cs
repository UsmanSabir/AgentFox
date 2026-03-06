using AgentFox.Models;
using AgentFox.Tools;

namespace AgentFox.Runtime;

using AgentFox.Agents;

/// <summary>
/// File system watcher for auto-running tasks on file changes
/// </summary>
public class FileWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly List<string> _watchedExtensions;
    private readonly Dictionary<string, DateTime> _lastEvents = new();
    private readonly TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(500);
    
    public event EventHandler<FileChangedEventArgs>? FileChanged;
    public bool IsRunning => _watcher.EnableRaisingEvents;
    
    public FileWatcher(string path, string filter = "*.*", bool recursive = true)
    {
        _watchedExtensions = new List<string>();
        _watcher = new FileSystemWatcher(path)
        {
            Filter = filter,
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
        };
        
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
    }
    
    public FileWatcher AddExtension(string extension)
    {
        if (!extension.StartsWith("."))
            extension = "." + extension;
        
        if (!_watchedExtensions.Contains(extension))
            _watchedExtensions.Add(extension);
        
        return this;
    }
    
    public FileWatcher AddExtensions(params string[] extensions)
    {
        foreach (var ext in extensions)
            AddExtension(ext);
        
        return this;
    }
    
    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
    }
    
    public void Stop()
    {
        _watcher.EnableRaisingEvents = false;
    }
    
    public void Dispose()
    {
        _watcher.Dispose();
    }
    
    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce rapid changes
        if (_lastEvents.TryGetValue(e.FullPath, out var lastTime))
        {
            if (DateTime.UtcNow - lastTime < _debounceInterval)
                return;
        }
        
        _lastEvents[e.FullPath] = DateTime.UtcNow;
        
        // Filter by extension
        if (_watchedExtensions.Count > 0)
        {
            var ext = Path.GetExtension(e.FullPath);
            if (!_watchedExtensions.Contains(ext))
                return;
        }
        
        FileChanged?.Invoke(this, new FileChangedEventArgs
        {
            FullPath = e.FullPath,
            ChangeType = e.ChangeType,
            Name = e.Name ?? ""
        });
    }
    
    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        FileChanged?.Invoke(this, new FileChangedEventArgs
        {
            FullPath = e.FullPath,
            OldFullPath = e.OldFullPath,
            ChangeType = e.ChangeType,
            Name = e.Name ?? ""
        });
    }
}

/// <summary>
/// File change event arguments
/// </summary>
public class FileChangedEventArgs : EventArgs
{
    public string FullPath { get; set; } = string.Empty;
    public string? OldFullPath { get; set; }
    public WatcherChangeTypes ChangeType { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Auto-run manager for file watching
/// </summary>
public class AutoRunManager
{
    private readonly FoxAgent _agent;
    private readonly List<FileWatcher> _watchers = new();
    private readonly Dictionary<string, string> _fileTasks = new();
    
    public AutoRunManager(FoxAgent agent)
    {
        _agent = agent;
    }
    
    /// <summary>
    /// Watch files and run tasks on changes
    /// </summary>
    public async Task WatchAndRunAsync(string path, string task, string[]? extensions = null)
    {
        var watcher = new FileWatcher(path)
            .AddExtensions(extensions ?? new[] { ".cs", ".json", ".yaml", ".yml", ".txt" });
        
        var taskId = Guid.NewGuid().ToString();
        _fileTasks[path] = task;
        
        watcher.FileChanged += async (s, e) =>
        {
            Console.WriteLine($"[AutoRun] File {e.ChangeType}: {e.Name}");
            
            var configuredTask = _fileTasks[path];
            var message = $"{configuredTask} - File changed: {e.Name}";
            
            var result = await _agent.ExecuteAsync(message);
            Console.WriteLine($"[AutoRun] Result: {result.Output}");
        };
        
        _watchers.Add(watcher);
        watcher.Start();
        
        Console.WriteLine($"[AutoRun] Watching {path} for changes...");
    }
    
    /// <summary>
    /// Stop all watchers
    /// </summary>
    public void StopAll()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Stop();
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
