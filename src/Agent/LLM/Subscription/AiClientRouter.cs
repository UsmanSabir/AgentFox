using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentFox.LLM.Subscription;

/// <summary>
/// Sends each purpose to the provider the owner chose for it (<see cref="AiRoutingStore"/>) when that provider is
/// signed in and this agent is allowed to use it; otherwise to the configured provider. Decided on every call, so the
/// settings card takes effect at once, and a provider that stops being available simply stops being used.
/// </summary>
public sealed class AiClientRouter : IAiClientRouter
{
    private readonly RuntimeModelChatClient _configured;
    private readonly IReadOnlyDictionary<string, ISubscriptionProvider> _providers;
    private readonly AiRoutingStore _routing;
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new(StringComparer.Ordinal);

    public AiClientRouter(RuntimeModelChatClient configured, IEnumerable<ISubscriptionProvider> providers,
        AiRoutingStore routing, IServiceProvider services)
    {
        _configured = configured;
        _providers = providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _routing = routing;
        _services = services;
    }

    public IChatClient For(string purpose) => _clients.GetOrAdd(purpose, p => new PurposeChatClient(this, p));

    public IReadOnlyCollection<ISubscriptionProvider> Providers => _providers.Values.ToList();

    /// <summary>The provider this purpose actually uses right now (<see cref="AiProviders"/>).</summary>
    public async Task<string> ProviderInUseAsync(string purpose, CancellationToken cancellationToken)
    {
        var chosen = _routing.ProviderFor(purpose);
        if (!_providers.TryGetValue(chosen, out var provider) || !provider.IsSignedIn) return AiProviders.Configured;
        return (await ReadAccessAsync(chosen, cancellationToken)).Allowed ? chosen : AiProviders.Configured;
    }

    /// <summary>Whether this purpose goes to a subscription provider (text only) right now.</summary>
    public async Task<bool> UsesSubscriptionAsync(string purpose, CancellationToken cancellationToken) =>
        await ProviderInUseAsync(purpose, cancellationToken) != AiProviders.Configured;

    /// <summary>Resolved on use: a hosted policy reads the hub, and a constructor edge to it can close a DI cycle.</summary>
    public async Task<SubscriptionAccess> ReadAccessAsync(string provider, CancellationToken cancellationToken)
    {
        var policy = _services.GetService<ISubscriptionAccessPolicy>() ?? new AllowSubscriptionAccessPolicy();
        try
        {
            return await policy.ReadAsync(provider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // Unreadable means not allowed: the configured provider keeps working, so this costs nothing but the
            // routing, where guessing "allowed" would spend a process on an account the operator switched off.
            return new SubscriptionAccess(false, "Access could not be checked.");
        }
    }

    private async Task<IChatClient> ResolveAsync(string purpose, CancellationToken cancellationToken)
    {
        var id = await ProviderInUseAsync(purpose, cancellationToken);
        return _providers.TryGetValue(id, out var provider) ? provider.Client : _configured;
    }

    private sealed class PurposeChatClient(AiClientRouter router, string purpose) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var client = await router.ResolveAsync(purpose, cancellationToken);
            return await client.GetResponseAsync(messages, options, cancellationToken);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var client = await router.ResolveAsync(purpose, cancellationToken);
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
                yield return update;
        }

        // Callers probing for the configured client (model switching, attachment support) still find it. The model's
        // metadata follows the routing as the saved choices stand (a sync read: the access policy is not consulted),
        // so a cache keyed on the model does not hand back an answer from the other provider.
        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceKey is null && serviceType.IsInstanceOfType(this)) return this;
            if (serviceKey is null && serviceType == typeof(ChatClientMetadata)
                && router._providers.TryGetValue(router._routing.ProviderFor(purpose), out var p) && p.IsSignedIn)
                return p.Client.GetService(serviceType);
            return router._configured.GetService(serviceType, serviceKey);
        }

        public void Dispose() { } // shared clients; owned by the container
    }
}
