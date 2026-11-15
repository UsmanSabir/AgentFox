using AgentFox.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace AgentFox.LLM;

/// <summary>Switches the main agent's model for subsequent calls without rebuilding the agent.</summary>
public sealed class RuntimeModelChatClient : IChatClient
{
    private readonly IConfiguration _configuration;
    private readonly object _gate = new();
    private ClientState _current;
    private bool _disposed;

    private sealed class ClientState(IChatClient client, string model)
    {
        public IChatClient Client { get; } = client;
        public string Model { get; } = model;
        public int ActiveCalls;
        public bool Retired;
    }

    public RuntimeModelChatClient(IConfiguration configuration)
    {
        _configuration = configuration;
        _current = new ClientState(LLMFactory.CreateFromConfiguration(configuration),
            configuration["LLM:Model"] ?? string.Empty);
    }

    public string Provider => _configuration["LLM:Provider"] ?? "ollama";
    public string ConfiguredModel => _configuration["LLM:Model"] ?? string.Empty;
    public bool CanSwitch => HasConfiguredApiKey(_configuration);

    public string Model
    {
        get { lock (_gate) return _current.Model; }
    }

    public bool TrySetModel(string? requested, out string? error)
    {
        error = null;
        if (!CanSwitch)
        {
            error = "Configure an API key before changing the model at runtime.";
            return false;
        }

        var model = requested?.Trim();
        if (string.IsNullOrEmpty(model) || model.Length > 200 || requested!.Any(char.IsControl))
        {
            error = "Enter a model ID of 1 to 200 characters without control characters.";
            return false;
        }

        lock (_gate)
        {
            if (model == _current.Model) return true;
        }

        IChatClient next;
        try
        {
            // A literal model ID only. Named Models entries may carry a different provider or key.
            next = LLMFactory.CreateChatClient(new ModelConfig
            {
                Provider = Provider,
                Model = model,
                BaseUrl = _configuration["LLM:BaseUrl"] ?? (Provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase)
                    ? "https://openrouter.ai/api/v1" : "https://api.openai.com/v1"),
                ApiKey = _configuration["LLM:ApiKey"],
                User = _configuration["LLM:User"],
                Headers = _configuration.GetSection("LLM:Headers").Get<Dictionary<string, string>>() ?? new(),
                TimeoutSeconds = int.TryParse(_configuration["LLM:TimeoutSeconds"], out var timeout) ? timeout : 0
            }) ?? throw new InvalidOperationException("The model client could not be created.");
        }
        catch (Exception)
        {
            error = "The model client could not be created. Check the model ID and provider settings.";
            return false;
        }

        IChatClient? retired = null;
        lock (_gate)
        {
            if (_disposed) { next.Dispose(); throw new ObjectDisposedException(nameof(RuntimeModelChatClient)); }
            var old = _current;
            _current = new ClientState(next, model);
            old.Retired = true;
            if (old.ActiveCalls == 0) retired = old.Client;
        }
        retired?.Dispose();
        return true;
    }

    private static bool HasConfiguredApiKey(IConfiguration config)
    {
        var provider = config["LLM:Provider"]?.ToLowerInvariant();
        var env = provider switch
        {
            "openai" => "OPENAI_API_KEY",
            "openrouter" => "OPENROUTER_API_KEY",
            "anthropic" => "ANTHROPIC_API_KEY",
            "azureopenai" => "AZURE_OPENAI_API_KEY",
            "googlevertexai" => "GOOGLE_VERTEX_AI_API_KEY",
            "google_genai" => "GOOGLE_GENAI_API_KEY",
            _ => null
        };
        return env != null && (!string.IsNullOrWhiteSpace(config["LLM:ApiKey"])
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(env)));
    }

    private ClientState Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _current.ActiveCalls++;
            return _current;
        }
    }

    private void Release(ClientState state)
    {
        IChatClient? retired = null;
        lock (_gate)
        {
            state.ActiveCalls--;
            if (state.ActiveCalls == 0 && state.Retired) retired = state.Client;
        }
        retired?.Dispose();
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var state = Acquire();
        try { return await state.Client.GetResponseAsync(messages, options, cancellationToken); }
        finally { Release(state); }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = Acquire();
        try
        {
            await foreach (var update in state.Client.GetStreamingResponseAsync(messages, options, cancellationToken))
                yield return update;
        }
        finally { Release(state); }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        lock (_gate) return _current.Client.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        IChatClient? retired = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _current.Retired = true;
            if (_current.ActiveCalls == 0) retired = _current.Client;
        }
        retired?.Dispose();
    }
}
