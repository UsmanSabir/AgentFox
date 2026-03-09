using AgentFox.Models;
using AgentFox.Tools;
using Newtonsoft.Json;
using System.Text;

namespace AgentFox.MCP;

/// <summary>
/// JSON-RPC Request for MCP
/// </summary>
public class JsonRpcRequest
{
    [JsonProperty("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";
    
    [JsonProperty("method")]
    public string Method { get; set; } = string.Empty;
    
    [JsonProperty("params")]
    public Dictionary<string, object?> Params { get; set; } = new();
    
    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();
}

/// <summary>
/// JSON-RPC Response for MCP
/// </summary>
public class JsonRpcResponse<T>
{
    [JsonProperty("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";
    
    [JsonProperty("result")]
    public T? Result { get; set; }
    
    [JsonProperty("error")]
    public JsonRpcError? Error { get; set; }
    
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;
}

/// <summary>
/// JSON-RPC Error
/// </summary>
public class JsonRpcError
{
    [JsonProperty("code")]
    public int Code { get; set; }
    
    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;
    
    [JsonProperty("data")]
    public dynamic? Data { get; set; }
}

/// <summary>
/// MCP (Model Context Protocol) Server implementation with real HTTP support
/// Allows agents to connect to external MCP servers for additional tools
/// </summary>
public class MCPServer
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new();
    public bool IsConnected { get; private set; }
    public List<ToolDefinition> AvailableTools { get; private set; } = new();
    public string? ServerVersion { get; private set; }
    
    private readonly HttpClient _httpClient;
    private readonly int _timeoutSeconds;
    
    public MCPServer(string name, string url, int timeoutSeconds = 30)
    {
        Name = name;
        Url = url;
        _timeoutSeconds = timeoutSeconds;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    }
    
    /// <summary>
    /// Initialize connection to MCP server with real protocol
    /// </summary>
    public async Task<bool> InitializeAsync()
    {
        try
        {
            var request = new JsonRpcRequest
            {
                Method = MCPProtocol.Initialize,
                Params = new Dictionary<string, object?>
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new { tools = true, resources = true, prompts = true },
                    ["clientInfo"] = new { name = "CSharpClaw", version = "1.0.0" }
                }
            };
            
            var response = await SendJsonRpcRequestAsync(request);
            
            if (response?.Result != null)
            {
                var resultDict = (Dictionary<string, object?>)response.Result;
                ServerVersion = resultDict.ContainsKey("serverInfo") 
                    ? resultDict["serverInfo"]?.ToString() 
                    : "Unknown";
                
                IsConnected = true;
                return true;
            }
            
            IsConnected = false;
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MCP Initialize failed: {ex.Message}");
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
            var request = new JsonRpcRequest
            {
                Method = MCPProtocol.ToolsList,
                Params = new Dictionary<string, object?>()
            };
            
            var response = await SendJsonRpcRequestAsync(request);
            
            if (response?.Result != null)
            {
                var resultDict = (Dictionary<string, object?>)response.Result;
                if (resultDict.TryGetValue("tools", out var value))
                {
                    AvailableTools = JsonConvert.DeserializeObject<List<ToolDefinition>>(
                        JsonConvert.SerializeObject(value)) ?? new();
                    return AvailableTools;
                }
            }
            
            return new List<ToolDefinition>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MCP ListTools failed: {ex.Message}");
            return new List<ToolDefinition>();
        }
    }
    
    /// <summary>
    /// Execute a tool on the MCP server using real JSON-RPC protocol
    /// </summary>
    public async Task<MCPResponse> ExecuteToolAsync(string toolName, Dictionary<string, object?> arguments)
    {
        if (!IsConnected)
            return new MCPResponse { Success = false, Error = "Not connected to MCP server" };
            
        try
        {
            var request = new JsonRpcRequest
            {
                Method = MCPProtocol.ToolsCall,
                Params = new Dictionary<string, object?>
                {
                    ["name"] = toolName,
                    ["arguments"] = arguments
                }
            };
            
            var response = await SendJsonRpcRequestAsync(request);
            
            if (response?.Error != null)
            {
                return new MCPResponse 
                { 
                    Success = false, 
                    Error = response.Error.Message 
                };
            }
            
            if (response?.Result != null)
            {
                var resultJson = JsonConvert.SerializeObject(response.Result);
                return new MCPResponse 
                { 
                    Success = true, 
                    Result = resultJson
                };
            }
            
            return new MCPResponse { Success = false, Error = "No result from MCP server" };
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
        ServerVersion = null;
    }
    
    /// <summary>
    /// Send a JSON-RPC request to the MCP server
    /// </summary>
    private async Task<JsonRpcResponse<dynamic>?> SendJsonRpcRequestAsync(JsonRpcRequest request)
    {
        try
        {
            var json = JsonConvert.SerializeObject(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var httpResponse = await _httpClient.PostAsync(Url, content);
            httpResponse.EnsureSuccessStatusCode();
            
            var responseJson = await httpResponse.Content.ReadAsStringAsync();
            var response = JsonConvert.DeserializeObject<JsonRpcResponse<dynamic>>(responseJson);
            
            return response;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"JSON-RPC request failed: {ex.Message}");
            throw;
        }
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