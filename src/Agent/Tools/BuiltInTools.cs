using System.Diagnostics;
using AgentFox.Plugins.Interfaces;
using AgentFox.Plugins.Security;
using ToolParameter = AgentFox.Plugins.Interfaces.ToolParameter;

namespace AgentFox.Tools;

/// <summary>
/// Tool for executing shell commands
/// </summary>
public class ShellCommandTool : BaseTool
{
    private readonly WorkspaceManager _workspaceManager;

    public ShellCommandTool(WorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public override string Name => "shell";
    public override string Description => "Execute a shell command and return the output";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["command"] = new() { Type = "string", Description = "The shell command to execute", Required = true },
        ["working_directory"] = new() { Type = "string", Description = "Working directory for the command", Required = false }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var command = arguments["command"]?.ToString();
        if (string.IsNullOrEmpty(command))
            return ToolResult.Fail("No command provided");

        var workingDir = arguments.GetValueOrDefault("working_directory")?.ToString();
        var resolvedDir = string.IsNullOrEmpty(workingDir) 
            ? _workspaceManager.ResolvePath(".") 
            : _workspaceManager.ResolvePath(workingDir);

        if (!_workspaceManager.IsPathAllowed(resolvedDir))
        {
            return ToolResult.Fail($"Access to working directory '{resolvedDir}' is denied by workspace configuration.");
        }

        // A shell is the widest credential-exfiltration path in the process: `set` dumps the
        // environment and `type appsettings.user.json` prints the key file. Refuse the command
        // outright, then start the child with secrets stripped from its environment so a
        // spelling we did not anticipate has nothing left to read.
        if (SecretGuard.TryDenyPayload(command, out var denial))
            return ToolResult.Fail(denial);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                WorkingDirectory = resolvedDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            SecretGuard.SanitizeChildEnvironment(startInfo);

            var process = new Process { StartInfo = startInfo };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (!string.IsNullOrEmpty(error))
            {
                return ToolResult.Ok($"Output:\n{output}\n\nErrors:\n{error}");
            }
            return ToolResult.Ok(output);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Tool for reading files
/// </summary>
public class ReadFileTool : BaseTool
{
    private readonly WorkspaceManager _workspaceManager;

    public ReadFileTool(WorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public override string Name => "read_file";
    public override string Description => "Read the contents of a file";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["path"] = new() { Type = "string", Description = "Path to the file to read", Required = true },
        ["start_line"] = new() { Type = "number", Description = "Line number to start reading from", Required = false, Default = 1 },
        ["end_line"] = new() { Type = "number", Description = "Line number to end at", Required = false }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var rawPath = arguments["path"]?.ToString();
        if (string.IsNullOrEmpty(rawPath))
            return ToolResult.Fail("No file path provided");

        var path = _workspaceManager.ResolvePath(rawPath);
        if (!_workspaceManager.IsPathAllowed(path))
            return ToolResult.Fail($"Access to path '{path}' is denied by workspace configuration.");

        // The workspace check is about location; this one is about content. appsettings.user.json
        // sits inside the primary workspace by design, so without this the key file is readable.
        if (SecretGuard.IsProtectedPath(path))
            return ToolResult.Fail(SecretGuard.ProtectedPathMessage(rawPath));

        try
        {
            if (!File.Exists(path))
                return ToolResult.Fail($"File not found: {path}");

            var lines = await File.ReadAllLinesAsync(path);
            var startLine = Convert.ToInt32(arguments.GetValueOrDefault("start_line") ?? 1);
            // Optional parameters must be looked up, not indexed — the model routinely omits
            // them, and an indexer throws KeyNotFoundException instead of using the default.
            var endLineArg = arguments.GetValueOrDefault("end_line");
            var endLine = endLineArg != null ? Convert.ToInt32(endLineArg) : lines.Length;

            startLine = Math.Max(1, startLine);
            endLine = Math.Min(lines.Length, endLine);

            var content = string.Join("\n", lines.Skip(startLine - 1).Take(endLine - startLine + 1));
            return ToolResult.Ok($"File: {path}\nLines {startLine}-{endLine}\n\n{content}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }
}

/// <summary>
/// Tool for writing files
/// </summary>
public class WriteFileTool : BaseTool
{
    private readonly WorkspaceManager _workspaceManager;

    public WriteFileTool(WorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public override string Name => "write_file";
    public override string Description => "Write content to a file";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["path"] = new() { Type = "string", Description = "Path to the file to write", Required = true },
        ["content"] = new() { Type = "string", Description = "Content to write to the file", Required = true },
        ["append"] = new() { Type = "boolean", Description = "Append to existing file instead of overwriting", Required = false, Default = false }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var rawPath = arguments["path"]?.ToString();
        var content = arguments["content"]?.ToString();
        
        if (string.IsNullOrEmpty(rawPath))
            return Task.FromResult(ToolResult.Fail("No file path provided"));
        
        var path = _workspaceManager.ResolvePath(rawPath);
        if (!_workspaceManager.IsPathAllowed(path))
            return Task.FromResult(ToolResult.Fail($"Access to path '{path}' is denied by workspace configuration."));
        // Writing is gated for the same reason reading is: an agent that can rewrite
        // appsettings.user.json can point a provider at an attacker endpoint, or clobber the
        // operator's keys. Config changes go through onboarding / Doctor / the web config UI.
        if (SecretGuard.IsProtectedPath(path))
            return Task.FromResult(ToolResult.Fail(SecretGuard.ProtectedPathMessage(rawPath)));
        if (content == null)
            return Task.FromResult(ToolResult.Fail("No content provided"));

        try
        {
            var append = Convert.ToBoolean(arguments.GetValueOrDefault("append") ?? false);
            
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (append)
            {
                File.AppendAllText(path, content);
            }
            else
            {
                File.WriteAllText(path, content);
            }

            return Task.FromResult(ToolResult.Ok($"Successfully wrote to {path}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(ex.Message));
        }
    }
}

/// <summary>
/// Tool for listing files in a directory
/// </summary>
public class ListFilesTool : BaseTool
{
    private readonly WorkspaceManager _workspaceManager;

    public ListFilesTool(WorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public override string Name => "list_files";
    public override string Description => "List files in a directory";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["path"] = new() { Type = "string", Description = "Directory path to list", Required = false, Default = "." },
        ["recursive"] = new() { Type = "boolean", Description = "List files recursively", Required = false, Default = false }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var rawPath = arguments.GetValueOrDefault("path")?.ToString() ?? ".";
        var recursive = Convert.ToBoolean(arguments.GetValueOrDefault("recursive") ?? false);

        var path = _workspaceManager.ResolvePath(rawPath);
        if (!_workspaceManager.IsPathAllowed(path))
            return Task.FromResult(ToolResult.Fail($"Access to path '{path}' is denied by workspace configuration."));

        try
        {
            if (!Directory.Exists(path))
                return Task.FromResult(ToolResult.Fail($"Directory not found: {path}"));

            var files = Directory.GetFiles(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            var result = string.Join("\n", files.Select(f => f.Replace(Path.GetFullPath(path), ".").TrimStart('.', Path.DirectorySeparatorChar)));
            
            return Task.FromResult(ToolResult.Ok(result));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(ex.Message));
        }
    }
}

/// <summary>
/// Tool for searching files by content
/// </summary>
public class SearchFilesTool : BaseTool
{
    private readonly WorkspaceManager _workspaceManager;

    public SearchFilesTool(WorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public override string Name => "search_files";
    public override string Description => "Search for text in files";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["path"] = new() { Type = "string", Description = "Directory to search in", Required = false, Default = "." },
        ["pattern"] = new() { Type = "string", Description = "Text pattern to search for", Required = true },
        ["file_pattern"] = new() { Type = "string", Description = "File glob pattern (e.g., *.cs)", Required = false, Default = "*" }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var rawPath = arguments.GetValueOrDefault("path")?.ToString() ?? ".";
        var pattern = arguments["pattern"]?.ToString();
        var filePattern = arguments.GetValueOrDefault("file_pattern")?.ToString() ?? "*";

        if (string.IsNullOrEmpty(pattern))
            return Task.FromResult(ToolResult.Fail("No search pattern provided"));

        var path = _workspaceManager.ResolvePath(rawPath);
        if (!_workspaceManager.IsPathAllowed(path))
            return Task.FromResult(ToolResult.Fail($"Access to path '{path}' is denied by workspace configuration."));

        try
        {
            if (!Directory.Exists(path))
                return Task.FromResult(ToolResult.Fail($"Directory not found: {path}"));

            var files = Directory.GetFiles(path, filePattern, SearchOption.AllDirectories);
            var results = new List<string>();

            foreach (var file in files)
            {
                // Credential files are skipped, not searched: a hit/miss on a guessed key
                // prefix would turn this tool into an oracle for reading them one probe at a time.
                if (SecretGuard.IsProtectedPath(file))
                    continue;

                try
                {
                    var content = File.ReadAllText(file);
                    if (content.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(file);
                    }
                }
                catch { }
            }

            if (results.Count == 0)
                return Task.FromResult(ToolResult.Ok("No matches found"));

            return Task.FromResult(ToolResult.Ok($"Found in {results.Count} files:\n" + string.Join("\n", results)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(ex.Message));
        }
    }
}

/// <summary>
/// Tool for creating directories
/// </summary>
public class MakeDirectoryTool : BaseTool
{
    private readonly WorkspaceManager _workspaceManager;

    public MakeDirectoryTool(WorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public override string Name => "make_directory";
    public override string Description => "Create a new directory";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["path"] = new() { Type = "string", Description = "Path of the directory to create", Required = true }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var rawPath = arguments["path"]?.ToString();
        
        if (string.IsNullOrEmpty(rawPath))
            return Task.FromResult(ToolResult.Fail("No directory path provided"));

        var path = _workspaceManager.ResolvePath(rawPath);
        if (!_workspaceManager.IsPathAllowed(path))
            return Task.FromResult(ToolResult.Fail($"Access to path '{path}' is denied by workspace configuration."));

        try
        {
            Directory.CreateDirectory(path);
            return Task.FromResult(ToolResult.Ok($"Created directory: {path}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(ex.Message));
        }
    }
}

/// <summary>
/// Tool for deleting files or directories
/// </summary>
public class DeleteTool : BaseTool
{
    private readonly WorkspaceManager _workspaceManager;

    public DeleteTool(WorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public override string Name => "delete";
    public override string Description => "Delete a file or directory";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["path"] = new() { Type = "string", Description = "Path to delete", Required = true },
        ["recursive"] = new() { Type = "boolean", Description = "Delete directories recursively", Required = false, Default = false }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var rawPath = arguments["path"]?.ToString();
        
        if (string.IsNullOrEmpty(rawPath))
            return Task.FromResult(ToolResult.Fail("No path provided"));

        var path = _workspaceManager.ResolvePath(rawPath);
        if (!_workspaceManager.IsPathAllowed(path))
             return Task.FromResult(ToolResult.Fail($"Access to path '{path}' is denied by workspace configuration."));
        // Deleting the key file is not a leak but it is a denial of service on the operator's
        // configuration, and a recursive delete of an install directory would take it with it.
        if (SecretGuard.IsProtectedPath(path))
            return Task.FromResult(ToolResult.Fail(SecretGuard.ProtectedPathMessage(rawPath)));

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return Task.FromResult(ToolResult.Ok($"Deleted file: {path}"));
            }
            else if (Directory.Exists(path))
            {
                var recursive = Convert.ToBoolean(arguments.GetValueOrDefault("recursive") ?? false);
                if (recursive)
                {
                    Directory.Delete(path, true);
                }
                else
                {
                    Directory.Delete(path);
                }
                return Task.FromResult(ToolResult.Ok($"Deleted directory: {path}"));
            }
            else
            {
                return Task.FromResult(ToolResult.Fail($"Path not found: {path}"));
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail(ex.Message));
        }
    }
}

/// <summary>
/// Tool for getting information about the current environment
/// </summary>
public class GetEnvironmentInfoTool : BaseTool
{
    public override string Name => "get_env_info";
    public override string Description => "Get information about the current environment";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new();

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        // Deliberately host facts only. Never add Environment.GetEnvironmentVariables() here —
        // the process environment carries provider API keys, and this tool's output goes
        // straight into model context.
        var info = $"""
            Operating System: {Environment.OSVersion}
            Machine Name: {Environment.MachineName}
            Current Directory: {AppContext.BaseDirectory}
            User: {Environment.UserName}
            Processor Count: {Environment.ProcessorCount}
            CLR Version: {Environment.Version}
            """;
        
        return Task.FromResult(ToolResult.Ok(info));
    }
}
