using Microsoft.Extensions.AI;

namespace AgentFox.LLM.Subscription;

/// <summary>A provider on the owner's own subscription, driven through its official CLI (Claude Code, Codex).</summary>
public interface ISubscriptionProvider
{
    /// <summary>One of <see cref="AiProviders"/>.</summary>
    string Id { get; }

    /// <summary>What the routing card calls it.</summary>
    string DisplayName { get; }

    /// <summary>Whether a sign-in is in place. Cheap and synchronous: it is asked on every routed call. It cannot say the
    /// sign-in still WORKS; a refused one is reported by the call that meets it.</summary>
    bool IsSignedIn { get; }

    IChatClient Client { get; }
}
