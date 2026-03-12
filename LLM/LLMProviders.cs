using System.Net.Http.Json;
using AgentFox.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Configuration;

namespace AgentFox.LLM;

/// <summary>
/// Interface for LLM providers
/// </summary>
public interface ILLMProvider
{
    string Name { get; }
    LLMConfig? DefaultConfig { get; set; }
    Task<string> GenerateAsync(List<Message> messages, List<ToolDefinition>? tools = null, LLMConfig? config = null);
    Task GenerateStreamingAsync(List<Message> messages, Func<string, Task> onChunk, List<ToolDefinition>? tools = null, LLMConfig? config = null);
}

/// <summary>
/// LLM Configuration
/// </summary>
public class LLMConfig
{
    public string? Model { get; set; }
    public string? BaseUrl { get; set; }
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 4096;
    public List<string>? Stop { get; set; }
    public double? TopP { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
}

/// <summary>
/// Base class for LLM providers offering common functionality like HTTP client creation
/// </summary>
public abstract class BaseLLMProvider : ILLMProvider
{
    protected readonly TimeSpan? _timeout;
    protected readonly Dictionary<string, string> _customHeaders;

    public abstract string Name { get; }
    public LLMConfig? DefaultConfig { get; set; }

    protected BaseLLMProvider(TimeSpan? timeout = null, Dictionary<string, string>? customHeaders = null)
    {
        _timeout = timeout;
        _customHeaders = customHeaders ?? new Dictionary<string, string>();
    }

    protected HttpClient CreateClient(LLMConfig? config = null)
    {
        var client = new HttpClient();
        if (_timeout.HasValue) 
        {
            client.Timeout = _timeout.Value;
        }

        foreach (var header in _customHeaders)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (config?.Headers != null)
        {
            foreach (var header in config.Headers)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return client;
    }

    public abstract Task<string> GenerateAsync(List<Message> messages, List<ToolDefinition>? tools = null, LLMConfig? config = null);
    public abstract Task GenerateStreamingAsync(List<Message> messages, Func<string, Task> onChunk, List<ToolDefinition>? tools = null, LLMConfig? config = null);
}

/// <summary>
/// OpenAI LLM Provider
/// </summary>
public class OpenAIProvider : BaseLLMProvider
{
    private readonly string _apiKey;
    private readonly string _baseUrl;
    
    public override string Name => "OpenAI";
    
    public OpenAIProvider(string apiKey, string? baseUrl = null, TimeSpan? timeout = null, Dictionary<string, string>? customHeaders = null)
        : base(timeout, customHeaders)
    {
        _apiKey = apiKey;
        _baseUrl = baseUrl ?? "https://api.openai.com/v1";
        // Ensure API key is added to headers
        _customHeaders["Authorization"] = $"Bearer {apiKey}";
    }
    
    public override async Task<string> GenerateAsync(List<Message> messages, List<ToolDefinition>? tools = null, LLMConfig? config = null)
    {
        var effectiveConfig = config ?? DefaultConfig;
        var client = CreateClient(effectiveConfig);
        
        var requestBody = new
        {            
            model = effectiveConfig?.Model ?? "gpt-4",
            messages = messages.Select(m => new { role = m.Role.ToString().ToLower(), m.Content }),
            temperature = effectiveConfig?.Temperature ?? 0.7,
            max_tokens = effectiveConfig?.MaxTokens ?? 4096,
            tools = tools?.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = new
                    {
                        type = "object",
                        properties = t.Parameters.ToDictionary(p => p.Key, p => new { type = p.Value.Type, description = p.Value.Description }),
                        required = t.Parameters.Where(p => p.Value.Required).Select(p => p.Key)
                    }
                }
            })
        };
        
