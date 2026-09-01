using AgentFox.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using TradingAgent.Broker;
using TradingAgent.Config;
using TradingAgent.Feed;

namespace AgentFox.ChannelTests;

/// <summary>
/// Which broker a cancel actually goes to.
///
/// <para>
/// <c>BrokerOrderCancellationService</c> took <see cref="AhkPortalClient"/> directly and never read the
/// registered <see cref="IBrokerOrderCanceller"/>. Observed live 2026-09-01: a deployment whose broker
/// adapter speaks the venue's own order protocol still launched a visible Chromium window, logged into
/// the broker's website to cancel a persistent order, timed out after 180 seconds and restarted the
/// browser. The registration was correct the whole time and simply unread.
/// </para>
/// </summary>
[TestClass]
public sealed class BrokerCancellationRoutingTests
{
    /// <summary>
    /// Built with NO portal at all — deliberately. <see cref="AhkPortalClient"/> drives a real browser,
    /// so it cannot be stood up here; passing null turns "the portal must not be touched" into a
    /// crash rather than a soft assertion, which is a stronger statement than any stub could make.
    /// </summary>
    private static BrokerOrderCancellationService Build(IBrokerNativeOrderCanceller canceller) =>
        new(portal: null!,
            new StubOptions<AhkConfig>(new AhkConfig()),
            NullLogger<BrokerOrderCancellationService>.Instance,
            canceller);

    [TestMethod]
    public async Task AConfiguredAdapterGetsTheCancel_NotTheBrowserPortal()
    {
        var adapter = new RecordingCanceller(
            new BrokerCancellationResult(true, true, true, "Cancelled by AHL."));

        var result = await Build(adapter).CancelExactAsync("0411XK65");

        Assert.AreEqual("0411XK65", adapter.Asked, "the adapter must be the one asked");
        Assert.IsTrue(result.Gone);
        StringAssert.Contains(result.Message, "Cancelled by AHL.",
            "the adapter's own verified answer is returned, not re-derived from a website");
    }

    [TestMethod]
    public async Task TheAdaptersRefusalIsPassedThroughUnchanged()
    {
        // An adapter that could not verify must not have its answer improved on the way out — "unknown"
        // is a real state and the caller decides what to do with it.
        var adapter = new RecordingCanceller(
            new BrokerCancellationResult(false, true, false, "Accepted but still outstanding."));

        var result = await Build(adapter).CancelExactAsync("0411XK65");

        Assert.IsFalse(result.Gone);
        Assert.IsTrue(result.RequestAccepted);
        Assert.IsFalse(result.Verified);
    }

    [TestMethod]
    public async Task AnEmptyOrderNumberIsRefusedBeforeAnyBrokerIsTouched()
    {
        var adapter = new RecordingCanceller(new BrokerCancellationResult(true, true, true, "unused"));

        var result = await Build(adapter).CancelExactAsync("   ");

        Assert.IsFalse(result.Gone);
        Assert.IsNull(adapter.Asked, "nothing should have been asked of the broker");
    }

    // NOTE: the no-adapter path is not covered here. It reaches AhkPortalClient, which drives a real
    // Chromium session, and a test that stands one up would be testing the browser rather than the
    // routing. What matters for the bug is that a REGISTERED adapter is used, which the tests above pin.

    private sealed class RecordingCanceller(BrokerCancellationResult answer)
        : IBrokerNativeOrderCanceller
    {
        public string? Asked { get; private set; }

        public Task<BrokerCancellationResult> CancelOrderAsync(
            string orderNo, CancellationToken ct = default)
        {
            Asked = orderNo;
            return Task.FromResult(answer);
        }
    }

    private sealed class StubOptions<T>(T value) : IRuntimePluginOptions<T> where T : class
    {
        public T Current => value;
    }
}
