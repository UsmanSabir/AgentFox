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
    Task<string> GenerateAsync(List<Message> messages, List<ToolDefinition>? tools = null, LLMConfig? config = null);
    Task GenerateStreamingAsync(List<Message> messages, Func<string, Task> onChunk, List<ToolDefinition>? tools = null, LLMConfig? config = null);
}

/// <summary>
/// LLM Configuration
/// </summary>
public class LLMConfig
{
    public string? Model { get; set; }
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 4096;
    public List<string>? Stop { get; set; }
    public double? TopP { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
}

/// <summary>
/// OpenAI LLM Provider
/// </summary>
public class OpenAIProvider : ILLMProvider
{
    private readonly string _apiKey;
    private readonly string _baseUrl;
    
    public string Name => "OpenAI";
    
    public OpenAIProvider(string apiKey, string? baseUrl = null)
    {
        _apiKey = apiKey;
        _baseUrl = baseUrl ?? "https://api.openai.com/v1";
    }
    
    public async Task<string> GenerateAsync(List<Message> messages, List<ToolDefinition>? tools = null, LLMConfig? config = null)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        
        var requestBody = new
        {
            model = config?.Model ?? "gpt-4",
            messages = messages.Select(m => new { role = m.Role.ToString().ToLower(), m.Content }),
            temperature = config?.Temperature ?? 0.7,
            max_tokens = config?.MaxTokens ?? 4096,
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
        var json = await response.Content.ReadAsStringAsync();
        
        var result = JObject.Parse(json);
        return result["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
    }
    
    public async Task GenerateStreamingAsync(List<Message> messages, Func<string, Task> onChunk, List<ToolDefinition>? tools = null, LLMConfig? config = null)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        
        var requestBody = new
        {
            model = config?.Model ?? "gpt-4",
            messages = messages.Select(m => new { role = m.Role.ToString().ToLower(), m.Content }),
            temperature = config?.Temperature ?? 0.7,
            max_tokens = config?.MaxTokens ?? 4096,
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
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line?.StartsWith("data: ") == true && line != "data: [DONE]")
            {
                var data = line[6..];
                try
                {
                    var json = JObject.Parse(data);
                    var chunk = json["choices"]?[0]?["delta"]?["content"]?.ToString();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        await onChunk(chunk);
                    }
                }
                catch { }
            }
        }
    }
}

/// <summary>
/// Anthropic Claude LLM Provider
/// </summary>
public class AnthropicProvider : ILLMProvider
{
    private readonly string _apiKey;
    private readonly string _baseUrl;
    
    public string Name => "Anthropic Claude";
    
    public AnthropicProvider(string apiKey, string? baseUrl = null)
    {
        _apiKey = apiKey;
        _baseUrl = baseUrl ?? "https://api.anthropic.com/v1";
    }
    
    public async Task<string> GenerateAsync(List<Message> messages, List<ToolDefinition>? tools = null, LLMConfig? config = null)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        
        var systemMessage = messages.FirstOrDefault(m => m.Role == MessageRole.System)?.Content ?? "";
        var userMessages = messages.Where(m => m.Role != MessageRole.System).ToList();
        
        var requestBody = new
        {
            model = config?.Model ?? "claude-3-opus-20240229",
            max_tokens = config?.MaxTokens ?? 4096,
            temperature = config?.Temperature ?? 0.7,
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
        var json = await response.Content.ReadAsStringAsync();
        
        var result = JObject.Parse(json);
        return result["content"]?[0]?["text"]?.ToString() ?? "";
    }
    
    public async Task GenerateStreamingAsync(List<Message> messages, Func<string, Task> onChunk, List<ToolDefinition>? tools = null, LLMConfig? config = null)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        
        var systemMessage = messages.FirstOrDefault(m => m.Role == MessageRole.System)?.Content ?? "";
        var userMessages = messages.Where(m => m.Role != MessageRole.System).ToList();
        
        var requestBody = new
        {
            model = config?.Model ?? "claude-3-opus-20240229",
            max_tokens = 1024,
            temperature = config?.Temperature ?? 0.7,
            system = systemMessage,
            messages = userMessages.Select(m => new { role = m.Role.ToString().ToLower(), content = m.Content }),
            stream = true
        };
        
