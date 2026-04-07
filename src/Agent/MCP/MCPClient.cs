using AgentFox.Http;
using AgentFox.Models;
using AgentFox.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Net.Http.Headers;
using System.Linq;

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
        _httpClient = HttpResilienceFactory.Create(TimeSpan.FromSeconds(timeoutSeconds * 4));
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
                // Normalize the result object into a dictionary so we can
                // safely access properties regardless of whether it's a JObject
                // or already a Dictionary.
                Dictionary<string, object?> resultDict;

                if (response.Result is Newtonsoft.Json.Linq.JObject jObject)
                {
                    resultDict = jObject.ToObject<Dictionary<string, object?>>() ?? new Dictionary<string, object?>();
                }
                else if (response.Result is Dictionary<string, object?> dict)
                {
                    resultDict = dict;
                }
                else
                {
                    // Fallback: serialize and deserialize into the expected shape
                    var normalizedJson = JsonConvert.SerializeObject(response.Result);
                    resultDict = JsonConvert.DeserializeObject<Dictionary<string, object?>>(normalizedJson)
                                 ?? new Dictionary<string, object?>();
                }

                if (resultDict.TryGetValue("serverInfo", out var serverInfoObj) && serverInfoObj != null)
                {
                    // serverInfo is often an object with name/version – store a readable representation
                    ServerVersion = serverInfoObj.ToString();
                }
                else
                {
                    ServerVersion = "Unknown";
                }

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
                // Normalize the result object into a dictionary so we can safely
                // access properties regardless of whether it's a JObject or a Dictionary.
                Dictionary<string, object?> resultDict;

                if (response.Result is Newtonsoft.Json.Linq.JObject jObject)
                {
                    resultDict = jObject.ToObject<Dictionary<string, object?>>() ?? new Dictionary<string, object?>();
                }
                else if (response.Result is Dictionary<string, object?> dict)
                {
                    resultDict = dict;
                }
                else
                {
                    var normalizedJson = JsonConvert.SerializeObject(response.Result);
                    resultDict = JsonConvert.DeserializeObject<Dictionary<string, object?>>(normalizedJson)
                                 ?? new Dictionary<string, object?>();
                }

                if (resultDict.TryGetValue("tools", out var value) && value != null)
                {
                    // MCP tool schema: each tool typically has
                    // - name
                    // - description
                    // - inputSchema: { type: "object", properties: { ... }, required: [...] }
                    // We translate this into our internal ToolDefinition/ToolParameter model.
                    var toolsToken = value as JToken ?? JToken.FromObject(value);
                    var parsedTools = new List<ToolDefinition>();

                    if (toolsToken is JArray toolsArray)
                    {
                        foreach (var toolItem in toolsArray.OfType<JObject>())
                        {
                            var name = toolItem["name"]?.ToString() ?? string.Empty;
                            var description = toolItem["description"]?.ToString() ?? string.Empty;

                            var toolDef = new ToolDefinition
                            {
                                Name = name,
                                Description = description,
                                Parameters = new Dictionary<string, AgentFox.Models.ToolParameter>()
                            };

                            var inputSchema = toolItem["inputSchema"] as JObject;
                            if (inputSchema != null)
                            {
                                var properties = inputSchema["properties"] as JObject;
                                var requiredArray = inputSchema["required"] as JArray;
                                var requiredNames = requiredArray != null
                                    ? new HashSet<string>(requiredArray.Values<string>().Where(n => n != null)!)
                                    : new HashSet<string>();

                                if (properties != null)
                                {
                                    foreach (var prop in properties.Properties())
                                    {
                                        var propName = prop.Name;
                                        var propSchema = prop.Value as JObject ?? new JObject();

                                        var param = new AgentFox.Models.ToolParameter
                                        {
                                            Type = propSchema["type"]?.ToString() ?? "string",
                                            Description = propSchema["description"]?.ToString() ?? string.Empty,
                                            Required = requiredNames.Contains(propName),
                                            Default = propSchema["default"]?.ToObject<object?>(),
                                            JsonSchema = propSchema.ToString(Formatting.None),
                                            Example = propSchema["example"]?.ToObject<object?>(),
                                            Pattern = propSchema["pattern"]?.ToString(),
                                            MinLength = propSchema["minLength"]?.ToObject<int?>(),
                                            MaxLength = propSchema["maxLength"]?.ToObject<int?>(),
                                            Minimum = propSchema["minimum"]?.ToObject<decimal?>(),
                                            Maximum = propSchema["maximum"]?.ToObject<decimal?>()
                                        };

                                        toolDef.Parameters[propName] = param;
                                    }
                                }
                            }

                            parsedTools.Add(toolDef);
                        }
                    }

                    AvailableTools = parsedTools;
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

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, Url)
            {
                Content = content
            };

            // Apply any configured headers for this MCP server
            foreach (var header in Headers)
            {
                if (!httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    httpRequest.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            // Ensure Accept header is compatible with MCP streamable HTTP servers.
            // According to the MCP spec, clients must advertise support for both
            // application/json and text/event-stream to avoid 406 responses.
            if (!httpRequest.Headers.Accept.Any(h => string.Equals(h.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
            {
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            }
            if (!httpRequest.Headers.Accept.Any(h => string.Equals(h.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)))
            {
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            }

            var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
            httpResponse.EnsureSuccessStatusCode();

            // Handle both standard JSON responses and streamable HTTP (text/event-stream)
            var mediaType = httpResponse.Content.Headers.ContentType?.MediaType;
            string responseJson;

            if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                // Read SSE stream until we get the first "data:" line with JSON
                await using var stream = await httpResponse.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string? line;
                StringBuilder dataBuilder = new StringBuilder();

                while ((line = await reader.ReadLineAsync()) != null)
                {
                    // SSE comments or empty lines
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith(":", StringComparison.Ordinal))
                        continue;

                    // Data line: accumulate payload (may be split across multiple data: lines)
                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        var dataPart = line.Substring("data:".Length).TrimStart();
                        dataBuilder.Append(dataPart);
                        // Many MCP servers send a single JSON object in one event; break on first.
                        break;
                    }
                }

                responseJson = dataBuilder.Length > 0 ? dataBuilder.ToString() : "{}";
            }
            else
            {
                responseJson = await httpResponse.Content.ReadAsStringAsync();
            }

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
    /// Add and connect to an MCP server with optional timeout and headers.
    /// </summary>
    public async Task<bool> AddServerAsync(string name, string url, int timeoutSeconds = 30, Dictionary<string, string>? headers = null)
    {
        var server = new MCPServer(name, url, timeoutSeconds);
        if (headers != null && headers.Count > 0)
        {
            foreach (var kvp in headers)
            {
                server.Headers[kvp.Key] = kvp.Value;
            }
        }
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
    /// Backwards-compatible overload for adding an MCP server without extra options.
    /// </summary>
    public Task<bool> AddServerAsync(string name, string url)
        => AddServerAsync(name, url, 30, null);
    
    /// <summary>
    /// Remove an MCP server and unregister its wrapped tools.
    /// </summary>
    public async Task RemoveServerAsync(string name)
    {
        if (_servers.TryGetValue(name, out var server))
        {
            server.Disconnect();
            _servers.Remove(name);

            var wrappersToRemove = _toolRegistry.GetAll()
                .OfType<MCPToolWrapper>()
                .Where(w => w.ServerName.Equals(name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var wrapper in wrappersToRemove)
            {
                await _toolRegistry.UnregisterAsync(wrapper.Name);
            }
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

    public string ServerName => _server.Name;
    public string ToolName => _definition.Name;
    
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