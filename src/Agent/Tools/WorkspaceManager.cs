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

        // If no workspaces configured, default to current directory
        if (_allowedWorkspaces.Count == 0)
        {
            _allowedWorkspaces.Add(AppContext.BaseDirectory);
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
