using AgentFox.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using TradingAgent.Config;
using TradingAgent.Feed;
using TradingAgent.Market;

namespace AgentFox.ChannelTests;

/// <summary>Shared construction helpers for tests that build a <c>TradingManager</c> directly.</summary>
internal static class TradingTestFactory
{
    /// <summary>
    /// An <see cref="OrderWindow"/> that defers entirely to <paramref name="calendar"/>.
    ///
    /// <para>
    /// The stub broker reports no market state, so the window falls back to the calendar — which is
    /// exactly the behaviour these tests were written against before the order gate learned to prefer
    /// the venue's own state. Tests of the gate itself live in <c>OrderWindowTests</c>.
    /// </para>
    /// </summary>
    public static OrderWindow CalendarOnlyWindow(IMarketCalendar calendar) => new(
        calendar,
        new NoBrokerState(),
        new FixedAhkOptions(new AhkConfig()),
        NullLogger<OrderWindow>.Instance);

    private sealed class NoBrokerState : IBrokerMarketState
    {
        public string? LastMarketStatus => null;
    }

    private sealed class FixedAhkOptions(AhkConfig value) : IRuntimePluginOptions<AhkConfig>
    {
        public AhkConfig Current => value;
    }
}
