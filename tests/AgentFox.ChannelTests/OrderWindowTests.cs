using AgentFox.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using TradingAgent.Config;
using TradingAgent.Market;

namespace AgentFox.ChannelTests;

/// <summary>
/// The rule deciding whether the venue is accepting orders.
///
/// <para>
/// The case that motivated this: PSX's pre-open <c>OHO</c> state accepts orders which go live at the
/// open, but the original gate tested only the regular matching session and refused them — silently
/// forfeiting queue priority at the open, which is exactly when an overnight signal wants to act.
/// </para>
/// </summary>
[TestClass]
public sealed class OrderWindowTests
{
    [TestMethod]
    public void PreOpenOHO_IsAllowed_BecauseTheBrokerQueuesTheOrderForTheOpen()
    {
        var window = Build(brokerState: "OHO", calendarOpen: false);

        var decision = window.Evaluate();

        Assert.IsTrue(decision.Allowed,
            "OHO accepts orders that go live at the open; refusing it forfeits queue priority.");
        Assert.AreEqual("broker", decision.Source);
    }

    [TestMethod]
    [DataRow("PRE")]
    [DataRow("Pre-Open")]
    [DataRow("PREOPEN")]
    public void TheOtherPreOpenSpellingsAreAllowedToo(string state)
    {
        // AHL names the same phase differently again, and both of these are CONFIRMED readings from
        // 2026-09-02 — PRE from an ORDER_MST push at 09:15:00, Pre-Open from MarketStatus 49 seconds
        // later. One venue phase, three spellings, one answer.
        Assert.IsTrue(Build(state, calendarOpen: false).Evaluate().Allowed,
            $"'{state}' is pre-open order handling, exactly like OHO");
    }

    [TestMethod]
    public void AnUnrecognisedStateIsRefused_NotDeferredToTheCalendar()
    {
        // The asymmetry that makes this list dangerous to get half-right, pinned so it stays a
        // decision rather than a discovery. A state the broker DOES report but this list does not
        // know is refused outright; only the ABSENCE of a reported state falls back to the calendar.
        // So an adapter taught a new token without this list learning it too refuses orders that used
        // to go through — which is exactly how adding PRE to premium's reader alone broke the pre-open
        // window, on a calendar-open day at that.
        var decision = Build("SomeNewPhase", calendarOpen: true).Evaluate();

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("broker", decision.Source,
            "an unrecognised reading is still a reading — it must not be reported as a calendar answer");
    }

    [TestMethod]
    public void BrokerStateOutranksTheCalendar_InBothDirections()
    {
        // Venue open while the local clock says closed — e.g. an extended session.
        Assert.IsTrue(Build("OPEN", calendarOpen: false).Evaluate().Allowed,
            "The venue's own state is authoritative; a hardcoded schedule cannot know about an extended session.");

        // Venue closed while the local clock says open — e.g. an unscheduled halt. This is the
        // direction that actually protects money.
        var halted = Build("CLOSED", calendarOpen: true).Evaluate();
        Assert.IsFalse(halted.Allowed,
            "A halt the calendar cannot know about must still block the order.");
        Assert.AreEqual("broker", halted.Source);
    }

    [TestMethod]
    public void GenuinelyClosedMarket_IsStillRefused()
    {
        // The gate is narrowed, not removed: this portal returns HTTP 200 with a green success alert
        // while placing nothing when the market is shut, so an order submitted then vanishes silently.
        var decision = Build("CLOSED", calendarOpen: false).Evaluate();

        Assert.IsFalse(decision.Allowed);
        StringAssert.Contains(decision.Reason, "CLOSED");
    }

    [TestMethod]
    public void WithNoBrokerState_ItFallsBackToTheCalendar()
    {
        // The feed may be switched off or not yet have polled. Falling back keeps the old behaviour
        // rather than defaulting to permissive.
        Assert.IsFalse(Build(null, calendarOpen: false).Evaluate().Allowed);

        var open = Build(null, calendarOpen: true).Evaluate();
        Assert.IsTrue(open.Allowed);
        Assert.AreEqual("calendar", open.Source);
    }

    [TestMethod]
    public void TrustBrokerMarketState_False_IgnoresTheBrokerEntirely()
    {
        var cfg = new AhkConfig { TrustBrokerMarketState = false };

        Assert.IsFalse(Build("OHO", calendarOpen: false, cfg).Evaluate().Allowed,
            "With the broker distrusted, only the calendar decides.");
        Assert.AreEqual("calendar", Build("OHO", calendarOpen: true, cfg).Evaluate().Source);
    }

    [TestMethod]
    public void EmptyConfiguredStates_MeansDefaults_NotRefuseEverything()
    {
        // Mirrors the AhkFeedConfig.Pages trap: the property is left empty so .NET's
        // ConfigurationBinder cannot append duplicates onto a pre-populated list.
        var cfg = new AhkConfig { OrderAcceptingMarketStates = [] };

        Assert.IsTrue(Build("OHO", calendarOpen: false, cfg).Evaluate().Allowed);
        Assert.IsTrue(Build("OPN", calendarOpen: false, cfg).Evaluate().Allowed);
    }

    [TestMethod]
    public void ConfiguredStates_OverrideTheDefaults()
    {
        var cfg = new AhkConfig { OrderAcceptingMarketStates = ["OPEN"] };

        Assert.IsFalse(Build("OHO", calendarOpen: false, cfg).Evaluate().Allowed,
            "An operator who lists only OPEN must not silently get OHO as well.");
        Assert.IsTrue(Build("OPEN", calendarOpen: false, cfg).Evaluate().Allowed);
    }

    [TestMethod]
    public void StateMatchingIsCaseAndWhitespaceInsensitive()
    {
        // The portal appends \r\n to marketStatus; AhkPortalClient strips it, but casing still varies
        // between the two vocabularies the portal uses.
        Assert.IsTrue(Build("oho", calendarOpen: false).Evaluate().Allowed);
        Assert.IsTrue(Build("  OPEN  ", calendarOpen: false).Evaluate().Allowed);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OrderWindow Build(string? brokerState, bool calendarOpen, AhkConfig? config = null) =>
        new(new StubCalendar(calendarOpen),
            new StubPortalState(brokerState),
            new StubOptions(config ?? new AhkConfig()),
            NullLogger<OrderWindow>.Instance);

    private sealed class StubCalendar(bool isOpen) : IMarketCalendar
    {
        public MarketStatus GetStatus(DateTime? utcNow = null) => new(
            isOpen,
            new DateTime(2026, 8, 18, 9, 5, 0, DateTimeKind.Unspecified),
            isOpen ? "PSX regular market is open." : "PSX regular market is closed.");
    }

    private sealed class StubOptions(AhkConfig value) : IRuntimePluginOptions<AhkConfig>
    {
        public AhkConfig Current => value;
    }

    private sealed class StubPortalState(string? state) : TradingAgent.Feed.IBrokerMarketState
    {
        public string? LastMarketStatus => state;
    }
}
