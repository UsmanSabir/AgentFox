using AgentFox.Agents;
using Anthropic;
using Google.GenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OllamaSharp;
using OpenAI;
using ChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;
using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.OpenAI;

namespace AgentFox.LLM;

/// <summary>
/// LLM Factory
/// </summary>
public class LLMFactory
{
    /// <summary>Builds the configured chat client. <paramref name="modelOverride"/> swaps only the model ID, so a runtime
    /// switch is built by exactly the code the startup client uses and the two cannot diverge.</summary>
    public static IChatClient CreateFromConfiguration(IConfiguration configuration, string? modelOverride = null)
    {
        var configuredModel = modelOverride ?? configuration["LLM:Model"];
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

        //https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/02-agents/AgentProviders
        IChatClient provider;
        switch (providerType)
        {
            case "openai":
                {
                    var apiKey = configuration["LLM:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                    var baseUrl = configuration["LLM:BaseUrl"] ?? "https://api.openai.com/v1";
                    var model = configuredModel;

                    var keyCredential = new ApiKeyCredential(apiKey);
                    var openAiClient = new OpenAIClient(keyCredential, OpenAiOptions(baseUrl, timeout, customHeaders));
                    var chatClient = openAiClient.GetChatClient(model);

                    provider = chatClient.AsIChatClient();
                    //provider = CreateOpenAI(apiKey, baseUrl, timeout, customHeaders);
                    break;
                }
            case "openrouter":
                {
                    var apiKey = configuration["LLM:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
                    var baseUrl = configuration["LLM:BaseUrl"] ?? "https://openrouter.ai/api/v1";
                    var model = configuredModel;

                    var keyCredential = new ApiKeyCredential(apiKey);
                    var openAiClient = new OpenAIClient(keyCredential, OpenAiOptions(baseUrl, timeout, customHeaders));
                    var chatClient = openAiClient.GetChatClient(model);

                    provider = WithOpenRouterUser(chatClient.AsIChatClient(), configuration["LLM:User"]);
                    //provider = CreateOpenAI(apiKey, baseUrl, timeout, customHeaders);
                    break;
                }
            case "anthropic":
                {
                    var apiKey = configuration["LLM:ApiKey"] ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                    var baseUrl = configuration["LLM:BaseUrl"];
                    var model = configuredModel;
                    if (string.IsNullOrEmpty(apiKey))
                        throw new InvalidOperationException("Anthropic Provider requires an API key in configuration or ANTHROPIC_API_KEY environment variable.");

                    AnthropicClient client;
                    if (!string.IsNullOrWhiteSpace(baseUrl))
                        client = new() { ApiKey = apiKey, Timeout = timeout, BaseUrl = baseUrl };
                    else
                        client = new() { ApiKey = apiKey, Timeout = timeout };

                    provider = client.AsIChatClient(model);
                    break;
                }
            case "azureopenai":
                {
                    var apiKey = configuration["LLM:ApiKey"] ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
                    var baseUrl = configuration["LLM:BaseUrl"] ?? throw new InvalidOperationException("Azure OpenAI Provider requires BaseUrl in configuration.");
                    var model = configuredModel ?? throw new InvalidOperationException("Azure OpenAI Provider requires Model name in configuration.");

                    //var keyCredential = new ApiKeyCredential(apiKey);
                    //var openAiClient = new OpenAIClient(keyCredential, new OpenAIClientOptions() { Endpoint = new Uri(baseUrl), NetworkTimeout = timeout });
                    //var chatClient = openAiClient.GetChatClient(model);
                    //provider = chatClient.AsIChatClient();

                    var azureClient = new AzureOpenAIClient(
                        new Uri(baseUrl),
                        new ApiKeyCredential(apiKey));

                    var chatClient = azureClient.GetChatClient(model);
                    provider = chatClient.AsIChatClient();
                    break;
                }
            case "googlevertexai":
                {
                    var apiKey = configuration["LLM:ApiKey"] ?? Environment.GetEnvironmentVariable("GOOGLE_VERTEX_AI_API_KEY");
                    var baseUrl = configuration["LLM:BaseUrl"] ?? throw new InvalidOperationException("Google Vertex AI Provider requires BaseUrl in configuration.");
                    var model = configuredModel ?? throw new InvalidOperationException("Google Vertex AI Provider requires Model name in configuration.");

                    var client = new Client(vertexAI: true, apiKey: apiKey);

                    provider = client.AsIChatClient(model);
                    break;
                }
            case "google_genai":
                {
                    var apiKey = configuration["LLM:ApiKey"] ?? Environment.GetEnvironmentVariable("GOOGLE_GENAI_API_KEY");
                    var baseUrl = configuration["LLM:BaseUrl"] ?? throw new InvalidOperationException("Google GenAI Provider requires BaseUrl in configuration.");
                    var model = configuredModel ?? throw new InvalidOperationException("Google GenAI Provider requires Model name in configuration.");

                    var client = new Client(vertexAI: false, apiKey: apiKey);

                    provider = client.AsIChatClient(model);
                    break;
                }
            case "ollama":
            default:
                {
                    var baseUrl = configuration["LLM:BaseUrl"] ?? "http://localhost:11434";
                    if (string.IsNullOrEmpty(baseUrl))
                        baseUrl = "http://localhost:11434"; // Ollama default only

                    var model = configuredModel ?? "qwen3.5:4b";
                    //var apiKey = configuration["LLM:ApiKey"] ?? Environment.GetEnvironmentVariable("OLLAMA_API_KEY");

                    if (string.IsNullOrEmpty(model))
                        throw new InvalidOperationException("Ollama Provider requires Model name.");

                    var ollamaApiClient = new OllamaApiClient(new OllamaApiClient.Configuration()
                    {
                        Model = model,
                        Uri = new Uri(baseUrl)
                    });
                    provider = ollamaApiClient;
                    break;
                }
        }

        return provider;
    }

    /// <summary>
    /// Creates a chat client with a model override.
    /// First looks for a named config under <c>Models:{modelNameOrKey}</c> (e.g. "CheapModel",
    /// "FastModel", "ReasoningModel"); falls back to the default provider with
    /// <paramref name="modelNameOrKey"/> used as the literal model name.
    /// Returns null if the client cannot be created.
    /// </summary>
    public static IChatClient? CreateWithModelOverride(IConfiguration configuration, string modelNameOrKey)
    {
        // 1. Named model config lookup
        var modelConfig = configuration.GetSection($"Models:{modelNameOrKey}").Get<ModelConfig>();
        if (modelConfig != null && !string.IsNullOrEmpty(modelConfig.Model))
        {
            modelConfig.User ??= configuration["LLM:User"];
            return CreateChatClient(modelConfig);
        }

        // 2. Fallback: reuse default provider settings, swap model name
        var fallback = new ModelConfig
        {
            Provider = configuration["LLM:Provider"] ?? "ollama",
            Model = modelNameOrKey,
            BaseUrl = configuration["LLM:BaseUrl"] ?? "http://localhost:11434",
            ApiKey = configuration["LLM:ApiKey"],
            User = configuration["LLM:User"],
            Headers = configuration.GetSection("LLM:Headers").Get<Dictionary<string, string>>() ?? new(),
            TimeoutSeconds = int.TryParse(configuration["LLM:TimeoutSeconds"], out var t) ? t : 3600
        };

        return CreateChatClient(fallback);
    }

    // Headers ride on a pipeline policy, not a replacement transport: a caller-built HttpClient brings its own
    // 100-second Timeout, which would silently cap NetworkTimeout, and the SDK's shared transport is kept.
    internal static OpenAIClientOptions OpenAiOptions(string baseUrl, TimeSpan? timeout,
        IReadOnlyDictionary<string, string> headers)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl), NetworkTimeout = timeout };
        if (headers.Count > 0)
            options.AddPolicy(new CustomHeaderPolicy(headers), PipelinePosition.PerCall);
        return options;
    }

    internal sealed class CustomHeaderPolicy : PipelinePolicy
    {
        private readonly KeyValuePair<string, string>[] _headers;

        public CustomHeaderPolicy(IReadOnlyDictionary<string, string> headers)
        {
            // Checked once, when the client is built: a bad name fails configuration, not every request.
            foreach (var name in headers.Keys)
            {
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    name.Length == 0 || name.Any(c => c <= ' ' || c >= 0x7f || c == ':'))
                    throw new InvalidOperationException($"Unsupported LLM header: {name}");
            }
            if (headers.Values.Any(v => v.Contains('\r') || v.Contains('\n')))
                throw new InvalidOperationException("LLM header values must be a single line.");
            _headers = headers.ToArray();
        }

        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            return ProcessNextAsync(message, pipeline, currentIndex);
        }

        internal void Apply(PipelineMessage message)
        {
            foreach (var (name, value) in _headers) message.Request.Headers.Set(name, value);
        }
    }

    private static IChatClient WithOpenRouterUser(IChatClient client, string? user)
    {
        // Some callers build a ModelConfig directly (for example, summarization), bypassing the LLM section.
        user ??= Environment.GetEnvironmentVariable("LLM__User");
        return string.IsNullOrWhiteSpace(user) ? client : new OpenRouterUserChatClient(client, user);
    }

    private sealed class OpenRouterUserChatClient(IChatClient inner, string user) : IChatClient
    {
        private ChatOptions AddUser(ChatOptions? options)
        {
            var copy = options?.Clone() ?? new ChatOptions();
            var originalFactory = copy.RawRepresentationFactory;
            copy.RawRepresentationFactory = client =>
            {
                var native = originalFactory?.Invoke(client) as ChatCompletionOptions ?? new ChatCompletionOptions();
                native.EndUserId = user; // Serialized as request-body "user" by the OpenAI-compatible chat API.
                return native;
            };
            return copy;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => inner.GetResponseAsync(messages, AddUser(options), cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => inner.GetStreamingResponseAsync(messages, AddUser(options), cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null)
            => inner.GetService(serviceType, serviceKey);

        public void Dispose() => inner.Dispose();
    }

    public static IChatClient? CreateChatClient(ModelConfig modelConfig)
    {
        var providerType = modelConfig.Provider?.ToLowerInvariant();

        TimeSpan? timeout = null;
        if (modelConfig.TimeoutSeconds > 0)
        {
            timeout = TimeSpan.FromSeconds(modelConfig.TimeoutSeconds);
        }

        //https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/02-agents/AgentProviders
        IChatClient? provider = null;
        switch (providerType)
        {
            case "openai":
                {
                    var apiKey = modelConfig.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                    var baseUrl = modelConfig.BaseUrl ?? "https://api.openai.com/v1";
                    var model = modelConfig.Model;

                    var keyCredential = new ApiKeyCredential(apiKey);
                    var openAiClient = new OpenAIClient(keyCredential, OpenAiOptions(baseUrl, timeout, modelConfig.Headers));
                    var chatClient = openAiClient.GetChatClient(model);
                    provider = chatClient.AsIChatClient();

                    break;
                }
            case "openrouter":
                {
                    var apiKey = modelConfig.ApiKey ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
                    var baseUrl = modelConfig.BaseUrl ?? "https://openrouter.ai/api/v1";
                    if (string.IsNullOrWhiteSpace(baseUrl))
                        baseUrl = "https://openrouter.ai/api/v1";

                    var model = modelConfig.Model;

                    var keyCredential = new ApiKeyCredential(apiKey);
                    var openAiClient = new OpenAIClient(keyCredential, OpenAiOptions(baseUrl, timeout, modelConfig.Headers));
                    var chatClient = openAiClient.GetChatClient(model);
                    provider = WithOpenRouterUser(chatClient.AsIChatClient(), modelConfig.User);

                    break;
                }
            case "anthropic":
                {
                    var apiKey = modelConfig.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                    var baseUrl = modelConfig.BaseUrl;
                    var model = modelConfig.Model;
                    if (string.IsNullOrEmpty(apiKey))
                        throw new InvalidOperationException("Anthropic Provider requires an API key in configuration or ANTHROPIC_API_KEY environment variable.");

                    AnthropicClient client;
                    if (!string.IsNullOrWhiteSpace(baseUrl))
                        client = new() { ApiKey = apiKey, Timeout = timeout, BaseUrl = baseUrl };
                    else
                        client = new() { ApiKey = apiKey, Timeout = timeout };

                    provider = client.AsIChatClient(model);
                    break;
                }
            case "azureopenai":
                {
                    var apiKey = modelConfig.ApiKey ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
                    var baseUrl = modelConfig.BaseUrl ?? throw new InvalidOperationException("Azure OpenAI Provider requires BaseUrl in configuration.");
                    var model = modelConfig.Model ?? throw new InvalidOperationException("Azure OpenAI Provider requires Model name in configuration.");

                    var keyCredential = new ApiKeyCredential(apiKey);
                    var openAiClient = new OpenAIClient(keyCredential, new OpenAIClientOptions() { Endpoint = new Uri(baseUrl), NetworkTimeout = timeout });
                    var chatClient = openAiClient.GetChatClient(model);
                    provider = chatClient.AsIChatClient();
                    break;
                }
            case "googlevertexai":
                {
                    var apiKey = modelConfig.ApiKey ?? Environment.GetEnvironmentVariable("GOOGLE_VERTEX_AI_API_KEY");
                    //var baseUrl = modelConfig.ApiKey ?? throw new InvalidOperationException("Google Vertex AI Provider requires BaseUrl in configuration.");
                    var model = modelConfig.Model ?? throw new InvalidOperationException("Google Vertex AI Provider requires Model name in configuration.");
                    var client = new Client(vertexAI: true, apiKey: apiKey);
                    provider = client.AsIChatClient(model);

                    break;
                }
            case "google_genai":
                {
                    var apiKey = modelConfig.ApiKey ?? Environment.GetEnvironmentVariable("GOOGLE_GENAI_API_KEY");
                    var baseUrl = modelConfig.BaseUrl ?? throw new InvalidOperationException("Google GenAI Provider requires BaseUrl in configuration.");
                    var model = modelConfig.Model ?? throw new InvalidOperationException("Google GenAI Provider requires Model name in configuration.");

                    var client = new Client(vertexAI: false, apiKey: apiKey);

                    provider = client.AsIChatClient(model);
                    break;
                }
            case "ollama":
                {
                    var baseUrl = modelConfig.BaseUrl ?? "http://localhost:11434";
                    if (string.IsNullOrEmpty(baseUrl))
                        baseUrl = "http://localhost:11434"; // Ollama default only

                    var model = modelConfig.Model ?? "qwen3.5:4b";
                    //var apiKey = configuration["LLM:ApiKey"] ?? Environment.GetEnvironmentVariable("OLLAMA_API_KEY");

                    if (string.IsNullOrEmpty(model))
                        throw new InvalidOperationException("Ollama Provider requires Model name.");

                    var ollamaApiClient = new OllamaApiClient(new OllamaApiClient.Configuration()
                    {
                        Model = model,
                        Uri = new Uri(baseUrl)
                    });
                    provider = ollamaApiClient;
                    break;
                }
        }

        return provider;
    }
}
