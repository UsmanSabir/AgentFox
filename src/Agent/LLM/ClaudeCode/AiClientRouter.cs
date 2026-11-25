using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentFox.LLM.ClaudeCode;

/// <summary>
/// Sends each purpose to Claude Code when the owner has signed in, switched that purpose on, and this agent is
/// allowed to use it; everything else stays on the configured provider. Decided on every call, so the settings card
/// takes effect at once.
/// </summary>
public sealed class AiClientRouter : IAiClientRouter
{
    private readonly RuntimeModelChatClient _configured;
    private readonly ClaudeCodeChatClient _claude;
    private readonly ClaudeCodeStore _store;
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new(StringComparer.Ordinal);

    public AiClientRouter(RuntimeModelChatClient configured, ClaudeCodeChatClient claude, ClaudeCodeStore store,
        IServiceProvider services)
    {
        _configured = configured;
        _claude = claude;
        _store = store;
        _services = services;
    }

    public IChatClient For(string purpose) => _clients.GetOrAdd(purpose, p => new PurposeChatClient(this, p));

    /// <summary>Whether this purpose goes to Claude Code right now.</summary>
    public async Task<bool> UsesClaudeCodeAsync(string purpose, CancellationToken cancellationToken)
    {
        var settings = _store.Read();
        if (!settings.HasToken || !settings.IsUsedFor(purpose)) return false;
        return (await ReadAccessAsync(cancellationToken)).Allowed;
    }

    /// <summary>Resolved on use: a hosted policy reads the hub, and a constructor edge to it can close a DI cycle.</summary>
    public async Task<ClaudeCodeAccess> ReadAccessAsync(CancellationToken cancellationToken)
    {
        var policy = _services.GetService<IClaudeCodeAccessPolicy>() ?? new AllowClaudeCodeAccessPolicy();
        try
        {
            return await policy.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // Unreadable means not allowed: the configured provider keeps working, so this costs nothing but the
            // routing, where guessing "allowed" would spend a process on an account the operator switched off.
            return new ClaudeCodeAccess(false, "Claude Code access could not be checked.");
        }
    }

    private async Task<IChatClient> ResolveAsync(string purpose, CancellationToken cancellationToken) =>
        await UsesClaudeCodeAsync(purpose, cancellationToken) ? _claude : _configured;

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
                && router._store.Read() is { HasToken: true } s && s.IsUsedFor(purpose))
                return router._claude.GetService(serviceType);
            return router._configured.GetService(serviceType, serviceKey);
        }

        public void Dispose() { } // shared clients; owned by the container
    }
}