        var response = await client.PostAsJsonAsync($"{_baseUrl}/chat/completions", requestBody);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"OpenAI API request failed with status {response.StatusCode}: {errorContent}");
        }
        
        var json = await response.Content.ReadAsStringAsync();
        
        if (string.IsNullOrEmpty(json))
            throw new InvalidOperationException("OpenAI API returned empty response");
        
        try
        {
            var result = JObject.Parse(json);
            var message = result["choices"]?[0]?["message"];
            if (message == null)
                throw new InvalidOperationException($"OpenAI API returned no message: {json}");
            
            var content = message["content"]?.ToString();
            var toolCalls = message["tool_calls"];
            
            // If there are tool calls, return them as JSON so the caller can process them
            if (toolCalls != null && toolCalls.HasValues)
            {
                var responseObj = new { content, tool_calls = toolCalls };
                return JsonConvert.SerializeObject(responseObj);
            }
            
            // Otherwise return the content (or throw if neither exists)
            if (string.IsNullOrEmpty(content))
                throw new InvalidOperationException($"OpenAI API returned no content: {json}");
            return content;
        }
        catch (JsonReaderException ex)
        {
            throw new InvalidOperationException($"Failed to parse OpenAI response: {json}", ex);
        }
    }
    
    public override async Task GenerateStreamingAsync(List<Message> messages, Func<string, Task> onChunk, List<ToolDefinition>? tools = null, LLMConfig? config = null)
    {
        var effectiveConfig = config ?? DefaultConfig;
        var client = CreateClient(effectiveConfig);
        var requestBody = new
        {
            model = effectiveConfig?.Model ?? "gpt-4",
            messages = messages.Select(m => new { role = m.Role.ToString().ToLower(), m.Content }),
            temperature = effectiveConfig?.Temperature ?? 0.7,
            max_tokens = effectiveConfig?.MaxTokens ?? 4096,
            stream = true,
            tools = tools?.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = new
                    {
                        type = "object",
                        properties = t.Parameters.ToDictionary(p => p.Key, p => new { type = p.Value.Type, description = p.Value.Description }),
                        required = t.Parameters.Where(p => p.Value.Required).Select(p => p.Key)
                    }
                }
            })
        };
        
        var response = await client.PostAsJsonAsync($"{_baseUrl}/chat/completions", requestBody);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"OpenAI streaming API request failed with status {response.StatusCode}: {errorContent}");
        }
        
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var sb = new System.Text.StringBuilder();
        var toolCallChunks = new System.Collections.Generic.List<JObject>();
        
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line?.StartsWith("data: ") == true && line != "data: [DONE]")
            {
                var data = line[6..];
                try
                {
                    var json = JObject.Parse(data);
                    var delta = json["choices"]?[0]?["delta"];
                    
                    // Handle content chunks
                    var contentChunk = delta?["content"]?.ToString();
                    if (!string.IsNullOrEmpty(contentChunk))
                    {
                        sb.Append(contentChunk);
                        await onChunk(contentChunk);
                    }
                    
                    // Handle tool calls in streaming (accumulate partial tool calls)
                    var toolCallDelta = delta?["tool_calls"];
                    if (toolCallDelta != null && toolCallDelta.HasValues)
                    {
                        // Store the tool call delta for later processing
                        foreach (var tc in toolCallDelta)
                        {
                            toolCallChunks.Add(JObject.FromObject(tc));
                        }
                        await onChunk(JsonConvert.SerializeObject(new { tool_calls = toolCallDelta }));
                    }
                }
                catch (JsonReaderException)
                {
                    // Malformed stream data, skip
                }
            }
        }
        
        // After stream completes, if we have accumulated tool calls, send them as a complete result
        if (toolCallChunks.Count > 0)
        {
            await onChunk(JsonConvert.SerializeObject(new { tool_calls = toolCallChunks, done = true }));
        }
    }
}

/// <summary>
/// Anthropic Claude LLM Provider
/// </summary>
public class AnthropicProvider : BaseLLMProvider
{
    private readonly string _apiKey;
    private readonly string _baseUrl;
    
    public override string Name => "Anthropic Claude";
    
    public AnthropicProvider(string apiKey, string? baseUrl = null, TimeSpan? timeout = null, Dictionary<string, string>? customHeaders = null)
        : base(timeout, customHeaders)
    {
        _apiKey = apiKey;
        _baseUrl = baseUrl ?? "https://api.anthropic.com/v1";
        // Ensure API key is added to headers
        _customHeaders["x-api-key"] = apiKey;
        _customHeaders["anthropic-version"] = "2023-06-01";
    }
    
