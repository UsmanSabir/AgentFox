using Microsoft.Extensions.Configuration;

namespace AgentFox.Tools;

/// <summary>
/// Manages allowed workspace paths for file operations
/// </summary>
public class WorkspaceManager
{
    private readonly List<string> _allowedWorkspaces = new();
    private readonly bool _restrictToWorkspace;

    public WorkspaceManager(IConfiguration configuration)
    {
        _restrictToWorkspace = configuration.GetValue("RestrictToWorkspace", true);

        // Load workspaces from configuration (e.g., appsettings.json or Environment Variables)
        var workspaces = configuration.GetSection("Workspaces").Get<string[]>();

        if (workspaces != null)
        {
            foreach (var ws in workspaces)
            {
                if (!string.IsNullOrWhiteSpace(ws))
                {
                    _allowedWorkspaces.Add(Path.GetFullPath(ws));
                }
            }
        }

        EnsureUsablePrimaryWorkspace();
    }

    /// <summary>
    /// Guarantees <c>_allowedWorkspaces[0]</c> is a directory we can actually write to.
    /// A published exe may carry a stale absolute path in appsettings.json (e.g. a dev
    /// machine's drive that doesn't exist on the user's box); without this the first
    /// write — the SQLite memory DB — throws and crashes startup. We promote the first
    /// usable configured workspace, else fall back to a writable per-user location.
    /// </summary>
    private void EnsureUsablePrimaryWorkspace()
    {
        var usable = _allowedWorkspaces.FindIndex(TryEnsureDirectory);
        if (usable == 0)
            return;
        if (usable > 0)
        {
            var ws = _allowedWorkspaces[usable];
            _allowedWorkspaces.RemoveAt(usable);
            _allowedWorkspaces.Insert(0, ws);
            return;
        }

        // No configured workspace is usable — fall back so the app still runs.
        foreach (var candidate in DefaultWorkspaceCandidates())
        {
            if (TryEnsureDirectory(candidate))
            {
                _allowedWorkspaces.Insert(0, candidate);
                return;
            }
        }
        _allowedWorkspaces.Insert(0, AppContext.BaseDirectory);
    }

    private static IEnumerable<string> DefaultWorkspaceCandidates()
    {
        // Anchor workspace-relative data (sessions, memory, logs) to a STABLE per-install
        // location so it does not depend on the directory the process was launched from.
        // The install dir (where AgentFox.exe lives) is preferred; only if it is not
        // writable do we fall back to a per-user location, and the launch directory is a
        // last resort. Previously CurrentDirectory was tried first, so launching via the
        // `agentfox` launcher from an arbitrary shell resolved a different session store
        // than running the exe from the install directory — the web UI then listed the
        // sessions of whichever store the process bootstrapped while transcripts lived in
        // another, surfacing as 404 on export and empty on load.
        yield return AppContext.BaseDirectory;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
            yield return Path.Combine(local, "AgentFox");
        yield return Environment.CurrentDirectory;
    }

    private static bool TryEnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// For testing or manual configuration
    /// </summary>
    public WorkspaceManager(IEnumerable<string> workspaces, bool restrictToWorkspace = true)
    {
        _restrictToWorkspace = restrictToWorkspace;

        foreach (var ws in workspaces)
        {
            if (!string.IsNullOrWhiteSpace(ws))
            {
                _allowedWorkspaces.Add(Path.GetFullPath(ws));
            }
        }

        if (_allowedWorkspaces.Count == 0)
        {
            _allowedWorkspaces.Add(AppContext.BaseDirectory);
        }
    }

    /// <summary>
    /// Checks if a given path is within any of the allowed workspaces.
    /// Always returns true when RestrictToWorkspace is disabled.
    /// </summary>
    public bool IsPathAllowed(string path)
    {
        if (!_restrictToWorkspace)
            return true;

        try
        {
            var fullPath = Path.GetFullPath(path);

            foreach (var workspace in _allowedWorkspaces)
            {
                // Ensure directory separator at the end so we don't accidentally allow sibling directories
                // e.g. "C:\workspace" allowing "C:\workspace2"
                var wsDir = workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;

                var targetDir = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;

                if (targetDir.StartsWith(wsDir, StringComparison.OrdinalIgnoreCase) || targetDir.Equals(wsDir, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            // Invalid paths are not allowed
            return false;
        }
    }

    /// <summary>
    /// Resolves a path, checking if it's allowed.
    /// If relative, it resolves it against the first configured workspace.
    /// </summary>
    public string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return _allowedWorkspaces[0];

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        // Resolve relative path against the primary workspace
        return Path.GetFullPath(Path.Combine(_allowedWorkspaces[0], path));
    }
}
