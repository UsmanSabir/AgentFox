using System.Globalization;
using System.Text.Json;
using AgentFox.Plugins;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Reconciliation;
using TradingAgent.Research;

namespace TradingAgent.Risk;

/// <summary>One day's mark-to-market result and the value it is measured against. Null fields mean unknown.</summary>
public sealed record DailyResult(decimal? PnlPkr, decimal? BasePkr, string Reason);

/// <summary>
/// The day's result, mark-to-market, from what the broker holds now, what traded today and the prices (plan step 8).
///
/// <para>
/// <b>Not equity, deliberately.</b> A deposit or withdrawal moves equity without anybody trading, and a loss limit that
/// tripped on a withdrawal would switch entries off for nothing. Measured from yesterday's close instead:
/// shares held now earn (last − previous close); a buy today earns (previous close − its price) on top, which with the
/// first term makes it (last − price); a sell today earns (price − previous close). Charges are left out — a known,
/// small understatement of the loss.
/// </para>
///
/// <para>
/// <b>The base</b> is the start-of-day value: holdings at yesterday's close plus cash, with today's trades undone
/// (cash now + bought − sold). With no cash reading the base is holdings alone, which is smaller and so trips sooner —
/// the safe direction.
/// </para>
///
/// <para><b>Unknown is never a number.</b> A held symbol without both prices makes the whole result unknown.</para>
/// </summary>
public static class DailyLoss
{
    public static DailyResult Compute(
        IReadOnlyList<BrokerPosition> positions,
        IReadOnlyList<BrokerFill> todaysFills,
        IReadOnlyDictionary<string, PsxLiveQuote> quotes,
        decimal? cashPkr)
    {
        decimal pnl = 0m, heldAtOpenValue = 0m, bought = 0m, sold = 0m;
        var symbols = positions.Where(p => p.Quantity > 0).Select(p => p.Symbol)
            .Concat(todaysFills.Select(f => f.Symbol)).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in symbols)
        {
            if (!quotes.TryGetValue(symbol, out var q) || q.Current is not > 0 || q.PreviousClose is not > 0)
                return new(null, null, $"{symbol} has no current price and previous close, so the day's result is unknown.");
            decimal last = q.Current.Value, previous = q.PreviousClose.Value;

            var held = positions.Where(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)).Sum(p => p.Quantity);
            var fills = todaysFills.Where(f => f.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)).ToList();
            var buys = fills.Where(f => IsBuy(f)).ToList();
            var sells = fills.Where(f => !IsBuy(f)).ToList();

            pnl += held * (last - previous)
                   + buys.Sum(f => f.Quantity * (previous - f.Price))
                   + sells.Sum(f => f.Quantity * (f.Price - previous));

            var heldAtOpen = held - buys.Sum(f => f.Quantity) + sells.Sum(f => f.Quantity);
            heldAtOpenValue += Math.Max(0m, heldAtOpen) * previous;
            bought += buys.Sum(f => f.Quantity * f.Price);
            sold += sells.Sum(f => f.Quantity * f.Price);
        }

        var basePkr = heldAtOpenValue + (cashPkr is { } cash ? Math.Max(0m, cash + bought - sold) : 0m);
        return new(pnl, basePkr, cashPkr is null
            ? "Measured against holdings at yesterday's close; cash was not readable."
            : "Measured against holdings at yesterday's close plus the day's opening cash.");
    }

    private static bool IsBuy(BrokerFill f) => f.Side?.Trim().StartsWith("B", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Whether the result breaches either limit; a limit of 0 or less is off.</summary>
    public static string? Breach(DailyResult result, decimal limitPct, decimal limitPkr)
    {
        if (result.PnlPkr is not { } pnl || pnl >= 0) return null;
        if (limitPkr > 0 && -pnl >= limitPkr)
            return $"the day's loss of PKR {-pnl:N0} reached the PKR {limitPkr:N0} limit";
        if (limitPct > 0 && result.BasePkr is > 0 && -pnl / result.BasePkr.Value * 100m >= limitPct)
            return $"the day's loss of PKR {-pnl:N0} is {-pnl / result.BasePkr.Value * 100m:0.##}% of the "
                   + $"PKR {result.BasePkr.Value:N0} start-of-day value, past the {limitPct:0.##}% limit";
        return null;
    }
}

/// <summary>
/// Switches ENTRIES off when the day's loss passes the limit, and back on at the next session (plan step 8; owner:
/// 3% on by default, user-overridable, PKR optional, auto-reset next session).
///
/// <para>
/// Only entries: protective stops, take-profits and exits keep working, because a loss limit that also stopped the
/// stops would turn a bad day into an unprotected one. Only ever escalates: an existing FullStop is left alone. The reset
/// clears only a stop this guard set (recorded as <c>tradingStopSetBy</c>); one the owner set themselves stays.
/// </para>
/// </summary>
public sealed class DailyLossGuard : BackgroundService
{
    internal const string SetByKey = "tradingStopSetBy";
    internal const string SetByPrefix = "daily-loss:";

