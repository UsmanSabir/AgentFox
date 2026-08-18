using AgentFox.Plugins;
using Microsoft.Extensions.Logging;
using TradingAgent.Config;
using TradingAgent.Feed;

namespace TradingAgent.Market;

/// <summary>Whether an order may be submitted right now, and the reason when it may not.</summary>
public readonly record struct OrderWindowDecision(bool Allowed, string Reason, string Source);

/// <summary>
/// Decides whether the venue is currently accepting orders.
///
/// <para>
/// <b>Why this is not just <see cref="IMarketCalendar"/>.</b> The calendar knows one thing: the
/// regular matching session (09:32–15:30 PKT). Gating order submission on that alone conflates two
/// very different states — a market that is genuinely shut, and a market that is accepting orders
/// into the queue but not yet matching them. PSX's pre-open <c>OHO</c> state is the second kind:
/// the broker accepts the order and it goes live at the open. Its own portal renders OHO in the same
/// green "success" style as OPEN, and nothing in the portal's client disables the order form for it.
/// </para>
///
/// <para>
/// Blocking OHO therefore forfeited something real — queue priority at the open is worth having, and
/// it is exactly when an overnight signal wants to be acted on. The original gate was a bare
/// <c>if (!market.IsOpen) reject</c> carrying no comment and no recorded rationale.
/// </para>
///
/// <para>
/// <b>The venue outranks the clock.</b> When the broker feed is running it reports the venue's own
/// market status, which is authoritative in a way a hardcoded schedule can never be: it knows about
/// trading halts, extended sessions and unscheduled closures. So that is consulted first, and the
/// calendar is the fallback for when the feed is switched off or has not answered yet.
/// </para>
///
/// <para>
/// The gate is NOT removed, because it protects against something real: submitting into a genuinely
/// closed market, where this portal returns HTTP 200 with a green success alert while placing nothing
/// at all (see <see cref="AhkConfig.VerifyOrderInBook"/>). An order that vanishes silently is far
/// worse than one that is refused.
/// </para>
/// </summary>
public sealed class OrderWindow
{
    /// <summary>
    /// Broker-reported states that accept orders when configuration supplies none. Covers both of the
    /// portal's vocabularies: <c>GetFeed.marketStatus</c> uses OPEN/CLOSED/OHO, while
    /// <c>GetMarketStates[].state</c> uses OPN/CLO/OHO/Close.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAcceptingStates = ["OPEN", "OPN", "OHO"];

    private readonly IMarketCalendar _calendar;
    private readonly IBrokerMarketState _portal;
    private readonly IRuntimePluginOptions<AhkConfig> _config;
    private readonly ILogger<OrderWindow> _logger;

    public OrderWindow(
        IMarketCalendar calendar,
        IBrokerMarketState portal,
        IRuntimePluginOptions<AhkConfig> config,
        ILogger<OrderWindow> logger)
    {
        _calendar = calendar;
        _portal = portal;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// True when an order may be submitted. Prefers the broker's reported state; falls back to the
    /// trading calendar when the broker has not reported one.
    /// </summary>
    public OrderWindowDecision Evaluate()
    {
        var cfg = _config.Current;

        if (!cfg.TrustBrokerMarketState)
            return FromCalendar("broker market state is not trusted (Ahk.TrustBrokerMarketState = false)");

        var reported = _portal.LastMarketStatus?.Trim();
        if (string.IsNullOrEmpty(reported))
        {
            // No feed running, or it has not polled yet. The calendar is all we have.
            return FromCalendar("the broker has not reported a market state");
        }

        // Empty means "use the defaults", not "accept nothing" — see AhkConfig for why the
        // configured list must not be pre-populated.
        var configured = (cfg.OrderAcceptingMarketStates ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var accepting = configured.Count > 0 ? configured : DefaultAcceptingStates;
        if (accepting.Any(s => string.Equals(s.Trim(), reported, StringComparison.OrdinalIgnoreCase)))
        {
            return new(true,
                $"The broker reports market state '{reported}', which accepts orders.", "broker");
        }

        return new(false,
            $"The broker reports market state '{reported}', which is not in the configured " +
            $"order-accepting states ({string.Join(", ", accepting)}). No order was submitted.",
            "broker");
    }

    private OrderWindowDecision FromCalendar(string why)
    {
        var market = _calendar.GetStatus();
        _logger.LogDebug("[OrderWindow] Falling back to the trading calendar because {Why}.", why);

        return market.IsOpen
            ? new(true, market.Reason, "calendar")
            : new(false, $"{market.Reason} ({why})", "calendar");
    }
}
