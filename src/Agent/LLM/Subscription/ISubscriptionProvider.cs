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

    /// <summary>Whether the CLI's program is present where this agent runs (<see cref="CliExecutable"/>). A sign-in
    /// lives on the agent's volume and outlives an image that no longer carries the CLI, so the router asks both.</summary>
    bool IsInstalled { get; }

    IChatClient Client { get; }
}
