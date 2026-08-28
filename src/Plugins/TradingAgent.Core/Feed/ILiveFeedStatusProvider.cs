namespace TradingAgent.Feed;

/// <summary>
/// Edition-neutral health surface for the preferred broker quote feed. Core supplies the AHK
/// fallback directly; an edition can register one or more providers without replacing the existing
/// <c>/trading/feed/status</c> or <c>/trading/activity</c> routes.
/// </summary>
public interface ILiveFeedStatusProvider
{
    /// <summary>Higher wins when more than one enabled feed is available.</summary>
    int Priority { get; }

    LiveFeedStatusSnapshot GetStatus();
}

/// <summary>The small provider-neutral contract consumed by shared UI.</summary>
public sealed record LiveFeedHealth(
    string Provider,
    bool Enabled,
    bool Healthy,
    bool Degraded,
    string State,
    string Reason);

/// <summary>
/// Health plus the provider's full diagnostic payload. Keeping details provider-owned preserves the
/// established AHK status response while allowing Premium to expose AHL-specific connection facts.
/// </summary>
public sealed record LiveFeedStatusSnapshot(LiveFeedHealth Health, object Details);