        var response = await client.PostAsJsonAsync($"{_baseUrl}/messages", requestBody);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line?.StartsWith("data: ") == true && line != "data: [DONE]")
            {
                var data = line[6..];
                try
                {
                    var json = JObject.Parse(data);
                    var chunk = json["delta"]?["text"]?.ToString();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        await onChunk(chunk);
                    }
                }
                catch { }
            }
        }
    }
}

/// <summary>
/// Ollama LLM Provider (local models)
/// </summary>
public class OllamaProvider : ILLMProvider
{
    private readonly string _baseUrl;
    private readonly string _model;
    
    public string Name => "Ollama";
    
    public OllamaProvider(string? baseUrl = null, string model = "llama2")
    {
        _baseUrl = baseUrl ?? "http://localhost:11434";
        _model = model;
    }
    
    public async Task<string> GenerateAsync(List<Message> messages, List<ToolDefinition>? tools = null, LLMConfig? config = null)
    {
        var client = new HttpClient();
        
        var requestBody = new
        {
            model = config?.Model ?? _model,
            messages = messages.Select(m => new { role = m.Role.ToString().ToLower(), content = m.Content }),
            stream = false,
            options = new
            {
                temperature = config?.Temperature ?? 0.7,
                num_predict = config?.MaxTokens ?? 4096
            }
        };
        
        var response = await client.PostAsJsonAsync($"{_baseUrl}/api/chat", requestBody);
        var json = await response.Content.ReadAsStringAsync();
        
        var result = JObject.Parse(json);
        return result["message"]?["content"]?.ToString() ?? "";
    }
    
    public async Task GenerateStreamingAsync(List<Message> messages, Func<string, Task> onChunk, List<ToolDefinition>? tools = null, LLMConfig? config = null)
    {
        var client = new HttpClient();
        
        var requestBody = new
        {
            model = config?.Model ?? _model,
            messages = messages.Select(m => new { role = m.Role.ToString().ToLower(), content = m.Content }),
            stream = true,
            options = new
            {
                temperature = config?.Temperature ?? 0.7,
                num_predict = config?.MaxTokens ?? 4096
            }
        };
        
        var response = await client.PostAsJsonAsync($"{_baseUrl}/api/chat", requestBody);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (!string.IsNullOrWhiteSpace(line))
            {
                try
                {
                    var json = JObject.Parse(line);
                    var chunk = json["message"]?["content"]?.ToString();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        await onChunk(chunk);
                    }
                }
                catch { }
            }
        }
    }
    
    /// <summary>
    /// List available models
    /// </summary>
    public async Task<List<string>> ListModelsAsync()
    {
        var client = new HttpClient();
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
    public static ILLMProvider CreateOpenAI(string apiKey, string? baseUrl = null)
        => new OpenAIProvider(apiKey, baseUrl);
    
    public static ILLMProvider CreateAnthropic(string apiKey, string? baseUrl = null)
        => new AnthropicProvider(apiKey, baseUrl);
    
    public static ILLMProvider CreateOllama(string? baseUrl = null, string model = "llama2")
        => new OllamaProvider(baseUrl, model);
    
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
        
        switch (providerType)
        {
            case "openai":
                {
                    var apiKey = configuration["LLM:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                    var baseUrl = configuration["LLM:BaseUrl"];
                    if (string.IsNullOrEmpty(apiKey))
                        throw new InvalidOperationException("OpenAI Provider requires an API key in configuration or OPENAI_API_KEY environment variable.");
                    return CreateOpenAI(apiKey, baseUrl);
                }
            case "anthropic":
                {
                    var apiKey = configuration["LLM:ApiKey"] ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                    var baseUrl = configuration["LLM:BaseUrl"];
                    if (string.IsNullOrEmpty(apiKey))
                        throw new InvalidOperationException("Anthropic Provider requires an API key in configuration or ANTHROPIC_API_KEY environment variable.");
                    return CreateAnthropic(apiKey, baseUrl);
                }
            case "ollama":
            default:
                {
                    var baseUrl = configuration["LLM:BaseUrl"];
                    var model = configuration["LLM:Model"] ?? "llama2";
                    return CreateOllama(baseUrl, model);
                }
        }
    }
}
