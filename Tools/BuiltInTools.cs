using System.Diagnostics;
using AgentFox.Models;

namespace AgentFox.Tools;

/// <summary>
/// Tool for executing shell commands
/// </summary>
public class ShellCommandTool : BaseTool
{
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

        var workingDir = arguments["working_directory"]?.ToString() ?? Directory.GetCurrentDirectory();

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command}",
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

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
        var path = arguments["path"]?.ToString();
        if (string.IsNullOrEmpty(path))
            return ToolResult.Fail("No file path provided");

        try
        {
            if (!File.Exists(path))
                return ToolResult.Fail($"File not found: {path}");

            var lines = await File.ReadAllLinesAsync(path);
            var startLine = Convert.ToInt32(arguments.GetValueOrDefault("start_line") ?? 1);
            var endLine = arguments["end_line"] != null ? Convert.ToInt32(arguments["end_line"]) : lines.Length;

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
        var path = arguments["path"]?.ToString();
        var content = arguments["content"]?.ToString();
        
        if (string.IsNullOrEmpty(path))
            return Task.FromResult(ToolResult.Fail("No file path provided"));
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
    public override string Name => "list_files";
    public override string Description => "List files in a directory";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["path"] = new() { Type = "string", Description = "Directory path to list", Required = false, Default = "." },
        ["recursive"] = new() { Type = "boolean", Description = "List files recursively", Required = false, Default = false }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var path = arguments["path"]?.ToString() ?? ".";
        var recursive = Convert.ToBoolean(arguments.GetValueOrDefault("recursive") ?? false);

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
        var path = arguments["path"]?.ToString() ?? ".";
        var pattern = arguments["pattern"]?.ToString();
        var filePattern = arguments["file_pattern"]?.ToString() ?? "*";

        if (string.IsNullOrEmpty(pattern))
            return Task.FromResult(ToolResult.Fail("No search pattern provided"));

        try
        {
            if (!Directory.Exists(path))
                return Task.FromResult(ToolResult.Fail($"Directory not found: {path}"));

            var files = Directory.GetFiles(path, filePattern, SearchOption.AllDirectories);
            var results = new List<string>();

            foreach (var file in files)
            {
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
    public override string Name => "make_directory";
    public override string Description => "Create a new directory";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["path"] = new() { Type = "string", Description = "Path of the directory to create", Required = true }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var path = arguments["path"]?.ToString();
        
        if (string.IsNullOrEmpty(path))
            return Task.FromResult(ToolResult.Fail("No directory path provided"));

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
    public override string Name => "delete";
    public override string Description => "Delete a file or directory";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["path"] = new() { Type = "string", Description = "Path to delete", Required = true },
        ["recursive"] = new() { Type = "boolean", Description = "Delete directories recursively", Required = false, Default = false }
    };

    protected override Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var path = arguments["path"]?.ToString();
        
        if (string.IsNullOrEmpty(path))
            return Task.FromResult(ToolResult.Fail("No path provided"));

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
        var info = $"""
            Operating System: {Environment.OSVersion}
            Machine Name: {Environment.MachineName}
            Current Directory: {Directory.GetCurrentDirectory()}
            User: {Environment.UserName}
            Processor Count: {Environment.ProcessorCount}
            CLR Version: {Environment.Version}
            """;
        
        return Task.FromResult(ToolResult.Ok(info));
    }
}
