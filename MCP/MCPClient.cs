using AgentFox.Models;
using AgentFox.Tools;
using Newtonsoft.Json;

namespace AgentFox.MCP;

/// <summary>
/// MCP (Model Context Protocol) Server implementation
/// Allows agents to connect to external MCP servers for additional tools
/// </summary>
public class MCPServer
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new();
    public bool IsConnected { get; private set; }
    public List<ToolDefinition> AvailableTools { get; private set; } = new();
    
    private readonly HttpClient _httpClient;
    
    public MCPServer(string name, string url)
    {
        Name = name;
        Url = url;
        _httpClient = new HttpClient();
    }
    
    /// <summary>
    /// Initialize connection to MCP server
    /// </summary>
    public async Task<bool> InitializeAsync()
    {
        try
        {
            // In a real implementation, this would call the MCP server's initialize endpoint
            // For now, we'll simulate the connection
            await Task.Delay(100);
            IsConnected = true;
            return true;
        }
        catch
        {
            IsConnected = false;
            return false;
        }
    }
    
    /// <summary>
    /// List available tools from MCP server
    /// </summary>
    public async Task<List<ToolDefinition>> ListToolsAsync()
    {
        if (!IsConnected)
            return new List<ToolDefinition>();
            
        try
        {
            // In a real implementation, this would call the MCP server's tools/list endpoint
            // Simulated response
            await Task.Delay(50);
            return AvailableTools;
        }
        catch
        {
            return new List<ToolDefinition>();
        }
    }
    
    /// <summary>
    /// Execute a tool on the MCP server
    /// </summary>
    public async Task<MCPResponse> ExecuteToolAsync(string toolName, Dictionary<string, object?> arguments)
    {
        if (!IsConnected)
            return new MCPResponse { Success = false, Error = "Not connected to MCP server" };
            
        try
        {
            // In a real implementation, this would call the MCP server's tools/call endpoint
            var request = new
            {
                name = toolName,
                arguments = arguments
            };
            
            // Simulated response
            await Task.Delay(100);
            return new MCPResponse 
            { 
                Success = true, 
                Result = JsonConvert.SerializeObject(new { message = "Tool executed successfully" }) 
            };
        }
        catch (Exception ex)
        {
            return new MCPResponse { Success = false, Error = ex.Message };
        }
    }
    
    /// <summary>
    /// Disconnect from MCP server
    /// </summary>
    public void Disconnect()
    {
        IsConnected = false;
        AvailableTools.Clear();
    }
}

/// <summary>
/// MCP Response
/// </summary>
public class MCPResponse
{
    public bool Success { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// MCP Client for connecting to multiple servers
/// </summary>
public class MCPClient
{
    private readonly Dictionary<string, MCPServer> _servers = new();
    private readonly ToolRegistry _toolRegistry;
    
    public IReadOnlyDictionary<string, MCPServer> Servers => _servers;
    
    public MCPClient(ToolRegistry toolRegistry)
    {
        _toolRegistry = toolRegistry;
    }
    
    /// <summary>
    /// Add and connect to an MCP server
    /// </summary>
    public async Task<bool> AddServerAsync(string name, string url)
    {
        var server = new MCPServer(name, url);
        var success = await server.InitializeAsync();
        
        if (success)
        {
            _servers[name] = server;
            
            // Register tools from the server
            var tools = await server.ListToolsAsync();
            foreach (var tool in tools)
            {
                // Wrap MCP tool in a local tool
                var wrappedTool = new MCPToolWrapper(server, tool);
                _toolRegistry.Register(wrappedTool);
            }
        }
        
        return success;
    }
    
    /// <summary>
    /// Remove an MCP server
    /// </summary>
    public void RemoveServer(string name)
    {
        if (_servers.TryGetValue(name, out var server))
        {
            server.Disconnect();
            _servers.Remove(name);
        }
    }
    
    /// <summary>
    /// Get all connected servers
    /// </summary>
    public List<MCPServer> GetConnectedServers()
    {
        return _servers.Values.Where(s => s.IsConnected).ToList();
    }
}

/// <summary>
/// Wrapper to convert MCP tool to local ITool
/// </summary>
public class MCPToolWrapper : ITool
{
    private readonly MCPServer _server;
    private readonly ToolDefinition _definition;
    
    public string Name => $"mcp_{_definition.Name}";
    public string Description => $"[MCP] {_definition.Description}";
    public Dictionary<string, Tools.ToolParameter> Parameters => _definition.Parameters
        .ToDictionary(p => p.Key, p => new Tools.ToolParameter
        {
            Type = p.Value.Type,
            Description = p.Value.Description,
            Required = p.Value.Required
        });
    
    public MCPToolWrapper(MCPServer server, ToolDefinition definition)
    {
        _server = server;
        _definition = definition;
    }
    
    public async Task<ToolResult> ExecuteAsync(Dictionary<string, object?> arguments)
    {
        var response = await _server.ExecuteToolAsync(_definition.Name, arguments);
        
        if (response.Success)
            return ToolResult.Ok(response.Result ?? "");
        else
            return ToolResult.Fail(response.Error ?? "Unknown error");
    }
}

/// <summary>
/// MCP Protocol messages
/// </summary>
public static class MCPProtocol
{
    public const string Initialize = "initialize";
    public const string ToolsList = "tools/list";
    public const string ToolsCall = "tools/call";
    public const string ResourcesList = "resources/list";
    public const string ResourcesRead = "resources/read";
    public const string PromptsList = "prompts/list";
    public const string PromptsGet = "prompts/get";
}
