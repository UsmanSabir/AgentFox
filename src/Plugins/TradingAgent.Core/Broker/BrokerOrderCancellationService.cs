using AgentFox.Plugins;
using Microsoft.Extensions.Logging;
using TradingAgent.Config;
using TradingAgent.Feed;

namespace TradingAgent.Broker;

public sealed record BrokerCancellationResult(
    bool Gone,
    bool RequestAccepted,
    bool Verified,
    string Message)
{
    /// <summary>
    /// The order is not resting, and THIS SYSTEM did not take it off. Something else did, and what
    /// that was is unknown — a fill is one of the possibilities.
    ///
    /// <para>
    /// <b>Why the distinction has to exist.</b> <see cref="Gone"/> answers "is it resting", which is
    /// all a cancel's caller usually needs, and it is deliberately true for an order the broker
    /// reports it has never heard of — see the AHL adapter's <c>ConfirmsNoSuchOrder</c>, and the
    /// phantom-stop defect that behaviour fixed on 2026-09-09. But an order can stop resting by
    /// FILLING, and then "gone" and "cancelled" are opposite outcomes wearing the same flag.
    /// </para>
    ///
    /// <para>
    /// <b>MEASURED 2026-09-21 on MWMP.</b> A BUY of 52 shares filled at 11:54:29; a cancel of that
    /// exact order number went out at 11:54:56 and came back
    /// <c>Invalid Order[0010TLON5W00P8CB] to cancel</c> — correctly, because it was no longer resting.
    /// A caller reading only <see cref="Gone"/> concludes the order was cancelled and the shares were
    /// never bought. They were.
    /// </para>
    ///
    /// <para>
    /// Derived rather than passed in, so it cannot be set inconsistently with the two flags it is a
    /// statement about, and so no adapter has to be taught to populate it.
    /// </para>
    /// </summary>
    public bool GoneUncancelled => Gone && !RequestAccepted;
}

/// <summary>
/// Cancels one exact broker order and proves the result against the outstanding book. Both dashboard
/// lifecycle code and agent tools use this service so neither can mistake HTTP acceptance for a
/// completed cancellation.
///
/// <para>
/// <b>It uses whichever broker the deployment actually configured.</b> When an
/// <see cref="IBrokerNativeOrderCanceller"/> is registered — an edition that speaks the broker's own
/// order protocol rather than driving its website — the cancel is delegated to it wholesale, because such
/// an adapter verifies its own result from the broker's reply and re-proving it against a website would
/// be both slower and less certain. Only when nothing is registered does this fall back to the browser
/// portal, which is the community path and is unchanged.
/// </para>
///
/// <para>
/// <b>It must be the NARROW interface.</b> <see cref="IBrokerNativeOrderCanceller"/> rather than
/// <see cref="IBrokerOrderCanceller"/>, because <see cref="AhkBrowserBrokerAdapter"/> implements the
/// latter by delegating straight back here — see that interface's own remarks for the
/// <c>StackOverflowException</c> that results.
/// </para>
///
/// <para>
/// <b>The bug this fixes, observed live 2026-09-01.</b> This class took
/// <see cref="AhkPortalClient"/> directly and never consulted the interface, so a deployment whose
/// <c>IBrokerOrderCanceller</c> and <c>IBrokerAdapter</c> were both a socket-protocol adapter STILL
/// launched a visible Chromium window and logged into the broker's website to cancel a persistent
/// order — then timed out after 180 seconds and restarted the browser. The registration was correct and
/// simply unread.
/// </para>
/// </summary>
public sealed class BrokerOrderCancellationService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly AhkPortalClient _portal;
    private readonly IBrokerNativeOrderCanceller? _canceller;
    private readonly IRuntimePluginOptions<AhkConfig> _config;
    private readonly ILogger<BrokerOrderCancellationService> _logger;

    /// <param name="canceller">
    /// The configured broker's own cancel path, when the deployment has one. Optional so the community
    /// edition, which has only the portal, is unaffected.
    /// </param>
    public BrokerOrderCancellationService(
        AhkPortalClient portal,
        IRuntimePluginOptions<AhkConfig> config,
        ILogger<BrokerOrderCancellationService> logger,
        IBrokerNativeOrderCanceller? canceller = null)
    {
        _portal = portal;
        _config = config;
        _logger = logger;
        _canceller = canceller;
    }

    public async Task<BrokerCancellationResult> CancelExactAsync(
        string orderNo, CancellationToken ct = default)
    {
        orderNo = orderNo.Trim();
        if (orderNo.Length == 0)
            return new(false, false, false, "No broker order number was supplied.");

        if (_canceller is not null)
        {
            // Delegated whole, verification included. An adapter that speaks the broker's order protocol
            // learns the outcome from the broker's own reply to THIS cancel; polling a website afterwards
            // would answer a different, weaker question.
            _logger.LogInformation(
                "[CancelOrder] Cancelling order {OrderNo} through the configured broker adapter.", orderNo);
            return await _canceller.CancelOrderAsync(orderNo, ct);
        }

        var before = await _portal.GetOutstandingAsync(ct: ct);
        if (!before.Ok)
            return new(false, false, false,
                $"The outstanding book could not be read: {before.Error} Nothing was cancelled.");

        if (!before.Orders.Any(o => string.Equals(
                o.OrderNo?.Trim(), orderNo, StringComparison.OrdinalIgnoreCase)))
        {
            return new(true, false, true,
                $"Order #{orderNo} is already absent from the broker's outstanding book.");
        }

        if (!await _portal.CancelOrderAsync(orderNo, ct))
            return new(false, false, false,
                $"The broker rejected the cancel request for order #{orderNo}.");

        var timeout = TimeSpan.FromMilliseconds(
            Math.Max(2_000, _config.Current.CancelVerifyTimeoutMs));
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            await Task.Delay(PollInterval, ct);
            var read = await _portal.GetOutstandingAsync(ct: ct);
            if (!read.Ok) continue;

            if (!read.Orders.Any(o => string.Equals(
                    o.OrderNo?.Trim(), orderNo, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation(
                    "[CancelOrder] Confirmed order {OrderNo} left the outstanding book.", orderNo);
                return new(true, true, true,
                    $"Order #{orderNo} was cancelled and is no longer outstanding.");
            }
        }
        while (DateTime.UtcNow < deadline);

        _logger.LogWarning(
            "[CancelOrder] Order {OrderNo} remained outstanding after a verified cancel wait.", orderNo);
        return new(false, true, false,
            $"The cancel for order #{orderNo} was accepted, but it is still outstanding. Its state is unknown.");
    }
}
