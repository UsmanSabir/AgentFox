using System.Diagnostics;
using System.Text;
using AgentFox.Plugins.Interfaces;
using AgentFox.Tools;

namespace AgentFox.Runtime;

/// <summary>
/// Code execution sandbox for running code safely
/// </summary>
public class CodeSandbox
{
    private readonly string _workingDirectory;
    private readonly int _timeoutSeconds;
    private readonly long _maxMemoryBytes;
    
    public CodeSandbox(string? workingDirectory = null, int timeoutSeconds = 30, long maxMemoryBytes = 512 * 1024 * 1024)
    {
        _workingDirectory = workingDirectory ?? Path.GetTempPath();
        _timeoutSeconds = timeoutSeconds;
        _maxMemoryBytes = maxMemoryBytes;
    }
    
    /// <summary>
    /// Execute C# code
    /// </summary>
    public async Task<CodeExecutionResult> ExecuteCSharpAsync(string code)
    {
        // Create a temporary project
        var projectDir = Path.Combine(_workingDirectory, $"csharp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectDir);
        
        try
        {
            // Write code to file
            var programFile = Path.Combine(projectDir, "Program.cs");
            await File.WriteAllTextAsync(programFile, WrapCSharpCode(code));
            
            // Create project file
            var projectFile = Path.Combine(projectDir, "CodeSandbox.csproj");
            var projectContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>";
            await File.WriteAllTextAsync(projectFile, projectContent);
            
            // Build and run
            return await RunProcessAsync("dotnet", $"run --project {projectFile}", projectDir);
        }
        finally
        {
            // Cleanup
            try { Directory.Delete(projectDir, true); } catch { }
        }
    }
    
    /// <summary>
    /// Execute Python code
    /// </summary>
    public async Task<CodeExecutionResult> ExecutePythonAsync(string code)
    {
        var scriptFile = Path.Combine(_workingDirectory, $"python_{Guid.NewGuid():N}.py");
        
        try
        {
            await File.WriteAllTextAsync(scriptFile, code);
            return await RunProcessAsync("python", scriptFile, _workingDirectory);
        }
        finally
        {
            try { File.Delete(scriptFile); } catch { }
        }
    }
    
    /// <summary>
    /// Execute JavaScript/Node.js code
    /// </summary>
    public async Task<CodeExecutionResult> ExecuteJavaScriptAsync(string code)
    {
        var scriptFile = Path.Combine(_workingDirectory, $"js_{Guid.NewGuid():N}.js");
        
        try
        {
            await File.WriteAllTextAsync(scriptFile, code);
            return await RunProcessAsync("node", scriptFile, _workingDirectory);
        }
        finally
        {
            try { File.Delete(scriptFile); } catch { }
        }
    }
    
    /// <summary>
    /// Execute shell command
    /// </summary>
    public async Task<CodeExecutionResult> ExecuteShellAsync(string command)
    {
        return await RunProcessAsync("cmd.exe", $"/c {command}", _workingDirectory);
    }
    
    private async Task<CodeExecutionResult> RunProcessAsync(string fileName, string arguments, string workingDir)
    {
        var result = new CodeExecutionResult();
        var output = new StringBuilder();
        var error = new StringBuilder();
        
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            
            process.Start();
            
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(_timeoutSeconds));
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            
            var completedTask = await Task.WhenAny(
                Task.WhenAll(process.WaitForExitAsync(), outputTask, errorTask),
                timeoutTask
            );
            
            if (completedTask == timeoutTask)
            {
                process.Kill();
                result.Success = false;
                result.Error = $"Execution timed out after {_timeoutSeconds} seconds";
                return result;
            }
            
            result.Output = await outputTask;
            result.Error = await errorTask;
            result.ExitCode = process.ExitCode;
            result.Success = process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }
        
        return result;
    }
    
    private string WrapCSharpCode(string code)
    {
        // Check if it's already a complete program
        if (code.Contains("class Program") && code.Contains("Main"))
            return code;
        
        return $@"
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

class Program
{{
    static void Main(string[] args)
    {{
        {code}
    }}
}}";
    }
}

/// <summary>
/// Code execution result
/// </summary>
public class CodeExecutionResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public TimeSpan Duration { get; set; }
    
    public static CodeExecutionResult Ok(string output) => new() { Success = true, Output = output };
    public static CodeExecutionResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Code execution tool for agents
/// </summary>
public class CodeExecutionTool : BaseTool
{
    private readonly CodeSandbox _sandbox;
    
    public CodeExecutionTool(CodeSandbox? sandbox = null)
    {
        _sandbox = sandbox ?? new CodeSandbox();
    }
    
    public override string Name => "execute_code";
    public override string Description => "Execute code in a sandboxed environment";
    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["language"] = new() { Type = "string", Description = "Programming language: csharp, python, javascript, shell", Required = true },
        ["code"] = new() { Type = "string", Description = "Code to execute", Required = true }
    };
    
    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var language = arguments["language"]?.ToString()?.ToLower();
        var code = arguments["code"]?.ToString();
        
        if (string.IsNullOrEmpty(language) || string.IsNullOrEmpty(code))
            return ToolResult.Fail("Language and code are required");
        
        CodeExecutionResult result;
        
        switch (language)
        {
            case "csharp":
            case "c#":
                result = await _sandbox.ExecuteCSharpAsync(code);
                break;
            case "python":
            case "py":
                result = await _sandbox.ExecutePythonAsync(code);
                break;
            case "javascript":
            case "js":
            case "node":
                result = await _sandbox.ExecuteJavaScriptAsync(code);
                break;
            case "shell":
            case "bash":
            case "cmd":
                result = await _sandbox.ExecuteShellAsync(code);
                break;
            default:
                return ToolResult.Fail($"Unsupported language: {language}");
        }
        
        if (result.Success)
            return ToolResult.Ok(result.Output);
        else
            return ToolResult.Fail(result.Error);
    }
}
