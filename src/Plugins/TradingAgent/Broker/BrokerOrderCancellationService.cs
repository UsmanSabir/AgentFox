using AgentFox.Plugins;
using Microsoft.Extensions.Logging;
using TradingAgent.Config;
using TradingAgent.Feed;

namespace TradingAgent.Broker;

public sealed record BrokerCancellationResult(
    bool Gone,
    bool RequestAccepted,
    bool Verified,
    string Message);

/// <summary>
/// Cancels one exact broker order and proves the result against the outstanding book. Both dashboard
/// lifecycle code and agent tools use this service so neither can mistake HTTP acceptance for a
/// completed cancellation.
/// </summary>
public sealed class BrokerOrderCancellationService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly AhkPortalClient _portal;
    private readonly IRuntimePluginOptions<AhkConfig> _config;
    private readonly ILogger<BrokerOrderCancellationService> _logger;

    public BrokerOrderCancellationService(
        AhkPortalClient portal,
        IRuntimePluginOptions<AhkConfig> config,
        ILogger<BrokerOrderCancellationService> logger)
    {
        _portal = portal;
        _config = config;
        _logger = logger;
    }

    public async Task<BrokerCancellationResult> CancelExactAsync(
        string orderNo, CancellationToken ct = default)
    {
        orderNo = orderNo.Trim();
        if (orderNo.Length == 0)
            return new(false, false, false, "No broker order number was supplied.");

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