    public override async Task<string> GenerateAsync(List<Message> messages, List<ToolDefinition>? tools = null, LLMConfig? config = null)
    {
        var effectiveConfig = config ?? DefaultConfig;
        var client = CreateClient(effectiveConfig);
        var systemMessage = messages.FirstOrDefault(m => m.Role == MessageRole.System)?.Content ?? "";
        var userMessages = messages.Where(m => m.Role != MessageRole.System).ToList();
        
        var requestBody = new
        {
            model = effectiveConfig?.Model ?? "claude-3-opus-20240229",
            max_tokens = effectiveConfig?.MaxTokens ?? 4096,
            temperature = effectiveConfig?.Temperature ?? 0.7,
            system = systemMessage,
            messages = userMessages.Select(m => new { role = m.Role.ToString().ToLower(), content = m.Content }),
            tools = tools?.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                input_schema = new
                {
                    type = "object",
                    properties = t.Parameters.ToDictionary(p => p.Key, p => new { type = p.Value.Type, description = p.Value.Description }),
                    required = t.Parameters.Where(p => p.Value.Required).Select(p => p.Key)
                }
            })
        };
        
        var response = await client.PostAsJsonAsync($"{_baseUrl}/messages", requestBody);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Anthropic API request failed with status {response.StatusCode}: {errorContent}");
        }
        
        var json = await response.Content.ReadAsStringAsync();
        
        if (string.IsNullOrEmpty(json))
            throw new InvalidOperationException("Anthropic API returned empty response");
        
        try
        {
            var result = JObject.Parse(json);
            var contentArray = result["content"] as JArray;
            if (contentArray == null || contentArray.Count == 0)
                throw new InvalidOperationException($"Anthropic API returned no content: {json}");
            
            var responseObj = new System.Collections.Generic.Dictionary<string, object>();
            var text = new System.Text.StringBuilder();
            var toolUses = new System.Collections.Generic.List<object>();
            
            // Process each content block
            foreach (var item in contentArray)
            {
                var type = item["type"]?.ToString();
                if (type == "text")
                {
                    text.Append(item["text"]?.ToString() ?? "");
                }
                else if (type == "tool_use")
                {
                    toolUses.Add(item);
                }
            }
            
            // Build response with both text and tool_use blocks if present
            if (!string.IsNullOrEmpty(text.ToString()))
                responseObj["content"] = text.ToString();
            
            if (toolUses.Count > 0)
                responseObj["tool_uses"] = toolUses;
            
            if (responseObj.Count == 0)
                throw new InvalidOperationException($"Anthropic API returned no usable content: {json}");
            
            return JsonConvert.SerializeObject(responseObj);
        }
        catch (JsonReaderException ex)
        {
            throw new InvalidOperationException($"Failed to parse Anthropic response: {json}", ex);
        }
    }
    
    public override async Task GenerateStreamingAsync(List<Message> messages, Func<string, Task> onChunk, List<ToolDefinition>? tools = null, LLMConfig? config = null)
    {
        var effectiveConfig = config ?? DefaultConfig;
        var client = CreateClient(effectiveConfig);
        var systemMessage = messages.FirstOrDefault(m => m.Role == MessageRole.System)?.Content ?? "";
        var userMessages = messages.Where(m => m.Role != MessageRole.System).ToList();
        
        var requestBody = new
        {
            model = effectiveConfig?.Model ?? "claude-3-opus-20240229",
            max_tokens = effectiveConfig?.MaxTokens ?? 1024,
            temperature = effectiveConfig?.Temperature ?? 0.7,
            system = systemMessage,
            messages = userMessages.Select(m => new { role = m.Role.ToString().ToLower(), content = m.Content }),
            stream = true,
            tools = tools?.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                input_schema = new
                {
                    type = "object",
                    properties = t.Parameters.ToDictionary(p => p.Key, p => new { type = p.Value.Type, description = p.Value.Description }),
                    required = t.Parameters.Where(p => p.Value.Required).Select(p => p.Key)
                }
            })
        };
        
        var response = await client.PostAsJsonAsync($"{_baseUrl}/messages", requestBody);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Anthropic streaming API request failed with status {response.StatusCode}: {errorContent}");
        }
        
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var toolUseBuilder = new System.Collections.Generic.List<JObject>();
        var currentToolUse = default(JObject);
        
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line?.StartsWith("data: ") == true && line != "data: [DONE]")
            {
                var data = line[6..];
                try
                {
                    var json = JObject.Parse(data);
                    var eventType = json["type"]?.ToString();
                    
                    // Handle content_block_delta events with text
                    var contentDelta = json["delta"]?["text"]?.ToString();
                    if (!string.IsNullOrEmpty(contentDelta))
                    {
                        await onChunk(contentDelta);
                    }
                    
                    // Handle content_block_start events for tool_use
                    var contentBlock = json["content_block"];
                    if (contentBlock != null && contentBlock["type"]?.ToString() == "tool_use")
                    {
                        currentToolUse = JObject.FromObject(contentBlock);
                        await onChunk(JsonConvert.SerializeObject(new { tool_uses = new[] { contentBlock } }));
                    }
                    
                    // Handle content_block_delta events for tool_use input
                    if (eventType == "content_block_delta" && currentToolUse != null)
                    {
                        var delta = json["delta"];
                        if (delta?["type"]?.ToString() == "input_delta")
                        {
                            var inputDelta = delta["text"]?.ToString();
                            if (!string.IsNullOrEmpty(inputDelta))
                            {
                                // Append to current tool_use input
                                var currentInput = currentToolUse["input"]?.ToString() ?? "";
                                currentToolUse["input"] = currentInput + inputDelta;
                                await onChunk(JsonConvert.SerializeObject(new { tool_uses = new[] { currentToolUse }, partial = true }));
                            }
                        }
                    }
                    
                    // Handle content_block_stop events
                    if (eventType == "content_block_stop" && currentToolUse != null)
                    {
                        toolUseBuilder.Add(currentToolUse);
                        currentToolUse = null;
                    }
                }
                catch (JsonReaderException)
                {
                    // Malformed stream data, skip
                }
            }
        }
        
        // After stream completes, if we have accumulated tool uses, send them as complete
        if (toolUseBuilder.Count > 0)
        {
            await onChunk(JsonConvert.SerializeObject(new { tool_uses = toolUseBuilder, done = true }));
        }
    }
}

