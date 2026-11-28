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
    string Version)
{
    private readonly TradingStopMode _tradingStop;

    /// <summary>
    /// The effective trading stop. <see cref="KillSwitch"/> stays the positional field — every existing
    /// reader keeps its meaning, "nothing at all is sent" — and always reads as
    /// <see cref="TradingStopMode.FullStop"/> here, so a snapshot built with only the legacy flag can never
    /// report a weaker stop than it carries.
    /// </summary>
    public TradingStopMode TradingStop
    {
        get => KillSwitch ? TradingStopMode.FullStop : _tradingStop;
        init => _tradingStop = value;
    }

    /// <summary>Whether a BUY must be refused: any stop at all.</summary>
    public bool EntriesBlocked => TradingStop != TradingStopMode.Off;
}

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
        var stop = ResolveTradingStop(runtime, opts);
        var killSwitch = stop == TradingStopMode.FullStop;

        // The stop's FULL mode is hashed, not just the legacy flag: an approval minted while trading was
        // normal must not validate once entries have been switched off, and vice versa.
        var canonical = string.Join('|', auto, mode, confidence, killSwitch,
            opts.AutoPlaceTargetSell, opts.AutoBuyWithoutEntryPrice,
            opts.RetryFailedTakeProfit, opts.TakeProfitRetryIntervalMinutes)
            + (stop == TradingStopMode.EntriesOff ? "|entries-off" : "");
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
            version)
        {
            TradingStop = stop
        };
    }

    /// <summary>
    /// The effective stop. When either runtime key is present the runtime decides, exactly as the legacy
    /// key always overrode the startup kill switch; otherwise the startup options do.
    ///
    /// <para>
    /// <b>The most restrictive runtime key wins, deliberately.</b> More than one thing writes this store:
    /// the <c>/kill-switch</c> endpoint writes both keys, but the chat stop command and the premium
    /// emergency button were written against the legacy <c>killSwitch</c> alone, and the Settings form
    /// writes one field at a time. If <c>tradingStop</c> simply won, a stale <c>tradingStop: Off</c> would
    /// silently cancel an emergency stop pressed later — the one direction this must never fail in. So
    /// <c>killSwitch: true</c> is always FullStop, and resuming means clearing both, which every writer in
    /// this codebase now does.
    /// </para>
    ///
    /// <para>
    /// A <c>tradingStop</c> value that cannot be read counts as EntriesOff: not Off, because a garbled
    /// "stop" must not trade normally, and not FullStop, because that would also stop protective stops on
    /// what may be nothing more than a typo.
    /// </para>
    /// </summary>
    internal static TradingStopMode ResolveTradingStop(
        IReadOnlyDictionary<string, object?> runtime, TradingAgentOptions opts)
    {
        var legacy = GetBool(runtime, "killSwitch");
        var stored = GetString(runtime, "tradingStop");
        var keyed = string.IsNullOrWhiteSpace(stored)
            ? (TradingStopMode?)null
            : TradingStopModes.Parse(stored) ?? TradingStopMode.EntriesOff;

        if (legacy is null && keyed is null)
            return opts.KillSwitch ? TradingStopMode.FullStop : opts.TradingStop;

        var fromLegacy = legacy == true ? TradingStopMode.FullStop : TradingStopMode.Off;
        var fromKey = keyed ?? TradingStopMode.Off;
        return (TradingStopMode)Math.Max((int)fromLegacy, (int)fromKey);
    }

    private static bool? GetBool(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) && value is bool result ? result : null;

    private static string? GetString(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? value as string : null;
}
