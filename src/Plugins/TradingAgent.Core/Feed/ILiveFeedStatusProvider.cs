namespace TradingAgent.Feed;

/// <summary>
/// Edition-neutral health surface for the preferred broker quote feed. Core supplies the AHK
/// fallback directly; an edition can register one or more providers without replacing the existing
/// <c>/trading/feed/status</c> or <c>/trading/activity</c> routes.
/// </summary>
public interface ILiveFeedStatusProvider
{
    /// <summary>
    /// Higher wins when more than one enabled feed is available — for DISPLAY ORDER ONLY.
    ///
    /// <para>
    /// <b>It does not influence which source a price comes from.</b> This value is read in exactly one
    /// place, ordering the feed status list on <c>/trading/feed/status</c>. Quote selection is
    /// <c>CompositeLiveQuoteSource</c>'s, which merges <see cref="Research.ILiveQuoteSource"/> in DI
    /// REGISTRATION order and has no priority concept at all.
    /// </para>
    ///
    /// <para>
    /// Spelled out because the trap is a natural one: an edition implementing both interfaces reads
    /// this as "how preferred my prices are", sets it high, and changes nothing about the prices
    /// anyone sees.
    /// </para>
    /// </summary>
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