/// <summary>
/// Ollama LLM Provider (local models)
/// </summary>
public class OllamaProvider : BaseLLMProvider
{
    private readonly string _baseUrl;
    private readonly string _model;
    
    public override string Name => "Ollama";
    
    public OllamaProvider(string? baseUrl = null, string model = "llama2", TimeSpan? timeout = null, Dictionary<string, string>? customHeaders = null)
        : base(timeout, customHeaders)
    {
        _baseUrl = baseUrl ?? "http://localhost:11434";
        _model = model;
    }
    
    public override async Task<string> GenerateAsync(List<Message> messages, List<ToolDefinition>? tools = null, LLMConfig? config = null)
    {
        var effectiveConfig = config ?? DefaultConfig;
        var client = CreateClient(effectiveConfig);
        
        // Build tools into the prompt if provided (Ollama doesn't support tools natively)
        var toolsPrompt = "";
        if (tools != null && tools.Count > 0)
        {
            var toolDefs = string.Join("\n", tools.Select(t => $"- {t.Name}: {t.Description}\n  Parameters: {string.Join(", ", t.Parameters.Keys)}"));
            toolsPrompt = $"\n\nYou have access to the following tools:\n{toolDefs}\n\nIf you need to use a tool, respond with JSON in this format:\n{{\"name\": \"tool_name\", \"arguments\": {{\"param1\": \"value1\"}}}}\n";
        }
        
        // Add tools context to the last user message if tools are available
        var modifiedMessages = messages.Select(m =>
        {
            if (m.Role == MessageRole.User && !string.IsNullOrEmpty(toolsPrompt))
            {
                return new { role = m.Role.ToString().ToLower(), content = m.Content + toolsPrompt };
            }
            return new { role = m.Role.ToString().ToLower(), content = m.Content };
        }).ToList();
        
        var requestBody = new
        {
            model = effectiveConfig?.Model ?? _model,
            messages = modifiedMessages,
            stream = false,
            options = new
            {
                temperature = effectiveConfig?.Temperature ?? 0.7,
                num_predict = effectiveConfig?.MaxTokens ?? 4096
            }
        };
        
        var response = await client.PostAsJsonAsync($"{_baseUrl}/api/chat", requestBody);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Ollama API request failed with status {response.StatusCode}: {errorContent}");
        }
        
        var json = await response.Content.ReadAsStringAsync();
        
        if (string.IsNullOrEmpty(json))
            throw new InvalidOperationException("Ollama API returned empty response");
        
