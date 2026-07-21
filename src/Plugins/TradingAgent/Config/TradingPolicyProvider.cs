using System.Security.Cryptography;
using System.Text;
using AgentFox.Plugins;
using Microsoft.Extensions.Options;

namespace TradingAgent.Config;

public sealed record TradingPolicySnapshot(
    bool AutoExecute,
    string ExecutionMode,
    string MinConfidence,
    bool AutoPlaceTargetSell,
    bool AutoBuyWithoutEntryPrice,
    bool RetryFailedTakeProfit,
    int TakeProfitRetryIntervalMinutes,
    bool KillSwitch,
    string Version);

/// <summary>
/// Single runtime source for values that affect whether an order may execute. Prompt text and
/// deterministic execution consume the same versioned snapshot.
/// </summary>
public sealed class TradingPolicyProvider
{
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly PluginConfigManager _configManager;

    public TradingPolicyProvider(
        IOptions<TradingAgentOptions> options,
        PluginConfigManager configManager)
    {
        _options = options;
        _configManager = configManager;
    }

    public TradingPolicySnapshot Current()
    {
        var opts = _options.Value;
        var runtime = _configManager.GetConfig("trading-agent");

        var auto = GetBool(runtime, "autoExecute") ?? opts.AutoExecute;
        var mode = GetString(runtime, "executionMode") ?? opts.ExecutionMode;
        var confidence = GetString(runtime, "minConfidence") ?? opts.MinConfidence;
        var killSwitch = GetBool(runtime, "killSwitch") ?? opts.KillSwitch;

        var canonical = string.Join('|', auto, mode, confidence, killSwitch,
            opts.AutoPlaceTargetSell, opts.AutoBuyWithoutEntryPrice,
            opts.RetryFailedTakeProfit, opts.TakeProfitRetryIntervalMinutes);
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];

        return new TradingPolicySnapshot(
            auto,
            string.IsNullOrWhiteSpace(mode) ? "Disabled" : mode.Trim(),
            string.IsNullOrWhiteSpace(confidence) ? "HIGH" : confidence.Trim().ToUpperInvariant(),
            opts.AutoPlaceTargetSell,
            opts.AutoBuyWithoutEntryPrice,
            opts.RetryFailedTakeProfit,
            Math.Max(1, opts.TakeProfitRetryIntervalMinutes),
            killSwitch,
            version);
    }

    private static bool? GetBool(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) && value is bool result ? result : null;

    private static string? GetString(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? value as string : null;
}
