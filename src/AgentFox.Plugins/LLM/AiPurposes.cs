using Microsoft.Extensions.AI;

namespace AgentFox.LLM;

/// <summary>
/// What a model call is for. The owner can send some purposes to a different provider (Claude Code on their own
/// subscription) while everything else keeps the configured one, so a caller names its purpose rather than taking
/// the shared <see cref="IChatClient"/>.
/// </summary>
public static class AiPurposes
{
    /// <summary>The main chat agent. Routed elsewhere it is text only: no tools.</summary>
    public const string Chat = "chat";

    /// <summary>A stock assessment: text in, JSON out.</summary>
    public const string StockAssessment = "stock-assessment";

    /// <summary>The Rebuy panel's "Ask AI" card: text in, JSON out.</summary>
    public const string RebuyAdvice = "rebuy-advice";
}

/// <summary>Hands out the chat client for a purpose. The routing is decided per call, so a setting changed in the UI
/// applies to the next call without a restart.</summary>
public interface IAiClientRouter
{
    IChatClient For(string purpose);
}

/// <summary>The providers a purpose can be sent to. <see cref="Configured"/> is whatever the LLM section sets.</summary>
public static class AiProviders
{
    public const string Configured = "configured";
    public const string ClaudeCode = "claude-code";
    public const string Codex = "codex";
}

/// <summary>Whether this agent may use a subscription provider (<see cref="AiProviders"/>) at all: a hosted account can
/// be switched off by its operator, per provider.</summary>
public interface ISubscriptionAccessPolicy
{
    Task<SubscriptionAccess> ReadAsync(string provider, CancellationToken cancellationToken = default);
}

/// <param name="Allowed">False keeps every purpose on the configured provider.</param>
/// <param name="Reason">Shown in the settings card when not allowed.</param>
public sealed record SubscriptionAccess(bool Allowed, string? Reason = null);

public sealed class AllowSubscriptionAccessPolicy : ISubscriptionAccessPolicy
{
    public Task<SubscriptionAccess> ReadAsync(string provider, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SubscriptionAccess(true));
}

/// <summary>
/// A provider refused or failed a call in a way its caller should not paper over. <see cref="Retryable"/> false means
/// asking again cannot help and may cost the owner (a refused sign-in, an exhausted subscription); the message is
/// written for the owner and is safe to show.
/// </summary>
public class AiProviderException(string message, bool retryable) : Exception(message)
{
    public bool Retryable { get; } = retryable;
}