        try
        {
            var result = JObject.Parse(json);
            var messageObj = result["message"];
            if (messageObj == null)
                throw new InvalidOperationException($"Ollama API returned no message: {json}");
            
            var content = messageObj["content"]?.ToString();
            var toolCall = messageObj["tool_call"];
            
            // If there's a tool call, return it in the same format as other providers
            if (toolCall != null)
            {
                var responseObj = new { content = "", tool_calls = new[] { toolCall } };
                return JsonConvert.SerializeObject(responseObj);
            }
            
            // Try to detect JSON in the response that looks like a tool call
            if (!string.IsNullOrEmpty(content) && content.TrimStart().StartsWith("{"))
            {
                try
                {
                    var potentialCall = JObject.Parse(content);
                    if (potentialCall["name"] != null || potentialCall["function"] != null)
                    {
                        var responseObj = new { content = "", tool_calls = new[] { potentialCall } };
                        return JsonConvert.SerializeObject(responseObj);
                    }
                }
                catch
                {
                    // Not valid JSON, return as content
                }
            }
            
            if (string.IsNullOrEmpty(content))
                throw new InvalidOperationException($"Ollama API returned no content: {json}");
            return content;
        }
        catch (JsonReaderException ex)
        {
            throw new InvalidOperationException($"Failed to parse Ollama response: {json}", ex);
        }
    }
    
    public override async Task GenerateStreamingAsync(List<Message> messages, Func<string, Task> onChunk, List<ToolDefinition>? tools = null, LLMConfig? config = null)
    {
        var effectiveConfig = config ?? DefaultConfig;
        var client = CreateClient(effectiveConfig);
        
        // Build tools into the prompt if provided (Ollama doesn't support tools natively)
        var toolsPrompt = "";
        if (tools != null && tools.Count > 0)
        {
            var toolDefs = string.Join("\n", tools.Select(t => $"- {t.Name}: {t.Description}\n  Parameters: {string.Join(", ", t.Parameters.Keys)}"));
            toolsPrompt = $"\n\nYou have access to the following tools:\n{toolDefs}\n\nIf you need to use a tool, respond with JSON in this format:\n{{\"name\": \"tool_name\", \"arguments\": {{\"param1\": \"value1\"}}}}\n";
        }
        
        // Add tools context to the last user message if tools are available
        var modifiedMessages = messages.Select(m =>
        {
            if (m.Role == MessageRole.User && !string.IsNullOrEmpty(toolsPrompt))
            {
                return new { role = m.Role.ToString().ToLower(), content = m.Content + toolsPrompt };
            }
            return new { role = m.Role.ToString().ToLower(), content = m.Content };
        }).ToList();
        
        var requestBody = new
        {
            model = effectiveConfig?.Model ?? _model,
            messages = modifiedMessages,
            stream = true,
            options = new
            {
                temperature = effectiveConfig?.Temperature ?? 0.7,
                num_predict = effectiveConfig?.MaxTokens ?? 4096
            }
        };
        
        var response = await client.PostAsJsonAsync($"{_baseUrl}/api/chat", requestBody);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Ollama streaming API request failed with status {response.StatusCode}: {errorContent}");
        }
        
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var contentBuilder = new System.Text.StringBuilder();
        
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (!string.IsNullOrWhiteSpace(line))
            {
                try
                {
                    var json = JObject.Parse(line);
                    var message = json["message"];
                    if (message == null) continue;
                    
                    // Check for content chunk
                    var chunk = message["content"]?.ToString();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        contentBuilder.Append(chunk);
                        await onChunk(chunk);
                    }
                    
                    // Check for tool call (Ollama may return this in different formats)
                    var toolCall = message["tool_call"];
                    if (toolCall != null)
                    {
                        var toolCallJson = new { tool_calls = new[] { toolCall } };
                        await onChunk(JsonConvert.SerializeObject(toolCallJson));
                    }
                }
                catch (JsonReaderException)
                {
                    // Malformed stream data, skip
                }
            }
        }
        
        // After stream completes, check if the full response looks like a tool call
        var fullContent = contentBuilder.ToString();
        if (!string.IsNullOrEmpty(fullContent) && fullContent.TrimStart().StartsWith("{"))
        {
            try
            {
                var potentialCall = JObject.Parse(fullContent);
                if (potentialCall["name"] != null || potentialCall["function"] != null)
                {
                    var toolCallJson = new { tool_calls = new[] { potentialCall } };
                    await onChunk(JsonConvert.SerializeObject(toolCallJson));
                }
            }
            catch
            {
                // Not valid JSON, ignore
            }
        }
    }
    
    /// <summary>
    /// List available models
    /// </summary>
    public async Task<List<string>> ListModelsAsync()
    {
        var client = CreateClient();
        var response = await client.GetAsync($"{_baseUrl}/api/tags");
        var json = await response.Content.ReadAsStringAsync();
        
        var result = JObject.Parse(json);
        return result["models"]?.Select(m => m["name"]?.ToString() ?? "").ToList() ?? new List<string>();
    }
}