    private readonly IMarketCalendar _calendar;
    private readonly TradingReconciliationState _reconciliation;
    private readonly CompositeLiveQuoteSource _quotes;
    private readonly PluginConfigManager _configs;
    private readonly TradingPolicyProvider _policy;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<DailyLossGuard> _logger;
    private readonly IUserNotifier? _notifier;
    private string? _unknownLoggedFor;

    public DailyLossGuard(IMarketCalendar calendar, TradingReconciliationState reconciliation,
        CompositeLiveQuoteSource quotes, PluginConfigManager configs, TradingPolicyProvider policy,
        IOptions<TradingAgentOptions> options, ILogger<DailyLossGuard> logger, IUserNotifier? notifier = null)
    {
        _calendar = calendar; _reconciliation = reconciliation; _quotes = quotes; _configs = configs;
        _policy = policy; _options = options; _logger = logger; _notifier = notifier;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "[DailyLoss] Check failed; trying again next minute."); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One check: reset a previous session's trip, then measure today. Internal for tests.</summary>
    internal async Task<string> CheckAsync(CancellationToken ct = default)
    {
        var status = _calendar.GetStatus();
        var today = DateOnly.FromDateTime(status.PktNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var runtime = _configs.GetConfig("trading-agent");
        var setBy = runtime.TryGetValue(SetByKey, out var raw) ? raw?.ToString() : null;

        // Auto-reset: a trip from an earlier session, still in force, is cleared at the next one.
        if (setBy is { } by && by.StartsWith(SetByPrefix, StringComparison.Ordinal) && by[SetByPrefix.Length..] != today
            && status.IsOpen && _policy.Current().TradingStop == TradingStopMode.EntriesOff)
        {
            await _configs.MergeConfigAsync("trading-agent", new Dictionary<string, object?>
            {
                ["tradingStop"] = TradingStopMode.Off.ToWire(), ["killSwitch"] = false, [SetByKey] = null
            });
            _logger.LogWarning("[DailyLoss] New session: entries switched back on after yesterday's loss limit.");
            await NotifyAsync("▶️ **New buys are back on**\n• Yesterday's daily loss limit has reset for the new session.");
            return "reset";
        }

        if (!status.IsOpen) return "closed";
        if (setBy == SetByPrefix + today) return "tripped-today";

        var (limitPct, limitPkr) = Limits(runtime);
        if (limitPct <= 0 && limitPkr <= 0) return "off";
        if (_policy.Current().TradingStop != TradingStopMode.Off) return "already-stopped";

        var snapshot = _reconciliation.Current;
        if (!snapshot.Supported || !snapshot.Healthy || DateTime.UtcNow - snapshot.CheckedUtc > TimeSpan.FromMinutes(3))
            return Unknown(today, "the broker account has not been read recently");

        var quotes = (await _quotes.GetQuotesAsync(ct)).Quotes;
        var fills = snapshot.Fills.Where(f => DateOnly.FromDateTime(f.FilledUtc.AddHours(5)).ToString("yyyy-MM-dd") == today).ToList();
        var result = DailyLoss.Compute(snapshot.Positions, fills, quotes, snapshot.AvailableCashPkr);
        if (result.PnlPkr is null) return Unknown(today, result.Reason);

        if (DailyLoss.Breach(result, limitPct, limitPkr) is not { } why) return "within";

        await _configs.MergeConfigAsync("trading-agent", new Dictionary<string, object?>
        {
            ["tradingStop"] = TradingStopMode.EntriesOff.ToWire(), ["killSwitch"] = false, [SetByKey] = SetByPrefix + today
        });
        _logger.LogWarning("[DailyLoss] Entries switched OFF: {Why}.", why);
        await NotifyAsync("⏸️ **Daily loss limit reached — new buys are off**\n"
            + $"• {char.ToUpperInvariant(why[0])}{why[1..]}\n"
            + "• Protective stops, take-profits and sells keep working\n"
            + "_Buying switches back on at the next session, or turn it on yourself in Settings._");
        return "tripped";
    }

    private string Unknown(string today, string why)
    {
        if (_unknownLoggedFor != today + why)
        {
            _unknownLoggedFor = today + why;
            _logger.LogWarning("[DailyLoss] The day's result is unknown ({Why}); nothing is tripped or cleared.", why);
        }
        return "unknown";
    }

    /// <summary>The runtime settings (Settings page) override the startup options.</summary>
    private (decimal Pct, decimal Pkr) Limits(IReadOnlyDictionary<string, object?> runtime) =>
        (Read(runtime, "dailyLossLimitPct") ?? _options.Value.DailyLossLimitPct,
         Read(runtime, "dailyLossLimitPkr") ?? _options.Value.DailyLossLimitPkr);

    private static decimal? Read(IReadOnlyDictionary<string, object?> runtime, string key) =>
        runtime.TryGetValue(key, out var v) ? v switch
        {
            decimal d => d, double d => (decimal)d, long l => l, int i => i,
            JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetDecimal(out var d) => d,
            string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        } : null;

    private async Task NotifyAsync(string message)
    {
        if (_notifier is null) return;
        try { await _notifier.NotifyAsync(message, TradingTopics.DailyLoss); }
        catch (Exception ex) { _logger.LogWarning(ex, "[DailyLoss] Could not deliver the alert."); }
    }
}
