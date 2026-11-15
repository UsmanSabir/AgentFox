using AgentFox.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentFox.LLM;

/// <summary>Switches the main agent's model for subsequent calls without rebuilding the agent.</summary>
public sealed class RuntimeModelChatClient : IChatClient
{
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider? _services;
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

    public RuntimeModelChatClient(IConfiguration configuration, IServiceProvider? services = null)
    {
        _configuration = configuration;
        _services = services;
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
        var result = TrySetModelAsync(requested).GetAwaiter().GetResult();
        error = result.Error;
        return result.Success;
    }

    public async Task<(bool Success, string? Error)> TrySetModelAsync(string? requested,
        CancellationToken cancellationToken = default)
    {
        if (!CanSwitch)
            return (false, "Configure an API key before changing the model at runtime.");

        var model = requested?.Trim();
        if (string.IsNullOrEmpty(model) || model.Length > 200 || requested!.Any(char.IsControl))
            return (false, "Enter a model ID of 1 to 200 characters without control characters.");

        var selection = await GetSelectionAsync(cancellationToken);
        if (selection.AllowedModels is { } allowed && !allowed.Contains(model, StringComparer.Ordinal))
            return (false, selection.Reason ?? "This model is not allowed for this account.");

        return SetModelCore(model);
    }

    /// <summary>Refreshes policy and restores the configured model if the active selection is no longer allowed.</summary>
    public async Task<ModelSwitchSelection> GetSelectionAsync(CancellationToken cancellationToken = default)
    {
        // Nothing to enforce when the model cannot leave the configured one (no API key). Consulting the policy anyway would cost
        // a hub read a minute, on every model call, for an agent that can never switch.
        if (!CanSwitch) return new ModelSwitchSelection(null);
        ModelSwitchSelection selection;
        try
        {
            // Resolved on use so a premium policy never creates a constructor cycle through the hub client.
            selection = await (_services?.GetService<IModelSwitchPolicy>() ?? new AllowAnyModelSwitchPolicy())
                .ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { selection = new ModelSwitchSelection([], "Model access could not be checked. Only the configured model is available."); }
        if (selection.AllowedModels is null) return selection;
        var allowed = selection.AllowedModels.Append(ConfiguredModel).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (!allowed.Contains(Model, StringComparer.Ordinal)) SetModelCore(ConfiguredModel);
        return selection with { AllowedModels = allowed };
    }

    private (bool Success, string? Error) SetModelCore(string model)
    {

        lock (_gate)
        {
            if (model == _current.Model) return (true, null);
        }

        IChatClient next;
        try
        {
            // Same construction as the startup client (provider defaults, headers, timeout, OpenRouter user, Azure client);
            // only the model ID differs. A literal model ID only: named Models entries may carry another provider or key.
            next = LLMFactory.CreateFromConfiguration(_configuration, model);
        }
        catch (Exception)
        {
            return (false, "The model client could not be created. Check the model ID and provider settings.");
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
        return (true, null);
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

    private async Task<ClientState> AcquireAsync(CancellationToken cancellationToken)
    {
        await GetSelectionAsync(cancellationToken);
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
        var state = await AcquireAsync(cancellationToken);
        try { return await state.Client.GetResponseAsync(messages, options, cancellationToken); }
        finally { Release(state); }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = await AcquireAsync(cancellationToken);
        try
        {
            await foreach (var update in state.Client.GetStreamingResponseAsync(messages, options, cancellationToken))
                yield return update;
        }
        finally { Release(state); }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        // Hand out the wrapper itself, never an inner client that switching would retire and dispose.
        if (serviceKey is null && serviceType.IsInstanceOfType(this)) return this;
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