/// <summary>
/// LLM Factory
/// </summary>
public class LLMFactory
{
    public static ILLMProvider CreateOpenAI(string apiKey, string? baseUrl = null, TimeSpan? timeout = null, Dictionary<string, string>? customHeaders = null)
        => new OpenAIProvider(apiKey, baseUrl, timeout, customHeaders);
    
    public static ILLMProvider CreateAnthropic(string apiKey, string? baseUrl = null, TimeSpan? timeout = null, Dictionary<string, string>? customHeaders = null)
        => new AnthropicProvider(apiKey, baseUrl, timeout, customHeaders);
    
    public static ILLMProvider CreateOllama(string? baseUrl = null, string model = "llama2", TimeSpan? timeout = null, Dictionary<string, string>? customHeaders = null)
        => new OllamaProvider(baseUrl, model, timeout, customHeaders);
    
    public static ILLMProvider CreateFromEnvironment()
    {
        var openaiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(openaiKey))
            return CreateOpenAI(openaiKey);
        
        var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrEmpty(anthropicKey))
            return CreateAnthropic(anthropicKey);
        
        return CreateOllama();
    }
    
    public static ILLMProvider CreateFromConfiguration(IConfiguration configuration)
    {
        var providerType = configuration["LLM:Provider"]?.ToLowerInvariant();
        
        TimeSpan? timeout = null;
        if (int.TryParse(configuration["LLM:TimeoutSeconds"], out int timeoutSeconds))
        {
            timeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        var customHeaders = new Dictionary<string, string>();
        var headersSection = configuration.GetSection("LLM:Headers");
        foreach (var child in headersSection.GetChildren())
        {
            if (!string.IsNullOrEmpty(child.Key) && !string.IsNullOrEmpty(child.Value))
            {
                customHeaders[child.Key] = child.Value;
            }
        }
        
        var defaultConfig = new LLMConfig
        {
            Model = configuration["LLM:Model"],
            BaseUrl = configuration["LLM:BaseUrl"],
            Temperature = double.TryParse(configuration["LLM:Temperature"], out double temp) ? temp : 0.7,
            MaxTokens = int.TryParse(configuration["LLM:MaxTokens"], out int tokens) ? tokens : 4096,
            Stop = configuration.GetSection("LLM:Stop").Get<List<string>>()
        };

        ILLMProvider provider;
        switch (providerType)
        {
            case "openai":
                {
                    var apiKey = configuration["LLM:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                    var baseUrl = configuration["LLM:BaseUrl"];
                    if (string.IsNullOrEmpty(apiKey))
                        throw new InvalidOperationException("OpenAI Provider requires an API key in configuration or OPENAI_API_KEY environment variable.");
                    //if (string.IsNullOrEmpty(baseUrl))
                    //   throw new InvalidOperationException("OpenAI Provider requires a BaseUrl in configuration under LLM:BaseUrl.");
                    
                    defaultConfig.Headers["Authorization"] = $"Bearer {apiKey}";
                    provider = CreateOpenAI(apiKey, baseUrl, timeout, customHeaders);
                    break;
                }
            case "anthropic":
                {
                    var apiKey = configuration["LLM:ApiKey"] ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                    var baseUrl = configuration["LLM:BaseUrl"];
                    if (string.IsNullOrEmpty(apiKey))
                        throw new InvalidOperationException("Anthropic Provider requires an API key in configuration or ANTHROPIC_API_KEY environment variable.");
                    //if (string.IsNullOrEmpty(baseUrl))
                    //    throw new InvalidOperationException("Anthropic Provider requires a BaseUrl in configuration under LLM:BaseUrl.");
                    
                    defaultConfig.Headers["x-api-key"] = apiKey;
                    defaultConfig.Headers["anthropic-version"] = "2023-06-01";
                    provider = CreateAnthropic(apiKey, baseUrl, timeout, customHeaders);
                    break;
                }
            case "ollama":
            default:
                {
                    var baseUrl = configuration["LLM:BaseUrl"];
                    if (string.IsNullOrEmpty(baseUrl))
                        baseUrl = "http://localhost:11434"; // Ollama default only
                    
                    var model = configuration["LLM:Model"] ?? "llama2";
                    provider = CreateOllama(baseUrl, model, timeout, customHeaders);
                    break;
                }
        }

        provider.DefaultConfig = defaultConfig;
        return provider;
    }
}
